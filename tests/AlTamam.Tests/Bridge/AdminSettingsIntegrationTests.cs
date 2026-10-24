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
    public async Task CreateSite_RetrySameOperation_ReusesOriginalIdWithoutSecondAudit()
    {
        var svc=new AdminConfigurationService(_db);
        var payload=new JsonObject{["name"]="المحطة التجريبية",["operationId"]="OPERATION-SITE-000001"};
        var first=await svc.CreateSiteAsync(payload,Actor,CancellationToken.None);
        var replay=await svc.CreateSiteAsync((JsonObject)payload.DeepClone(),Actor,CancellationToken.None);
        Assert.True(first.Success,first.UserMessage);
        Assert.True(replay.Success,replay.UserMessage);
        string original=Assert.IsType<JsonObject>(first.Data)["siteId"]!.GetValue<string>();
        Assert.Equal(original,Assert.IsType<JsonObject>(replay.Data)["siteId"]!.GetValue<string>());
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM SiteNameHistory WHERE SiteId=$id;";
        q.Parameters.AddWithValue("$id",original);
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
        q.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE TargetEntityId=$id AND OperationCode='SITE_CREATE';";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
        q.CommandText="SELECT COUNT(*) FROM MutationReceipts WHERE ActorUserId=$actor AND OperationId=$op;";
        q.Parameters.AddWithValue("$actor",Actor.UserId);
        q.Parameters.AddWithValue("$op","OPERATION-SITE-000001");
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
    }

    [Fact]
    public async Task StatusCreation_DifferentPayloadWithReusedOperationId_IsConflict()
    {
        var svc=new AdminConfigurationService(_db);
        var payload=new JsonObject{["name"]="حالة مؤقتة",["symbol"]="ح",
            ["color"]="#2277DD",["operationId"]="OPERATION-STATUS-000001"};
        var first=await svc.CreateStatusAsync(payload,Actor,CancellationToken.None);
        Assert.True(first.Success,first.UserMessage);
        var replay=await svc.CreateStatusAsync((JsonObject)payload.DeepClone(),Actor,CancellationToken.None);
        Assert.True(replay.Success,replay.UserMessage);
        Assert.Equal(Assert.IsType<JsonObject>(first.Data)["statusId"]!.GetValue<string>(),
            Assert.IsType<JsonObject>(replay.Data)["statusId"]!.GetValue<string>());
        var changed=(JsonObject)payload.DeepClone();
        changed["color"]="#BB5511";
        var conflict=await svc.CreateStatusAsync(changed,Actor,CancellationToken.None);
        Assert.False(conflict.Success);
        Assert.Equal(AlTamam.Contracts.Bridge.BridgeErrorCodes.ConflictStale,conflict.ErrorCode);
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='STATUS_CREATE';";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
    }

    [Fact]
    public async Task CreatePerson_ExactRetry_LeavesOnePermanentIdentifierAndAssignment()
    {
        var svc=new PersonRosterService(_db);
        var payload=new JsonObject{["visibleName"]="فرد تجريبي للتزامن",
            ["serviceNumberA"]="4782",["siteName"]="غير محدد",
            ["operationId"]="OPERATION-PERSON-000001"};
        var first=await svc.CreatePersonAsync(Actor.UserId,Actor.Username,payload,CancellationToken.None);
        Assert.True(first.Success,first.UserMessage);
        var replay=await svc.CreatePersonAsync(Actor.UserId,Actor.Username,
            (JsonObject)payload.DeepClone(),CancellationToken.None);
        Assert.True(replay.Success,replay.UserMessage);
        string original=Assert.IsType<JsonObject>(first.Data)["personId"]!.GetValue<string>();
        Assert.Equal(original,Assert.IsType<JsonObject>(replay.Data)["personId"]!.GetValue<string>());
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM Persons WHERE PersonId=$id;";
        q.Parameters.AddWithValue("$id",original);
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
        q.CommandText="SELECT COUNT(*) FROM PersonAssignments WHERE PersonId=$id;";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
        q.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_CREATE' AND TargetEntityId=$id;";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
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
