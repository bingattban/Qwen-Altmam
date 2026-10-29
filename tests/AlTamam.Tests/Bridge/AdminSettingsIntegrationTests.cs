using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
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
    public async Task AuditSearch_FiltersAcrossEntireHistoryBeforePageBoundaries()
    {
        var config=new AdminConfigurationService(_db);
        foreach(var site in new[]{"المحطة الجديدة 100","موقع مختلف تماماً","قاعدة المحطة الجديدة 200"})
        {
            var result=await config.CreateSiteAsync(
                new JsonObject{["name"]=site},Actor,CancellationToken.None);
            Assert.True(result.Success,result.UserMessage);
        }
        var audit=new AuditOverlayService(_db);
        var first=await audit.QueryAuditLogAsync(
            new JsonObject{["term"]="المحطة الجديدة",["page"]=1,["pageSize"]=1},
            CancellationToken.None,Actor);
        Assert.True(first.Success,first.UserMessage);
        var firstData=Assert.IsType<JsonObject>(first.Data);
        Assert.Equal(2L,firstData["total"]!.GetValue<long>());
        Assert.True(firstData["hasNext"]!.GetValue<bool>());
        var firstRow=Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(firstData["items"])));
        Assert.Equal(1L,firstRow["sequence"]!.GetValue<long>());
        Assert.Equal("تم الاعتماد",firstRow["outcome"]!.GetValue<string>());
        Assert.Contains("synthetic-admin",firstRow["actorDisplay"]!.GetValue<string>());
        Assert.NotNull(firstRow["newValue"]);
        var next=await audit.QueryAuditLogAsync(
            new JsonObject{["term"]="المحطة الجديدة",["page"]=2,["pageSize"]=1},
            CancellationToken.None,Actor);
        Assert.True(next.Success,next.UserMessage);
        var second=Assert.IsType<JsonObject>(next.Data);
        Assert.Equal(2L,second["total"]!.GetValue<long>());
        Assert.Single(Assert.IsType<JsonArray>(second["items"]));
        Assert.False(second["hasNext"]!.GetValue<bool>());
        var noMatch=await audit.QueryAuditLogAsync(
            new JsonObject{["term"]="ليس_في_السجل",["page"]=1,["pageSize"]=100},
            CancellationToken.None,Actor);
        Assert.True(noMatch.Success,noMatch.UserMessage);
        Assert.Equal(0L,Assert.IsType<JsonObject>(noMatch.Data)["total"]!.GetValue<long>());
        // Escape LIKE metacharacters: a wildcard must not accidentally
        // reveal every record in the audit table.
        var wildcard=await audit.QueryAuditLogAsync(
            new JsonObject{["term"]="%_%%",["page"]=1,["pageSize"]=100},
            CancellationToken.None,Actor);
        Assert.True(wildcard.Success,wildcard.UserMessage);
        Assert.Equal(0L,Assert.IsType<JsonObject>(wildcard.Data)["total"]!.GetValue<long>());
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
        var audit=new AuditOverlayService(_db);
        var byNumber=await audit.QueryAuditLogAsync(
            new JsonObject{["term"]="4782"},CancellationToken.None,Actor);
        Assert.True(byNumber.Success,byNumber.UserMessage);
        var returned=Assert.IsType<JsonObject>(byNumber.Data);
        Assert.Contains(Assert.IsType<JsonArray>(returned["items"]),
            entry=>entry!["operationCode"]!.GetValue<string>()=="PERSON_CREATE");
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
    public async Task FailedScheduledBackup_IsPersistedDeduplicatedAndVisibleWithoutFalseSuccess()
    {
        var backup=new AutomaticBackupService(_db);
        await backup.RecordFailureAsync("اختبار: القرص غير متاح");
        await backup.RecordFailureAsync("اختبار: القرص غير متاح");
        var overview=await new SettingsReadBackupService(_db)
            .OverviewAsync(Actor,CancellationToken.None);
        var data=Assert.IsType<JsonObject>(overview.Data);
        Assert.Contains("القرص غير متاح",
            data["lastAutoBackupFailure"]!.GetValue<string>());
        Assert.Contains("فشل النسخ التلقائي",
            data["automaticBackupNote"]!.GetValue<string>());
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='AUTO_BACKUP_FAILED';";
        Assert.Equal(1L,Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        cmd.CommandText="SELECT COUNT(*) FROM BackupHistory WHERE VerificationPassed=1;";
        Assert.Equal(0L,Convert.ToInt64(await cmd.ExecuteScalarAsync()));
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
