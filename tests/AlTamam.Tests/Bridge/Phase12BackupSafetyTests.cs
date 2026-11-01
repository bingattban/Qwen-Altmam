using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

[Collection("SQLCipherGlobalPool")]
public sealed class Phase12BackupSafetyTests : IAsyncLifetime, IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"phase12-live-"+Guid.NewGuid().ToString("N"));
    private readonly string _external=Path.Combine(Path.GetTempPath(),"phase12-external-"+Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db=null!;
    private BridgePrincipal _owner=null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_external);
        _db=new AlTamamDbContext(Path.Combine(_root,"live.db"),
            SyntheticFixtures.TestKeyHex,useConnectionPooling:false);
        await _db.InitializeAsync();
        var created=await new BridgeSharedServices(_db).ProvisionFirstAdministratorAsync(
            "phase12-founder","test-password-123",CancellationToken.None);
        Assert.True(created.Success,created.UserMessage);
        _owner=new BridgePrincipal(((JsonObject)created.Data!)["userId"]!.GetValue<string>(),
            "phase12-founder",UserRole.GeneralAdministration,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }
    public Task DisposeAsync()=>Task.CompletedTask;

    private static JsonObject Confirm(JsonObject preview)=>new()
    {
        ["previewToken"]=preview["previewToken"]!.GetValue<string>(),
        ["expectedRevision"]=preview["expectedRevision"]!.GetValue<long>(),
        ["password"]="test-password-123",["confirmation"]="تصفير جميع البيانات",
        ["confirmDeleteLiveData"]=true,["confirmDeleteOwnedBackups"]=true
    };
    [Fact]
    public async Task Reset_RequiresVerifiedInternalRescue_AndPreservesExternalCopy()
    {
        var choice=await new OutputDirectorySettings(_db).SaveAsync(
            _external,_owner,CancellationToken.None);
        Assert.True(choice.Success,choice.UserMessage);
        var backup=await new SettingsReadBackupService(_db)
            .BackupAsync(_owner,CancellationToken.None);
        Assert.True(backup.Success,backup.UserMessage);
        string path=((JsonObject)backup.Data!)["path"]!.GetValue<string>();
        string digest=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var service=new FullDataResetService(_db);
        var preview=(JsonObject)(await service.PreviewAsync(_owner,CancellationToken.None)).Data!;
        await Task.Delay(5150);
        var result=await service.CommitAsync(Confirm(preview),_owner,CancellationToken.None);
        Assert.True(result.Success,result.UserMessage);
        Assert.True(File.Exists(path),"Independent external backups must not be deleted.");
        Assert.Equal(digest,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        string rescue=Assert.Single(Directory.GetFiles(Path.Combine(_root,"backups"),
            "rescue-before-full-reset-*.dbbackup"));
        var inspection=await _db.InspectEncryptedBackupAsync(rescue);
        Assert.True(inspection.UserCount>=1);
        Assert.True(inspection.AuditCount>=1);
        using var connection=_db.CreateOpenConnection();
        using var q=connection.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM Users;";
        Assert.Equal(0L,Convert.ToInt64(q.ExecuteScalar()));
    }
    [Fact]
    public async Task UnavailableRescueDestination_RejectsResetWithoutChangingLiveDatabase()
    {
        File.WriteAllText(Path.Combine(_root,"backups"),"not-a-folder");
        var service=new FullDataResetService(_db);
        var preview=(JsonObject)(await service.PreviewAsync(_owner,CancellationToken.None)).Data!;
        await Task.Delay(5150);
        var result=await service.CommitAsync(Confirm(preview),_owner,CancellationToken.None);
        Assert.False(result.Success);
        using var connection=_db.CreateOpenConnection();
        using var q=connection.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM Users;";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
        Assert.True(_db.HasValidIntegrity());
    }
    public void Dispose()
    {
        _db?.Dispose();
        try{Directory.Delete(_root,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        try{Directory.Delete(_external,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
