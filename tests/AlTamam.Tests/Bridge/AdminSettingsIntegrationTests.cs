using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Only synthetic encrypted databases: no production keys or records.</summary>
public sealed class AdminSettingsIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new(
        "TST-ADMIN", "synthetic-admin", UserRole.GeneralAdministration,
        "0123456789abcdef0123456789abcdef");

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    [Fact]
    public async Task SiteCreated_WithImmutableIdHistoryAndAudit()
    {
        var svc = new AdminConfigurationService(_db);
        var first = await svc.CreateSiteAsync(new JsonObject { ["name"] = "موقع التجربة ألف" },
            Actor, CancellationToken.None);
        Assert.True(first.Success, first.UserMessage);
        string id = Assert.IsType<JsonObject>(first.Data)["siteId"]!.GetValue<string>();
        Assert.StartsWith("SITE-",id);
        var duplicate = await svc.CreateSiteAsync(new JsonObject { ["name"] = "موقع التجربة ألف" },
            Actor, CancellationToken.None);
        Assert.False(duplicate.Success);
        using var c = _db.CreateOpenConnection();
        using var check = c.CreateCommand();
        check.CommandText = """
            SELECT COUNT(*) FROM SiteNameHistory WHERE SiteId=$id;
            """;
        check.Parameters.AddWithValue("$id",id);
        Assert.Equal(1L,Convert.ToInt64(check.ExecuteScalar()));
        check.CommandText = "SELECT COUNT(*) FROM AuditEvents WHERE TargetEntityId=$id AND OperationCode='SITE_CREATE';";
        Assert.Equal(1L,Convert.ToInt64(check.ExecuteScalar()));
    }

    [Fact]
    public async Task CustomStatus_RemainsSafeToDeactivateWithAudit()
    {
        var svc = new AdminConfigurationService(_db);
        var created = await svc.CreateStatusAsync(new JsonObject
        {
            ["name"]="حالة تجريبية",["symbol"]="ت",["color"]="#AA55DD"
        }, Actor,CancellationToken.None);
        Assert.True(created.Success,created.UserMessage);
        var data = Assert.IsType<JsonObject>(created.Data);
        string id=data["statusId"]!.GetValue<string>();
        var disabled=await svc.DeactivateStatusAsync(new JsonObject
        {
            ["statusId"]=id,["expectedRevision"]=1L
        },Actor,CancellationToken.None);
        Assert.True(disabled.Success,disabled.UserMessage);
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT ActiveFlag FROM AttendanceStatusDefinitions WHERE StatusId=$id;";
        cmd.Parameters.AddWithValue("$id",id);
        Assert.Equal(0L,Convert.ToInt64(cmd.ExecuteScalar()));
        cmd.CommandText="SELECT COUNT(*) FROM AttendanceStatusNameHistory WHERE StatusId=$id;";
        Assert.Equal(1L,Convert.ToInt64(cmd.ExecuteScalar()));
        cmd.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE TargetEntityId=$id;";
        Assert.Equal(2L,Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task DatabaseBackup_IsEncryptedVerifiedAndRestorableWithKey()
    {
        string backup=_path+".verified-backup";
        await _db.CreateVerifiedEncryptedBackupAsync(backup);
        Assert.True(File.Exists(backup));
        byte[] first=File.ReadAllBytes(backup).Take(16).ToArray();
        Assert.NotEqual("SQLite format 3\0",Encoding.ASCII.GetString(first));
        using var copy=new AlTamamDbContext(backup,SyntheticFixtures.TestKeyHex);
        Assert.True(copy.HasValidIntegrity());
        using var c=copy.CreateOpenConnection();
        using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM SchemaVersions;";
        Assert.True(Convert.ToInt64(q.ExecuteScalar())>=1);
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
        try{File.Delete(_path+".verified-backup");}catch(IOException){}
    }
}
