using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Backup;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

[Collection("SQLCipherGlobalPool")]
public sealed class Phase12BackupRetentionTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _folder=Path.Combine(Path.GetTempPath(),
        "phase12-retention-"+Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db=null!;
    private BridgePrincipal _owner=null!;
    private readonly Clock _clock=new();
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now=new(2026,10,24,16,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>_now;
        public void NextDay()=>_now=_now.AddDays(1);
    }
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        var created=await new BridgeSharedServices(_db,_clock)
            .ProvisionFirstAdministratorAsync("retention-owner","test-password-123",default);
        Assert.True(created.Success,created.UserMessage);
        _owner=new BridgePrincipal(((JsonObject)created.Data!)["userId"]!.GetValue<string>(),
            "retention-owner",UserRole.GeneralAdministration,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }
    public Task DisposeAsync()=>Task.CompletedTask;

    [Theory]
    [InlineData("AUTO-20261001","AlTamam-Auto-20261001-0123456789abcdef0123456789abcdef.dbbackup",true)]
    [InlineData("AUTO-20261001","AlTamam-Auto-20261002-0123456789abcdef0123456789abcdef.dbbackup",false)]
    [InlineData("AUTO-20261001","AlTamam-20261001-BKP-0123456789abcdef0123456789abcdef.dbbackup",false)]
    [InlineData("AUTO-20261001","AlTamam-Auto-20261001-../../outside.dbbackup",false)]
    public void RetentionOnlyAcceptsCanonicalAutomaticOwnedFiles(
        string id,string name,bool valid)
    {
        Assert.Equal(valid,AutoBackupRetentionPolicy.IsEligible(
            id,name,new DateOnly(2026,11,1),7));
    }

    [Fact]
    public async Task AfterVerifiedNewBackup_RetainsNewestAndIndependentFiles()
    {
        Assert.True((await new OutputDirectorySettings(_db).SaveAsync(
            _folder,_owner,default)).Success);
        var scheduler=new AutomaticBackupService(_db,_clock);
        var invalid=await scheduler.ConfigureAsync(new JsonObject{
            ["enabled"]=true,["backupTime"]="16:00",["retentionDays"]=3651
        },_owner,default);
        Assert.False(invalid.Success);
        var configured=await scheduler.ConfigureAsync(new JsonObject{
            ["enabled"]=true,["backupTime"]="16:00",["retentionDays"]=1
        },_owner,default);
        Assert.True(configured.Success,configured.UserMessage);
        Assert.Equal(1,await scheduler.ReadRetentionDaysAsync());
        Assert.True(await scheduler.RunIfDueAsync());
        string previous=Assert.Single(Directory.GetFiles(_folder,"AlTamam-Auto-*.dbbackup"));
        var manual=await new SettingsReadBackupService(_db)
            .BackupAsync(_owner,default);
        Assert.True(manual.Success,manual.UserMessage);
        string manualPath=((JsonObject)manual.Data!)["path"]!.GetValue<string>();
        string unrelated=Path.Combine(_folder,
            "AlTamam-Auto-20260901-0123456789abcdef0123456789abcdef.dbbackup");
        File.WriteAllText(unrelated,"independent, unregistered file");

        _clock.NextDay();
        Assert.True(await scheduler.RunIfDueAsync());
        Assert.False(File.Exists(previous));
        Assert.True(File.Exists(manualPath));
        Assert.True(File.Exists(unrelated));
        Assert.Single(Directory.GetFiles(_folder,"AlTamam-Auto-20261025-*.dbbackup"));
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='AUTO_BACKUP_RETENTION_REMOVED';";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
    }

    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
        try{Directory.Delete(_folder,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
