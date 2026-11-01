using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Phase 11: real encrypted append-only audit and authorisation evidence.</summary>
public sealed class Phase11AuditHistoryAcceptanceTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal General=new(
        "TST-AUDIT-GA","audit-general",UserRole.GeneralAdministration,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly BridgePrincipal Admin=new(
        "TST-AUDIT-ADMIN","audit-admin",UserRole.OrdinaryAdministration,
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly BridgePrincipal Supervisor=new(
        "TST-AUDIT-SUP","audit-supervisor",UserRole.Supervisor,
        "cccccccccccccccccccccccccccccccc");

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }
    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }

    [Fact]
    public async Task SupervisorsReadButCannotChangeOrReviewOverlayHistory_AdminsAppendEntireBeforeAfter()
    {
        var created=await new AdminConfigurationService(_db).CreateSiteAsync(
            new JsonObject{["name"]="موقع تدقيق النسخ"},General,CancellationToken.None);
        Assert.True(created.Success,created.UserMessage);
        string siteId=Assert.IsType<JsonObject>(created.Data)["siteId"]!.GetValue<string>();
        var service=new AuditOverlayService(_db);
        var first=await service.QueryAuditLogAsync(new JsonObject
        {
            ["operation"]="SITE_CREATE",["target"]=siteId
        },CancellationToken.None,Supervisor);
        Assert.True(first.Success,first.UserMessage);
        var original=Assert.IsType<JsonObject>(Assert.Single(
            Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(first.Data)["items"])));
        string id=original["auditEventId"]!.GetValue<string>();
        string originalDescription=original["rawDescription"]!.GetValue<string>();
        string originalHash=original["eventHash"]!.GetValue<string>();

        var denied=await service.UpdateAuditPresentationAsync(new JsonObject{
            ["auditEventId"]=id,["expectedOverlayVersion"]=0,
            ["reason"]="محاولة مشرف غير مصرّح بها",["correctedDescription"]="وصف مرفوض",
            ["operationId"]="PHASE11-SUPERVISOR-DENIED"
        },Supervisor,CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Equal(BridgeErrorCodes.Unauthorized,denied.ErrorCode);
        var deniedHide=await service.DeleteAuditPresentationAsync(new JsonObject{
            ["auditEventId"]=id,["expectedOverlayVersion"]=0,
            ["reason"]="محاولة حذف غير مصرح بها",["operationId"]="PHASE11-SUPERVISOR-HIDE"
        },Supervisor,CancellationToken.None);
        Assert.False(deniedHide.Success);
        var privateHistory=await service.QueryAuditPresentationChangesAsync(
            new JsonObject(),CancellationToken.None,Supervisor);
        Assert.False(privateHistory.Success);
        Assert.Equal(BridgeErrorCodes.Unauthorized,privateHistory.ErrorCode);

        var v1=await service.UpdateAuditPresentationAsync(new JsonObject
        {
            ["auditEventId"]=id,["expectedOverlayVersion"]=0,
            ["correctedDescription"]="تم التعديل المعتمد أولاً",
            ["reason"]="توضيح الوصف في السجل دون المساس بالأصل",
            ["operationId"]="PHASE11-VERSION-ONE"
        },Admin,CancellationToken.None);
        Assert.True(v1.Success,v1.UserMessage);
        var afterOne=Assert.IsType<JsonObject>(v1.Data);
        Assert.Equal(originalDescription,
            afterOne["beforeState"]!["displayDescription"]!.GetValue<string>());
        Assert.Equal("تم التعديل المعتمد أولاً",
            afterOne["afterState"]!["displayDescription"]!.GetValue<string>());
        Assert.False(afterOne["afterState"]!["isTombstoned"]!.GetValue<bool>());

        var v2=await service.UpdateAuditPresentationAsync(new JsonObject
        {
            ["auditEventId"]=id,["expectedOverlayVersion"]=1,
            ["correctedDescription"]="تم التعديل المعتمد ثانياً",
            ["reason"]="تعديل ثانٍ يوثق القيمتين وحالة العرض",
            ["operationId"]="PHASE11-VERSION-TWO"
        },General,CancellationToken.None);
        Assert.True(v2.Success,v2.UserMessage);
        Assert.Equal("تم التعديل المعتمد أولاً",
            Assert.IsType<JsonObject>(v2.Data)["beforeState"]!["displayDescription"]!.GetValue<string>());

        var hidden=await service.DeleteAuditPresentationAsync(new JsonObject
        {
            ["auditEventId"]=id,["expectedOverlayVersion"]=2,
            ["reason"]="إخفاء العرض مع إبقاء الأدلة الأصلية",
            ["operationId"]="PHASE11-VERSION-THREE"
        },Admin,CancellationToken.None);
        Assert.True(hidden.Success,hidden.UserMessage);
        var hiddenResult=Assert.IsType<JsonObject>(hidden.Data);
        Assert.Equal("تم التعديل المعتمد ثانياً",
            hiddenResult["beforeState"]!["displayDescription"]!.GetValue<string>());
        Assert.True(hiddenResult["afterState"]!["isTombstoned"]!.GetValue<bool>());

        var details=await service.GetAuditEntryDetailsAsync(
            new JsonObject{["auditEventId"]=id},CancellationToken.None);
        Assert.True(details.Success,details.UserMessage);
        var entry=Assert.IsType<JsonObject>(details.Data);
        Assert.Equal(originalDescription,entry["rawDescription"]!.GetValue<string>());
        Assert.Equal(originalHash,entry["eventHash"]!.GetValue<string>());
        Assert.True(entry["chainValid"]!.GetValue<bool>());
        var overlays=Assert.IsType<JsonArray>(entry["overlays"]);
        Assert.Equal(3,overlays.Count);
        Assert.Equal("تم التعديل المعتمد أولاً",
            overlays[1]!["beforeState"]!["displayDescription"]!.GetValue<string>());
        Assert.Equal("تم التعديل المعتمد ثانياً",
            overlays[2]!["beforeState"]!["displayDescription"]!.GetValue<string>());

        var history=await service.QueryAuditPresentationChangesAsync(
            new JsonObject{["auditEventId"]=id},CancellationToken.None,Admin);
        Assert.True(history.Success,history.UserMessage);
        var changes=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(history.Data)["items"]);
        Assert.Equal(3,changes.Count);
        Assert.Contains(changes,x=>x!["afterState"]!["isTombstoned"]!.GetValue<bool>());
        var hiddenVisible=await service.QueryAuditLogAsync(
            new JsonObject{["operation"]="SITE_CREATE",["target"]=siteId},
            CancellationToken.None,Supervisor);
        Assert.Equal(0L,Assert.IsType<JsonObject>(hiddenVisible.Data)["total"]!.GetValue<long>());
        var privileged=await service.QueryAuditLogAsync(
            new JsonObject{["operation"]="SITE_CREATE",["target"]=siteId,
                ["includeTombstoned"]=true},CancellationToken.None,General);
        Assert.Equal(1L,Assert.IsType<JsonObject>(privileged.Data)["total"]!.GetValue<long>());

        using var connection=_db.CreateOpenConnection();
        using var check=connection.CreateCommand();
        check.CommandText="""
            SELECT OldValueJson,NewValueJson
            FROM AuditEvents WHERE TargetEntityId=$id
              AND OperationCode IN ('UpdateAuditPresentation','DeleteAuditPresentation')
            ORDER BY rowid ASC;
            """;
        check.Parameters.AddWithValue("$id",id);
        using var reader=await check.ExecuteReaderAsync();
        var before=new List<string>();
        var after=new List<string>();
        while(await reader.ReadAsync())
        {
            before.Add(JsonNode.Parse(reader.GetString(0))!["displayDescription"]!.GetValue<string>());
            after.Add(JsonNode.Parse(reader.GetString(1))!["displayDescription"]!.GetValue<string>());
        }
        Assert.Equal(new[]{originalDescription,"تم التعديل المعتمد أولاً",
            "تم التعديل المعتمد ثانياً"},before);
        Assert.Equal(new[]{"تم التعديل المعتمد أولاً","تم التعديل المعتمد ثانياً",
            "تم التعديل المعتمد ثانياً"},after);
    }

    [Fact]
    public async Task EventTimeActorSnapshotsRemainUnchangedAfterAccountAndRosterRename()
    {
        using(var conn=_db.CreateOpenConnection())
        using(var cmd=conn.CreateCommand())
        {
            cmd.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-AUDIT-P1','الاسم الأصلي للمنفذ','الاسم الاصلي للمنفذ',
                    '939311','939311','2026-01-01','2026-01-01T00:00:00Z',1,1,1);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES('TST-AUDIT-P1','الاسم الأصلي للمنفذ','939311','2026-01-01',NULL,
                    'TST-AUDIT-ADMIN','2026-01-01T00:00:00Z','هوية وقت التنفيذ');
                INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
                    CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,
                    FailedLoginCount,CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,RowVersion)
                VALUES('TST-AUDIT-ADMIN','TST-AUDIT-P1','audit-admin','audit-admin',2,
                    'test-hash','test-salt',0,1,0,'TEST','TEST','2026-01-01T00:00:00Z',1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var created=await new AdminConfigurationService(_db).CreateSiteAsync(
            new JsonObject{["name"]="موقع تغيير الاسم"},Admin,CancellationToken.None);
        Assert.True(created.Success,created.UserMessage);
        var audit=new AuditOverlayService(_db);
        var response=await audit.QueryAuditLogAsync(new JsonObject{
            ["operation"]="SITE_CREATE",["target"]="موقع تغيير الاسم"
        },CancellationToken.None,Supervisor);
        var record=Assert.IsType<JsonObject>(Assert.Single(
            Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(response.Data)["items"])));
        string before=record["actorDisplay"]!.GetValue<string>();
        Assert.Equal("audit-admin - الاسم الأصلي للمنفذ",before);

        using(var conn=_db.CreateOpenConnection())
        using(var cmd=conn.CreateCommand())
        {
            cmd.CommandText="""
                UPDATE Users SET Username='renamed-audit-admin',
                    NormalizedUsername='renamed-audit-admin'
                WHERE UserId='TST-AUDIT-ADMIN';
                UPDATE Persons SET CurrentVisibleName='اسم جديد لاحقاً'
                WHERE PersonId='TST-AUDIT-P1';
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var again=await audit.QueryAuditLogAsync(new JsonObject{
            ["operation"]="SITE_CREATE",["target"]="موقع تغيير الاسم"
        },CancellationToken.None,Supervisor);
        var stable=Assert.IsType<JsonObject>(Assert.Single(
            Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(again.Data)["items"])));
        Assert.Equal(before,stable["actorDisplay"]!.GetValue<string>());
        Assert.Equal("الاسم الأصلي للمنفذ",stable["actorRosterFullName"]!.GetValue<string>());
        Assert.False(stable["foundingAccountProvenance"]!.GetValue<bool>());
    }
}
