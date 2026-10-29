using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Section 02.11/02.13 evidence and date-specific historical reporting.</summary>
public sealed class Section02EvidenceAndReportsTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal General = new("TST-EVID-GA", "test-evid-ga",
        UserRole.GeneralAdministration, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly BridgePrincipal Ordinary = new("TST-EVID-OA", "test-evid-oa",
        UserRole.OrdinaryAdministration, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly BridgePrincipal Supervisor = new("TST-EVID-SUP", "test-evid-sup",
        UserRole.Supervisor, "cccccccccccccccccccccccccccccccc");

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
    private void Sql(string sql)
    {
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private string Scalar(string sql)
    {
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    [Fact]
    public async Task HistoricalDailyAndMonthlyCells_UseTheIdentityAtEachDate()
    {
        Sql("""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
               NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
               OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-HIST-1','الاسم الحديث','الاسم الحديث','5678','5678',
               '2026-01-01','2026-01-01T01:00:00Z',1,1,2);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
              ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-HIST-1','الاسم القديم','1234','2026-01-01','2026-01-16',
              'TST-USER','2026-01-01T01:00:00Z','بداية'),
              ('TST-HIST-1','الاسم الحديث','5678','2026-01-16',NULL,
              'TST-USER','2026-01-16T01:00:00Z','تحديث');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
              ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-HIST-ASSIGN','TST-HIST-1','SITE-UNSPECIFIED','2026-01-01',
              NULL,NULL,'2026-01-01T01:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
              StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
              AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
              Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES('TST-HIST-1','2026-01-15','STS-PRESENT','موجود','م','#38BDF8',
              'TST-HIST-ASSIGN','SITE-UNSPECIFIED','غير محدد','manual','TST-U',
              '2026-01-15T10:00:00Z',1,'TST-HIST-ATT-1'),
              ('TST-HIST-1','2026-01-16','STS-ABSENT','غياب','غ','#EF4444',
              'TST-HIST-ASSIGN','SITE-UNSPECIFIED','غير محدد','manual','TST-U',
              '2026-01-16T10:00:00Z',1,'TST-HIST-ATT-2');
            """);
        var report = await new PersonnelReportService(_db).ReportAsync(
            new JsonObject { ["personId"] = "TST-HIST-1" }, CancellationToken.None);
        Assert.True(report.Success, report.UserMessage);
        var dates = Assert.IsType<JsonArray>(((JsonObject)report.Data!)["attendance"]);
        Assert.Equal("الاسم القديم", dates.Single(x => x!["date"]!.GetValue<string>()=="2026-01-15")!["name"]!.GetValue<string>());
        Assert.Equal("1234", dates.Single(x => x!["date"]!.GetValue<string>()=="2026-01-15")!["serviceNumberA"]!.GetValue<string>());
        Assert.Equal("الاسم الحديث", dates.Single(x => x!["date"]!.GetValue<string>()=="2026-01-16")!["name"]!.GetValue<string>());

        var month = await new DailyMonthlyReadService(_db).MonthlyGridAsync(
            new JsonObject { ["year"] = 2026, ["month"] = 1 }, CancellationToken.None);
        Assert.True(month.Success, month.UserMessage);
        var items = Assert.IsType<JsonArray>(((JsonObject)month.Data!)["items"]);
        var record = Assert.IsType<JsonObject>(items.Single(x => x!["personId"]!.GetValue<string>()=="TST-HIST-1"));
        var daily = Assert.IsType<JsonObject>(record["days"]);
        Assert.Equal("الاسم القديم", daily["2026-01-15"]!["personName"]!.GetValue<string>());
        Assert.Equal("1234", daily["2026-01-15"]!["serviceNumberA"]!.GetValue<string>());
        Assert.Equal("الاسم الحديث", daily["2026-01-16"]!["personName"]!.GetValue<string>());
        Assert.Equal("غير محدد", daily["2026-01-15"]!["siteName"]!.GetValue<string>());
    }


    [Fact]
    public async Task AuditOverlay_ExactRetryPreservesOneVersionAndOneMetaAudit_AndHiddenIsAdminOnly()
    {
        Sql("""
            -- A nonfounding Ordinary Admin MUST own a real active roster link.
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
              CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
              FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-EVID-OA-PERSON','مسؤول اختباري','مسؤول اختباري',
              '9100','9100','2026-01-01','2026-01-01T00:00:00Z',1,1,1);
            INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
              CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,FailedLoginCount,
              CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,RowVersion)
            VALUES('TST-EVID-GA',NULL,'test-evid-ga','test-evid-ga',1,
              'test-hash','test-salt',0,1,0,'USR-SYSTEM','النظام','2026-01-01T00:00:00Z',1),
              ('TST-EVID-OA','TST-EVID-OA-PERSON','test-evid-oa','test-evid-oa',2,
              'test-hash','test-salt',0,1,0,'USR-SYSTEM','النظام','2026-01-01T00:00:00Z',1);
            """);
        await _db.ExecuteInWriteTransactionAsync(async (c,t) =>
        {
            await BridgeAuditWriter.WriteAsync(c,t,TimeProvider.System,
                "TEST_OVERLAY_RETRY","سجل اختبار مراجعة","TST-EVID-GA","test-evid-ga",null,
                "Person","TST-SUBJECT","اختبار",null,null,"الوصف الأصلي غير المعدل",null,
                "2026-01-01","TEST-RETRY-CORR",CancellationToken.None);
            return true;
        });
        string id=Scalar("SELECT AuditEventId FROM AuditEvents WHERE OperationCode='TEST_OVERLAY_RETRY';");
        var svc=new AuditOverlayService(_db);

        var originalEdit=new JsonObject
        {
            ["auditEventId"]=id,["expectedOverlayVersion"]=0,
            ["reason"]="توضيح ظاهر موثق",["correctedDescription"]="تصحيح الوصف الظاهر",
            ["operationId"]="audit-edit-retry-20261025"
        };
        var first=await svc.UpdateAuditPresentationAsync(originalEdit,General,CancellationToken.None);
        Assert.True(first.Success,first.UserMessage);
        var retry=await svc.UpdateAuditPresentationAsync(
            (JsonObject)originalEdit.DeepClone(),General,CancellationToken.None);
        Assert.True(retry.Success,retry.UserMessage);
        Assert.Equal(Assert.IsType<JsonObject>(first.Data).ToJsonString(),
            Assert.IsType<JsonObject>(retry.Data).ToJsonString());
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM AuditLogPresentationEdits;"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='UpdateAuditPresentation';"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM MutationReceipts;"));
        Assert.Equal("الوصف الأصلي غير المعدل",Scalar(
            "SELECT DefaultArabicDescription FROM AuditEvents WHERE OperationCode='TEST_OVERLAY_RETRY';"));

        var altered=(JsonObject)originalEdit.DeepClone();
        altered["correctedDescription"]="نص آخر";
        var collision=await svc.UpdateAuditPresentationAsync(altered,General,CancellationToken.None);
        Assert.False(collision.Success);
        Assert.False((await svc.UpdateAuditPresentationAsync(originalEdit,Supervisor,CancellationToken.None)).Success);
        Assert.False((await svc.UpdateAuditPresentationAsync(new JsonObject{
            ["auditEventId"]=id,["expectedOverlayVersion"]=1,
            ["reason"]="غير مصرح",["correctedDescription"]="غير مصرح"
        },Ordinary,CancellationToken.None)).Success);

        var hide=new JsonObject
        {
            ["auditEventId"]=id,["expectedOverlayVersion"]=1,
            ["reason"]="إخفاء ظاهر بعد المراجعة",["operationId"]="audit-hide-retry-20261025"
        };
        var hideFirst=await svc.DeleteAuditPresentationAsync(hide,General,CancellationToken.None);
        Assert.True(hideFirst.Success,hideFirst.UserMessage);
        Assert.True((await svc.DeleteAuditPresentationAsync(
            (JsonObject)hide.DeepClone(),General,CancellationToken.None)).Success);
        Assert.Equal("2",Scalar("SELECT COUNT(*) FROM AuditLogPresentationEdits;"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='DeleteAuditPresentation';"));
        Assert.Equal("2",Scalar("SELECT COUNT(*) FROM MutationReceipts;"));

        var normal=await svc.QueryAuditLogAsync(new JsonObject
        {
            ["operation"]="TEST_OVERLAY_RETRY"
        },CancellationToken.None,Supervisor);
        Assert.True(normal.Success,normal.UserMessage);
        Assert.Empty(Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(normal.Data)["items"]));

        var bypass=await svc.QueryAuditLogAsync(new JsonObject
        {
            ["operation"]="TEST_OVERLAY_RETRY",["includeTombstoned"]=true
        },CancellationToken.None,Supervisor);
        Assert.False(bypass.Success);
        var adminVisible=await svc.QueryAuditLogAsync(new JsonObject
        {
            ["operation"]="TEST_OVERLAY_RETRY",["includeTombstoned"]=true
        },CancellationToken.None,General);
        Assert.True(adminVisible.Success,adminVisible.UserMessage);
        var hiddenEntries=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(adminVisible.Data)["items"]);
        Assert.Single(hiddenEntries);
        Assert.True(hiddenEntries[0]!["isTombstoned"]!.GetValue<bool>());

        var restore=new JsonObject
        {
            ["auditEventId"]=id,["expectedOverlayVersion"]=2,
            ["reason"]="إعادة الظهور بعد مراجعة المرفقات",
            ["correctedDescription"]="بيان مصحح مع إثبات تاريخ الإخفاء",
            ["operationId"]="audit-restore-retry-20261025"
        };
        Assert.True((await svc.UpdateAuditPresentationAsync(restore,General,CancellationToken.None)).Success);
        Assert.Equal("3",Scalar("SELECT COUNT(*) FROM AuditLogPresentationEdits;"));
        Assert.Equal("الوصف الأصلي غير المعدل",Scalar(
            "SELECT DefaultArabicDescription FROM AuditEvents WHERE OperationCode='TEST_OVERLAY_RETRY';"));
        var details=await svc.GetAuditEntryDetailsAsync(new JsonObject
        {
            ["auditEventId"]=id
        },CancellationToken.None);
        Assert.True(details.Success,details.UserMessage);
        var data=Assert.IsType<JsonObject>(details.Data);
        Assert.True(data["chainValid"]!.GetValue<bool>());
        Assert.Equal(3,Assert.IsType<JsonArray>(data["overlays"]).Count);
        Assert.Equal("3",Scalar("SELECT COUNT(*) FROM MutationReceipts;"));
    }

    [Fact]
    public async Task DisplayOverlay_IsAuthorizedAppendOnlyAndAudited()
    {
        Sql("""
            -- A nonfounding Ordinary Admin MUST own a real active roster link.
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
              CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
              FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-EVID-OA-PERSON','مسؤول اختباري','مسؤول اختباري',
              '9100','9100','2026-01-01','2026-01-01T00:00:00Z',1,1,1);
            INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
              CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,FailedLoginCount,
              CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,RowVersion)
            VALUES('TST-EVID-GA',NULL,'test-evid-ga','test-evid-ga',1,
              'test-hash','test-salt',0,1,0,'USR-SYSTEM','النظام','2026-01-01T00:00:00Z',1),
              ('TST-EVID-OA','TST-EVID-OA-PERSON','test-evid-oa','test-evid-oa',2,
              'test-hash','test-salt',0,1,0,'USR-SYSTEM','النظام','2026-01-01T00:00:00Z',1);
            """);
        await _db.ExecuteInWriteTransactionAsync(async (c, t) =>
        {
            await BridgeAuditWriter.WriteAsync(c, t, TimeProvider.System,
                "TEST_EVIDENCE","قيد اختبار","TST-EVID-GA","test-evid-ga",null,
                "Person","TST-SUBJECT","اختبار",null,null,"الوصف الأصلي",null,
                "2026-01-01","TEST-EVID-CORR",CancellationToken.None);
            return true;
        });
        string id = Scalar("SELECT AuditEventId FROM AuditEvents WHERE OperationCode='TEST_EVIDENCE';");
        var svc = new AuditOverlayService(_db);
        var rejected = await svc.UpdateAuditPresentationAsync(new JsonObject {
            ["auditEventId"] = id, ["expectedOverlayVersion"] = 0,
            ["reason"] = "سبب اختبار محفوظ", ["correctedDescription"] = "وصف جديد"
        }, Supervisor, CancellationToken.None);
        Assert.False(rejected.Success);
        var edit = await svc.UpdateAuditPresentationAsync(new JsonObject {
            ["auditEventId"] = id, ["expectedOverlayVersion"] = 0,
            ["reason"] = "سبب اختبار محفوظ", ["correctedDescription"] = "الوصف الظاهر المعدل"
        }, General, CancellationToken.None);
        Assert.True(edit.Success, edit.UserMessage);
        var stale = await svc.DeleteAuditPresentationAsync(new JsonObject {
            ["auditEventId"] = id, ["expectedOverlayVersion"] = 0, ["reason"] = "طلب إخفاء قديم"
        }, Ordinary, CancellationToken.None);
        Assert.False(stale.Success);
        var forbiddenHide = await svc.DeleteAuditPresentationAsync(new JsonObject {
            ["auditEventId"] = id, ["expectedOverlayVersion"] = 1, ["reason"] = "طلب الإدارة العادية"
        }, Ordinary, CancellationToken.None);
        Assert.False(forbiddenHide.Success);
        var hide = await svc.DeleteAuditPresentationAsync(new JsonObject {
            ["auditEventId"] = id, ["expectedOverlayVersion"] = 1, ["reason"] = "سبب الإخفاء"
        }, General, CancellationToken.None);
        Assert.True(hide.Success, hide.UserMessage);
        Assert.Equal("الوصف الأصلي", Scalar("SELECT DefaultArabicDescription FROM AuditEvents WHERE OperationCode='TEST_EVIDENCE';"));
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM AuditLogPresentationEdits WHERE AuditEventId=(SELECT AuditEventId FROM AuditEvents WHERE OperationCode='TEST_EVIDENCE');"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='UpdateAuditPresentation';"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='DeleteAuditPresentation';"));
        Assert.Throws<SqliteException>(() => Sql("UPDATE AuditLogPresentationEdits SET Reason='tampered';"));
        Assert.Throws<SqliteException>(() => Sql("DELETE FROM AuditLogPresentationEdits;"));
        Assert.Throws<SqliteException>(() => Sql("DELETE FROM AuditEvents WHERE OperationCode='TEST_EVIDENCE';"));
    }
}
