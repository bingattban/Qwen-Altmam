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
    public async Task DisplayOverlay_IsAuthorizedAppendOnlyAndAudited()
    {
        Sql("""
            INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
              CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,FailedLoginCount,
              CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,RowVersion)
            VALUES('TST-EVID-GA',NULL,'test-evid-ga','test-evid-ga',1,
              'test-hash','test-salt',0,1,0,'USR-SYSTEM','النظام','2026-01-01T00:00:00Z',1),
              ('TST-EVID-OA',NULL,'test-evid-oa','test-evid-oa',2,
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
        var hide = await svc.DeleteAuditPresentationAsync(new JsonObject {
            ["auditEventId"] = id, ["expectedOverlayVersion"] = 1, ["reason"] = "سبب الإخفاء"
        }, Ordinary, CancellationToken.None);
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
