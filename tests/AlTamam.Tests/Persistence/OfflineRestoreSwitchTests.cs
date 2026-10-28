using System.Text.Json.Nodes;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>
/// Disposable SQLCipher databases exercise native offline transaction intent,
/// unchanged historical state, crash boundaries and rollback.
/// </summary>
public sealed class OfflineRestoreSwitchTests : IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),
        "altamam-offline-switch-"+Guid.NewGuid().ToString("N"));
    private static readonly string Key=SyntheticFixtures.TestKeyHex;
    private string Live=>Path.Combine(_dir,"altamam.db");
    private string Candidate=>Path.Combine(_dir,"incoming.dbbackup");

    private async Task<StagedRestorePreparation> PreparedAsync()
    {
        Directory.CreateDirectory(_dir);
        using var db=new AlTamamDbContext(Live,Key);
        await db.InitializeAsync();
        await db.CreateVerifiedEncryptedBackupAsync(Candidate);
        using(var connection=db.CreateOpenConnection())
        using(var command=connection.CreateCommand())
        {
            command.CommandText="""
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES('RestoreSentinel','after-backup','2026-10-27T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }
        return await new EncryptedRestoreStagingService(db).PrepareAsync(Candidate);
    }

    private string? ReadSentinel()
    {
        using var db=new AlTamamDbContext(Live,Key);
        using var connection=db.CreateOpenConnection();
        using var command=connection.CreateCommand();
        command.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey='RestoreSentinel';";
        return command.ExecuteScalar()?.ToString();
    }

    [Fact]
    public async Task OfflineActivation_RescuesLatestRecordsAndSwitchesOnlyOneWholeEncryptedDb()
    {
        var stage=await PreparedAsync();
        Assert.Equal("after-backup",ReadSentinel());
        SqliteConnection.ClearAllPools();
        var service=new OfflineRestoreSwitch();
        string rollback=await service.ActivateOfflineAsync(Live,Key,stage);
        Assert.Null(ReadSentinel());
        Assert.True(File.Exists(rollback));
        using(var old=new AlTamamDbContext(rollback,Key))
        using(var connection=old.CreateOpenConnection())
        using(var query=connection.CreateCommand())
        {
            query.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey='RestoreSentinel';";
            Assert.Equal("after-backup",query.ExecuteScalar()?.ToString());
        }
        Assert.Single(Directory.GetFiles(Path.Combine(_dir,"backups"),
            "rescue-at-activation-*.dbbackup"));
        Assert.Equal(OfflineRestoreSwitch.RecoveryOutcome.NothingPending,
            await service.ReconcileAtStartupAsync(Live,Key));
    }

    [Theory]
    [InlineData(OfflineRestoreSwitch.Checkpoint.BeforeReplace,
        OfflineRestoreSwitch.RecoveryOutcome.OriginalRetained)]
    [InlineData(OfflineRestoreSwitch.Checkpoint.AfterReplace,
        OfflineRestoreSwitch.RecoveryOutcome.ReplacementVerified)]
    public async Task SimulatedAbnormalExit_ReconcilesAtStartupWithoutHistoryMixing(
        OfflineRestoreSwitch.Checkpoint stop,
        OfflineRestoreSwitch.RecoveryOutcome expected)
    {
        var stage=await PreparedAsync();
        SqliteConnection.ClearAllPools();
        var service=new OfflineRestoreSwitch();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            service.ActivateOfflineAsync(Live,Key,stage,point=>
            {
                if(point==stop)throw new InvalidOperationException("simulated abrupt termination");
            }));
        SqliteConnection.ClearAllPools();
        Assert.Equal(expected,await service.ReconcileAtStartupAsync(Live,Key));
        Assert.Equal(stop==OfflineRestoreSwitch.Checkpoint.BeforeReplace
            ?"after-backup":null,ReadSentinel());
        Assert.Equal(OfflineRestoreSwitch.RecoveryOutcome.NothingPending,
            await service.ReconcileAtStartupAsync(Live,Key));
    }

    [Fact]
    public async Task ChangedEncryptedStage_IsRejectedWithoutSwitchingLiveDb()
    {
        var stage=await PreparedAsync();
        SqliteConnection.ClearAllPools();
        byte[] bytes=await File.ReadAllBytesAsync(stage.StageFilePath);
        bytes[0]^=0xA5;
        await File.WriteAllBytesAsync(stage.StageFilePath,bytes);
        await Assert.ThrowsAnyAsync<Exception>(()=>
            new OfflineRestoreSwitch().ActivateOfflineAsync(Live,Key,stage));
        Assert.Equal("after-backup",ReadSentinel());
        Assert.False(File.Exists(Path.Combine(_dir,
            ".altamam-restore-activation.pending.json")));
    }


    [Theory]
    [InlineData("rollback-extension")]
    [InlineData("rescue-outside-owned-directory")]
    [InlineData("rescue-different-id")]
    [InlineData("stage-outside-live-directory")]
    public async Task TamperedPendingIntent_RejectsUntrustedPathsWithoutChangingLiveHistory(string mutation)
    {
        var prepared=await PreparedAsync();
        SqliteConnection.ClearAllPools();
        var service=new OfflineRestoreSwitch();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            service.ActivateOfflineAsync(Live,Key,prepared,point=>
            {
                if(point==OfflineRestoreSwitch.Checkpoint.BeforeReplace)
                    throw new InvalidOperationException("synthetic crash before replacement");
            }));
        string journal=Path.Combine(_dir,".altamam-restore-activation.pending.json");
        Assert.True(File.Exists(journal));
        JsonObject state=JsonNode.Parse(await File.ReadAllTextAsync(journal))!.AsObject();
        switch(mutation)
        {
            case "rollback-extension":
                state["Rollback"]=Path.ChangeExtension(state["Rollback"]!.GetValue<string>(),".txt");
                break;
            case "rescue-outside-owned-directory":
                state["Rescue"]=Path.Combine(_dir,Path.GetFileName(state["Rescue"]!.GetValue<string>()));
                break;
            case "rescue-different-id":
                state["Rescue"]=Path.Combine(_dir,"backups",
                    "rescue-at-activation-"+Guid.NewGuid().ToString("N")+".dbbackup");
                break;
            case "stage-outside-live-directory":
                state["Stage"]=Path.Combine(Path.GetTempPath(),
                    Path.GetFileName(prepared.StageFilePath));
                break;
            default:throw new InvalidOperationException("unknown test mutation");
        }
        await File.WriteAllTextAsync(journal,state.ToJsonString());
        SqliteConnection.ClearAllPools();
        await Assert.ThrowsAsync<InvalidDataException>(()=>
            service.ReconcileAtStartupAsync(Live,Key));
        Assert.True(File.Exists(journal)); // no destructive recovery or false success
        Assert.True(File.Exists(prepared.StageFilePath));
        Assert.Equal("after-backup",ReadSentinel());
    }

    [Fact]
    public async Task InvalidOldDigest_CannotClearARealPostReplaceRecoveryIntent()
    {
        var prepared=await PreparedAsync();
        SqliteConnection.ClearAllPools();
        var service=new OfflineRestoreSwitch();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            service.ActivateOfflineAsync(Live,Key,prepared,point=>
            {
                if(point==OfflineRestoreSwitch.Checkpoint.AfterReplace)
                    throw new InvalidOperationException("synthetic crash after replacement");
            }));
        string journal=Path.Combine(_dir,".altamam-restore-activation.pending.json");
        JsonObject state=JsonNode.Parse(await File.ReadAllTextAsync(journal))!.AsObject();
        state["OldDigest"]=new string('Z',64);
        await File.WriteAllTextAsync(journal,state.ToJsonString());
        SqliteConnection.ClearAllPools();
        await Assert.ThrowsAsync<InvalidDataException>(()=>
            service.ReconcileAtStartupAsync(Live,Key));
        Assert.True(File.Exists(journal));
        Assert.Null(ReadSentinel()); // replacement remains unaltered, but NOT approved
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{if(Directory.Exists(_dir))Directory.Delete(_dir,true);}
        catch(IOException){ /* native Windows SQLCipher temp handle */ }
        catch(UnauthorizedAccessException){ /* native Windows SQLCipher temp handle */ }
    }
}
