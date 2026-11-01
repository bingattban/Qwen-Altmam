using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AlTamam.Application.Backup;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Phase 12: real encrypted files, synthetic credentials/data only.</summary>
public sealed class BackupRetentionIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _folder=Path.Combine(Path.GetTempPath(),
        "altamam-backup-retention-"+Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal Admin=new(
        "TST-RETENTION-ADMIN","retention-admin",UserRole.GeneralAdministration,
        "12121212121212121212121212121212");
    private static readonly BridgePrincipal Supervisor=new(
        "TST-RETENTION-SUP","retention-supervisor",UserRole.Supervisor,
        "34343434343434343434343434343434");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex,false);
        await _db.InitializeAsync();
        await SetAsync("PreferredBackupDirectory",_folder);
    }
    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
        try{Directory.Delete(_folder,true);}catch(IOException){}
        catch(UnauthorizedAccessException){}
    }

    private async Task SetAsync(string key,string value)
    {
        await _db.ExecuteInWriteTransactionAsync(async(c,tx)=>
        {
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES($key,$value,'2026-10-01T12:00:00Z')
                ON CONFLICT(SettingKey) DO UPDATE SET SettingValue=excluded.SettingValue;
                """;
            cmd.Parameters.AddWithValue("$key",key);
            cmd.Parameters.AddWithValue("$value",value);
            await cmd.ExecuteNonQueryAsync();
            return 0;
        });
    }

    private async Task<string> AddAutomaticAsync(string day,int order)
    {
        string file=Path.Combine(_folder,"AlTamam-Auto-"+day+"-"+Guid.NewGuid().ToString("N")+".dbbackup");
        await _db.CreateVerifiedEncryptedBackupAsync(file);
        string digest=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)))
            .ToLowerInvariant();
        await _db.ExecuteInWriteTransactionAsync(async(c,tx)=>
        {
            using var insert=c.CreateCommand();insert.Transaction=tx;
            insert.CommandText="""
                INSERT INTO BackupHistory(BackupId,BackupFilePath,Sha256Checksum,
                    SchemaVersion,IsAutomatic,VerificationPassed,KeyProtectionMode,
                    CreatedByUserId,CreatedAtUtc,NotesArabic)
                VALUES($id,$file,$sha,$schema,1,1,'SQLCipher','USR-SYSTEM',$now,'اختبار آمن');
                """;
            insert.Parameters.AddWithValue("$id","AUTO-"+day);
            insert.Parameters.AddWithValue("$file",file);
            insert.Parameters.AddWithValue("$sha",digest);
            insert.Parameters.AddWithValue("$schema",AlTamamDbContext.CurrentSchemaVersion);
            insert.Parameters.AddWithValue("$now",$"2026-10-{order:00}T16:00:00Z");
            await insert.ExecuteNonQueryAsync();
            return 0;
        });
        return file;
    }

    private long Scalar(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();q.CommandText=sql;
        return Convert.ToInt64(q.ExecuteScalar());
    }

    [Fact]
    public async Task DefaultRetentionIsUnlimited_AndSupervisorCannotChangePolicy()
    {
        var policy=new BackupRetentionService(_db);
        Assert.Equal(0,await policy.ReadCountAsync());
        string first=await AddAutomaticAsync("20261020",20);
        string second=await AddAutomaticAsync("20261021",21);
        Assert.Equal(0,await policy.PruneAfterVerifiedAutomaticBackupAsync());
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        var denied=await new AutomaticBackupService(_db).ConfigureAsync(new JsonObject{
            ["enabled"]=false,["backupTime"]="16:00",["retentionCount"]=2
        },Supervisor,CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Equal(0,await policy.ReadCountAsync());
        var invalid=await new AutomaticBackupService(_db).ConfigureAsync(new JsonObject{
            ["enabled"]=false,["backupTime"]="16:00",["retentionCount"]=1
        },Admin,CancellationToken.None);
        Assert.False(invalid.Success);
        Assert.Equal(0,await policy.ReadCountAsync());
    }

    [Fact]
    public async Task ExplicitRetentionPrunesOnlyVerifiedOwnedAutomaticFiles_NeverLastTwo()
    {
        string first=await AddAutomaticAsync("20261020",20);
        string second=await AddAutomaticAsync("20261021",21);
        string third=await AddAutomaticAsync("20261022",22);
        string manual=Path.Combine(_folder,"AlTamam-BKP-"+Guid.NewGuid().ToString("N")+".dbbackup");
        await _db.CreateVerifiedEncryptedBackupAsync(manual);
        string foreign=Path.Combine(_folder,"other-user-data.txt");
        await File.WriteAllTextAsync(foreign,"not application backup data");
        var configured=await new AutomaticBackupService(_db).ConfigureAsync(new JsonObject{
            ["enabled"]=false,["backupTime"]="16:00",["retentionCount"]=2,
            ["operationId"]="RETENTION-TEST-001"
        },Admin,CancellationToken.None);
        Assert.True(configured.Success,configured.UserMessage);
        Assert.Equal(2,await new BackupRetentionService(_db).ReadCountAsync());
        Assert.Equal(1,await new BackupRetentionService(_db)
            .PruneAfterVerifiedAutomaticBackupAsync());
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(third));
        Assert.True(File.Exists(manual));
        Assert.True(File.Exists(foreign));
        Assert.Equal(2L,Scalar("SELECT COUNT(*) FROM BackupHistory WHERE VerificationPassed=1 AND IsAutomatic=1"));
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='AUTO_BACKUP_RETENTION_PRUNED'"));
        Assert.Equal(0,await new BackupRetentionService(_db).PruneAfterVerifiedAutomaticBackupAsync());
        Assert.True((await _db.InspectEncryptedBackupAsync(second)).PeopleCount>=0);
    }

    [Fact]
    public async Task ChangedFolderNeverPrunesCopiesInPreviousFolder()
    {
        string first=await AddAutomaticAsync("20261020",20);
        string second=await AddAutomaticAsync("20261021",21);
        string third=await AddAutomaticAsync("20261022",22);
        await SetAsync("AutoBackupRetentionCount","2");
        string other=Path.Combine(Path.GetTempPath(),
            "altamam-backup-retention-moved-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            await SetAsync("PreferredBackupDirectory",other);
            Assert.Equal(0,await new BackupRetentionService(_db)
                .PruneAfterVerifiedAutomaticBackupAsync());
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.True(File.Exists(third));
        }
        finally{Directory.Delete(other,true);}
    }

    [Fact]
    public async Task TamperedOlderBackupIsNeverDeletedByRetention()
    {
        string tampered=await AddAutomaticAsync("20261019",19);
        await File.AppendAllTextAsync(tampered,"this makes the recorded digest invalid");
        string first=await AddAutomaticAsync("20261020",20);
        string second=await AddAutomaticAsync("20261021",21);
        string third=await AddAutomaticAsync("20261022",22);
        await SetAsync("AutoBackupRetentionCount","2");
        Assert.Equal(1,await new BackupRetentionService(_db)
            .PruneAfterVerifiedAutomaticBackupAsync());
        Assert.True(File.Exists(tampered));
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(third));
    }
}
