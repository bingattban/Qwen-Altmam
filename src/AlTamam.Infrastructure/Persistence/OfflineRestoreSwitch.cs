using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AlTamam.Infrastructure.Persistence;

/// <summary>
/// Native OFFLINE database switching. Not wired to any UI/bridge command.
/// Call only after all application services are stopped and after separate
/// administrator authentication. Uses same-volume File.Replace and durable
/// recovery intent; never deletes pre-restore rescue/rollback images.
/// </summary>
public sealed class OfflineRestoreSwitch
{
    public enum Checkpoint { BeforeReplace, AfterReplace }
    public enum RecoveryOutcome { NothingPending, OriginalRetained, ReplacementVerified, OriginalRecovered }
    private sealed record Intent(string Live,string Stage,string Rollback,string Rescue,
        string OldDigest,string NewDigest);
    private const string JournalName=".altamam-restore-activation.pending.json";
    private static string Journal(string live)
        =>Path.Combine(Path.GetDirectoryName(Path.GetFullPath(live))!,JournalName);

    private static async Task<string> HashAsync(string file,CancellationToken ct)
    {
        await using var f=new FileStream(file,FileMode.Open,FileAccess.Read,
            FileShare.Read,81920,FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(f,ct)
            .ConfigureAwait(false)).ToLowerInvariant();
    }
    private static void ValidateStagePath(string live,string stage)
    {
        if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(live)),
            Path.GetDirectoryName(Path.GetFullPath(stage)),StringComparison.OrdinalIgnoreCase)||
           !Path.GetFileName(stage).StartsWith(".altamam-restore-stage-",StringComparison.Ordinal)||
           !string.Equals(Path.GetExtension(stage),".dbbackup",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The stage must be a verified sibling of the live DB.");
    }
    private static void RequireOffline(string live)
    {
        if(File.Exists(live+"-wal")||File.Exists(live+"-shm")||File.Exists(live+"-journal"))
            throw new IOException("Cannot switch while SQLite WAL/SHM/journal sidecars remain.");
    }
    private static void CheckpointAndQuiesceWal(string live,string key)
    {
        // Native caller holds the single-instance mutex. Fail if a reader or
        // writer still owns the database; do NOT delete WAL bytes manually.
        SqliteConnection.ClearAllPools();
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource=live,Mode=SqliteOpenMode.ReadWrite,Password=key,
            Pooling=false,ForeignKeys=true,DefaultTimeout=15
        }.ConnectionString))
        {
            connection.Open();
            using(var checkpoint=connection.CreateCommand())
            {
                checkpoint.CommandText="PRAGMA wal_checkpoint(TRUNCATE);";
                using var row=checkpoint.ExecuteReader();
                if(!row.Read() || row.GetInt64(0)!=0)
                    throw new IOException("SQLite has an active reader/writer; WAL cannot be checkpointed.");
            }
            using(var journal=connection.CreateCommand())
            {
                journal.CommandText="PRAGMA journal_mode=DELETE;";
                if(!string.Equals(journal.ExecuteScalar()?.ToString(),"delete",
                    StringComparison.OrdinalIgnoreCase))
                    throw new IOException("SQLite refused to leave WAL journal mode.");
            }
        }
        SqliteConnection.ClearAllPools();
        RequireOffline(live);
    }

    private static async Task<bool> ValidExactAsync(string path,string digest,
        string live,string key,CancellationToken ct)
    {
        if(!File.Exists(path))return false;
        if(!string.Equals(await HashAsync(path,ct),digest,StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var result=await EncryptedBackupInspector.VerifyAsync(path,
                Path.Combine(Path.GetDirectoryName(live)!,"unused-not-live.db"),key,ct)
                .ConfigureAwait(false);
            return result.SchemaVersion==AlTamamDbContext.CurrentSchemaVersion;
        }
        catch(Exception ex) when(ex is SqliteException or IOException or
            InvalidDataException or FormatException or InvalidOperationException){return false;}
    }
    private static async Task WriteIntentAsync(string path,Intent intent,CancellationToken ct)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,
                FileShare.None,4096,FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file,intent,cancellationToken:ct)
                    .ConfigureAwait(false);
                file.Flush(flushToDisk:true);
            }
            File.Move(temp,path,overwrite:false);
        }
        finally
        {
            try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}
        }
    }
    private static async Task<Intent> ReadIntentAsync(string live,CancellationToken ct)
    {
        string json=await File.ReadAllTextAsync(Journal(live),ct).ConfigureAwait(false);
        var state=JsonSerializer.Deserialize<Intent>(json)
            ??throw new InvalidDataException("Restore intent is incomplete.");
        if(!string.Equals(Path.GetFullPath(live),Path.GetFullPath(state.Live),
               StringComparison.OrdinalIgnoreCase)||
           !string.Equals(Path.GetDirectoryName(Path.GetFullPath(state.Rollback)),
               Path.GetDirectoryName(Path.GetFullPath(live)),StringComparison.OrdinalIgnoreCase)||
           !Path.GetFileName(state.Rollback).StartsWith(".altamam-restore-rollback-",
               StringComparison.Ordinal)||
           state.OldDigest.Length!=64||state.NewDigest.Length!=64)
            throw new InvalidDataException("Restore intent paths/digests are not trusted.");
        ValidateStagePath(live,state.Stage);
        return state;
    }

    public async Task<string> ActivateOfflineAsync(string live,string key,
        StagedRestorePreparation prepared,Action<Checkpoint>? testCheckpoint=null,
        CancellationToken ct=default)
    {
        live=Path.GetFullPath(live);
        ValidateStagePath(live,prepared.StageFilePath);
        if(File.Exists(Journal(live)))throw new IOException(
            "Previous restore must be reconciled before any new switch.");
        var stage=await EncryptedBackupInspector.VerifyAsync(
            prepared.StageFilePath,live,key,ct).ConfigureAwait(false);
        if(stage.SchemaVersion!=AlTamamDbContext.CurrentSchemaVersion||
           !string.Equals(stage.Sha256,prepared.StageSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid staged restore image.");
        var earlierRescue=await EncryptedBackupInspector.VerifyAsync(
            prepared.RescueFilePath,live,key,ct).ConfigureAwait(false);
        if(!string.Equals(earlierRescue.Sha256,prepared.RescueSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Earlier rescue is altered.");

        string id=Guid.NewGuid().ToString("N");
        string freshRescue=Path.Combine(Path.GetDirectoryName(live)!,"backups",
            "rescue-at-activation-"+id+".dbbackup");
        // An additional WAL-consistent snapshot covers writes since staging.
        using(var db=new AlTamamDbContext(live,key))
            await db.CreateVerifiedEncryptedBackupAsync(freshRescue,ct).ConfigureAwait(false);
        var emergency=await EncryptedBackupInspector.VerifyAsync(
            freshRescue,live,key,ct).ConfigureAwait(false);
        if(emergency.SchemaVersion!=AlTamamDbContext.CurrentSchemaVersion)
            throw new InvalidDataException("Fresh rescue failed verification.");

        CheckpointAndQuiesceWal(live,key);
        var old=await EncryptedBackupInspector.VerifyAsync(
            live,prepared.StageFilePath,key,ct).ConfigureAwait(false);
        if(old.SchemaVersion!=AlTamamDbContext.CurrentSchemaVersion)
            throw new InvalidDataException("Active DB schema is incompatible.");
        RequireOffline(live);
        string rollback=Path.Combine(Path.GetDirectoryName(live)!,
            ".altamam-restore-rollback-"+id+".dbbackup");
        var intent=new Intent(live,Path.GetFullPath(prepared.StageFilePath),
            rollback,freshRescue,old.Sha256,stage.Sha256);
        ct.ThrowIfCancellationRequested();
        await WriteIntentAsync(Journal(live),intent,ct).ConfigureAwait(false);
        // The durable intent must be reconciled after interruption.
        testCheckpoint?.Invoke(Checkpoint.BeforeReplace);
        File.Replace(prepared.StageFilePath,live,rollback);
        testCheckpoint?.Invoke(Checkpoint.AfterReplace);
        if(!await ValidExactAsync(live,intent.NewDigest,live,key,CancellationToken.None)
            .ConfigureAwait(false))
            throw new InvalidDataException("Post-switch verification failed; intent retained.");
        File.Delete(Journal(live));
        return rollback;
    }

    /// <summary>Run under the single-instance lock, BEFORE opening the DB.</summary>
    public async Task<RecoveryOutcome> ReconcileAtStartupAsync(string live,string key,
        CancellationToken ct=default)
    {
        live=Path.GetFullPath(live);
        if(!File.Exists(Journal(live)))return RecoveryOutcome.NothingPending;
        RequireOffline(live);
        var state=await ReadIntentAsync(live,ct).ConfigureAwait(false);
        if(await ValidExactAsync(live,state.NewDigest,live,key,ct).ConfigureAwait(false))
        {
            File.Delete(Journal(live));
            return RecoveryOutcome.ReplacementVerified;
        }
        if(await ValidExactAsync(live,state.OldDigest,live,key,ct).ConfigureAwait(false))
        {
            File.Delete(Journal(live));
            return RecoveryOutcome.OriginalRetained;
        }
        if(!await ValidExactAsync(state.Rollback,state.OldDigest,live,key,ct)
            .ConfigureAwait(false))
            throw new InvalidDataException(
                "Unknown DB state without a valid rollback; no overwrite performed.");
        if(!File.Exists(live))File.Copy(state.Rollback,live,overwrite:false);
        else File.Replace(state.Rollback,live,
            live+".failed-restore-"+Guid.NewGuid().ToString("N"));
        if(!await ValidExactAsync(live,state.OldDigest,live,key,ct).ConfigureAwait(false))
            throw new InvalidDataException("Rollback verification failed; intent retained.");
        File.Delete(Journal(live));
        return RecoveryOutcome.OriginalRecovered;
    }
}
