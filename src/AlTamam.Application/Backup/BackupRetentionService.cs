using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Backup;

/// <summary>
/// Explicit opt-in retention for the scheduler's OWN automatic backups only.
/// Never removes manual, rescue, moved, or independently stored backup files.
/// A new authenticated encrypted backup must already have committed before pruning.
/// Retains at least TWO verified automatic files in the currently selected folder;
/// zero means retain everything. The registry and immutable audit evidence survive.
/// </summary>
public sealed class BackupRetentionService
{
    private static readonly Regex AutoName = new(
        @"^AlTamam-Auto-\d{8}-[a-fA-F0-9]{32}\.dbbackup$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    public const int MaximumCount = 365;

    public BackupRetentionService(AlTamamDbContext db, TimeProvider? time=null)
    {
        _db=db;
        _time=time??TimeProvider.System;
    }

    public static bool IsValidCount(int count) =>
        count==0 || count is >=2 and <=MaximumCount;

    public async Task<int> ReadCountAsync(CancellationToken ct=default)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey='AutoBackupRetentionCount';";
        string? value=(await q.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
        return int.TryParse(value,out int count) && IsValidCount(count)?count:0;
    }

    private sealed record Entry(string Id,string File,string Hash);

    /// <returns>The count of removed, fully verified, automatic files.
    /// Failures in pruning never invalidate a successfully committed backup.</returns>
    public async Task<int> PruneAfterVerifiedAutomaticBackupAsync(CancellationToken ct=default)
    {
        int keep=await ReadCountAsync(ct).ConfigureAwait(false);
        if(keep==0)return 0;
        string folder=await new OutputDirectorySettings(_db).GetAsync(ct).ConfigureAwait(false);
        if(!OutputDirectorySettings.IsSafeDirectory(folder,_db.DatabasePath,out string canonicalFolder))
            return 0;

        var catalog=new List<Entry>();
        using(var c=_db.CreateOpenConnection())
        using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                SELECT BackupId,BackupFilePath,Sha256Checksum FROM BackupHistory
                WHERE IsAutomatic=1 AND VerificationPassed=1 AND BackupId LIKE 'AUTO-%'
                ORDER BY CreatedAtUtc DESC, BackupId DESC LIMIT 512;
                """;
            using var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await reader.ReadAsync(ct).ConfigureAwait(false))
                catalog.Add(new Entry(reader.GetString(0),reader.GetString(1),reader.GetString(2)));
        }

        var verified=new List<Entry>();
        foreach(var entry in catalog)
        {
            ct.ThrowIfCancellationRequested();
            if(!IsOwnedInFolder(entry.File,canonicalFolder))continue;
            if(await MatchesShaAsync(entry.File,entry.Hash,ct).ConfigureAwait(false))
                verified.Add(entry);
        }
        // Do not touch anything unless enough healthy copies really exist.
        if(verified.Count<=keep)return 0;
        int removed=0;
        foreach(var old in verified.Skip(keep))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Revalidate immediately before deleting: the path could have
                // been removed, replaced or modified since the initial scan.
                if(!IsOwnedInFolder(old.File,canonicalFolder) ||
                   !await MatchesShaAsync(old.File,old.Hash,ct).ConfigureAwait(false))
                    continue;
                File.Delete(old.File);
                await _db.ExecuteInWriteTransactionAsync(async(c,tx)=>
                {
                    using var change=c.CreateCommand();
                    change.Transaction=tx;
                    change.CommandText="""
                        UPDATE BackupHistory
                        SET VerificationPassed=0,
                            NotesArabic='انتهت مدة الاحتفاظ؛ أزيل ملف النسخة الآلية بعد التحقق من بصمته'
                        WHERE BackupId=$id AND BackupFilePath=$path
                          AND Sha256Checksum=$hash AND IsAutomatic=1 AND VerificationPassed=1;
                        """;
                    change.Parameters.AddWithValue("$id",old.Id);
                    change.Parameters.AddWithValue("$path",old.File);
                    change.Parameters.AddWithValue("$hash",old.Hash);
                    if(await change.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                        return 0;
                    await BridgeAuditWriter.WriteAsync(c,tx,_time,
                        "AUTO_BACKUP_RETENTION_PRUNED","تنظيف النسخ التلقائية القديمة",
                        "USR-SYSTEM","النظام",null,"Backup",old.Id,
                        Path.GetFileName(old.File),
                        System.Text.Json.JsonSerializer.Serialize(new {sha256=old.Hash,path=old.File}),
                        System.Text.Json.JsonSerializer.Serialize(new {fileRemoved=true}),
                        "إزالة ملف نسخة آلية وفق سياسة الاحتفاظ المعتمدة بعد نجاح نسخة جديدة",
                        null,_time.GetLocalNow().ToString("yyyy-MM-dd"),
                        Guid.NewGuid().ToString("N"),ct).ConfigureAwait(false);
                    return 1;
                },ct).ConfigureAwait(false);
                removed++;
            }
            catch(IOException){ /* disconnected or locked: preserve remaining backups */ }
            catch(UnauthorizedAccessException){ /* no unverified fallback path */ }
        }
        return removed;
    }

    private static bool IsOwnedInFolder(string path,string folder)
    {
        try
        {
            if(!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\",StringComparison.Ordinal) ||
               !AutoName.IsMatch(Path.GetFileName(path)))return false;
            string canonical=Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(canonical),folder,
                StringComparison.OrdinalIgnoreCase);
        }
        catch(ArgumentException){return false;}
        catch(NotSupportedException){return false;}
    }

    private static async Task<bool> MatchesShaAsync(string path,string expected,CancellationToken ct)
    {
        try
        {
            var attributes=File.GetAttributes(path);
            if((attributes & FileAttributes.ReparsePoint)!=0)return false;
            await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,
                FileShare.Read,81920,FileOptions.SequentialScan);
            if(stream.Length<1024 || stream.Length>20L*1024*1024*1024)return false;
            string digest=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)
                .ConfigureAwait(false));
            return string.Equals(digest,expected,StringComparison.OrdinalIgnoreCase);
        }
        catch(IOException){return false;}
        catch(UnauthorizedAccessException){return false;}
    }
}
