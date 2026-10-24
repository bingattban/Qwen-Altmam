using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Real encrypted integration tests. Never operate on production accounts or files.</summary>
public sealed class ClosedDayCorrectionIntegrationTests : IAsyncLifetime, IDisposable
{
    private const string Day = "2026-10-10";
    private const string Person = "TST-CLOSED-P1";
    private const string OriginalTime = "2026-10-10T09:30:00.0000000Z";
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Ga = new("TST-CLOSED-GA","tst-ga",
        UserRole.GeneralAdministration,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly BridgePrincipal OtherGa = new("TST-CLOSED-GA-OTHER","tst-ga-other",
        UserRole.GeneralAdministration,"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly BridgePrincipal Admin = new("TST-CLOSED-ADMIN","tst-admin",
        UserRole.OrdinaryAdministration,"dddddddddddddddddddddddddddddddd");
    private static readonly BridgePrincipal Supervisor = new("TST-CLOSED-SV","tst-sv",
        UserRole.Supervisor,"cccccccccccccccccccccccccccccccc");

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-CLOSED-P1','شخص اختبار مغلق','شخص اختبار مغلق','C-1','C-1',
                '2026-10-01','2026-10-01T07:00:00Z',1,1,1);
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-CLOSED-A1','TST-CLOSED-P1','SITE-UNSPECIFIED',
                '2026-10-01',NULL,NULL,'2026-10-01T07:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES('TST-CLOSED-P1','2026-10-10','STS-PRESENT','موجود','م','#38BDF8',
                'TST-CLOSED-A1','SITE-UNSPECIFIED','غير محدد',
                'manual','TST-OLD-ACTOR','2026-10-10T09:30:00.0000000Z',1,'TST-CLOSED-INIT');
            INSERT INTO PeriodClosures(PeriodKey,PeriodType,IsClosed,ClosedAtUtc,
                ClosedByActor,PolicyVersion)
            VALUES('2026-10-10','DAY',1,'2026-10-10T20:00:00Z','TST-OLD-ACTOR','v1');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static JsonObject Request(string status="STS-ABSENT") => new()
    {
        ["personId"]=Person, ["businessDate"]=Day, ["statusId"]=status,
        ["reason"]="تصحيح معتمد بعد مراجعة المستندات الرسمية"
    };

    private async Task Enable()
    {
        var result = await new ClosedDayCorrectionService(_db).SetPolicyAsync(new JsonObject
        {
            ["enabled"]=true, ["reason"]="سماح مؤقت لإصلاح قيد مثبت ومغلق"
        }, Ga, CancellationToken.None);
        Assert.True(result.Success, result.UserMessage);
    }

    private string Stored(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText=sql;
        return Convert.ToString(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)??"";
    }

    [Fact]
    public async Task DisabledByDefaultAndUnauthorizedUsersCannotOverride()
    {
        var service = new ClosedDayCorrectionService(_db);
        var denied = await service.PreviewAsync(Request(), Ga, CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Equal(BridgeErrorCodes.Unauthorized, denied.ErrorCode);
        var changePolicy = await service.SetPolicyAsync(new JsonObject
        {
            ["enabled"]=true, ["reason"]="محاولة إعداد غير مصرح به لتصحيح اليوم"
        }, Supervisor, CancellationToken.None);
        Assert.False(changePolicy.Success);
        Assert.Equal(BridgeErrorCodes.Unauthorized, changePolicy.ErrorCode);
        await Enable();
        var stillDenied = await service.PreviewAsync(Request(), Supervisor, CancellationToken.None);
        Assert.False(stillDenied.Success);
        Assert.Equal(BridgeErrorCodes.Unauthorized, stillDenied.ErrorCode);
        Assert.Equal("موجود", Stored("SELECT StatusNameSnapshot FROM AttendanceRecords"));
    }

    [Fact]
    public async Task CommitPreservesOriginalTimeAndClosure_WritesRevisionAndAudit_RejectsReplay()
    {
        await Enable();
        var svc = new ClosedDayCorrectionService(_db);
        var preview = await svc.PreviewAsync(Request(),Ga,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        var details = Assert.IsType<JsonObject>(preview.Data);
        Assert.Equal("موجود",details["beforeStatusName"]!.GetValue<string>());
        Assert.Equal("غياب",details["afterStatusName"]!.GetValue<string>());
        string token=details["previewToken"]!.GetValue<string>();
        var committed=await svc.CommitAsync(new JsonObject { ["previewToken"]=token },Ga,CancellationToken.None);
        Assert.True(committed.Success,committed.UserMessage);
        Assert.Equal("غياب",Stored("SELECT StatusNameSnapshot FROM AttendanceRecords"));
        Assert.Equal("2",Stored("SELECT RevisionNumber FROM AttendanceRecords"));
        Assert.Equal(OriginalTime,Stored("SELECT CommittedAtUtc FROM AttendanceRecords"));
        Assert.Equal("TST-OLD-ACTOR",Stored("SELECT CreatedByUserId FROM AttendanceRecords"));
        Assert.Equal("1",Stored("SELECT IsClosed FROM PeriodClosures WHERE PeriodKey='2026-10-10'"));
        Assert.Equal("1",Stored("SELECT COUNT(*) FROM AttendanceRevisions"));
        Assert.Equal("general-admin-enabled-one-time-preview",
            Stored("SELECT AuthorizationMethod FROM AttendanceRevisions"));
        Assert.Equal("CLOSED_DAY_CORRECTED",
            Stored("SELECT OperationCode FROM AuditEvents ORDER BY rowid DESC LIMIT 1"));
        var replay=await svc.CommitAsync(new JsonObject { ["previewToken"]=token },Ga,CancellationToken.None);
        Assert.False(replay.Success);
        Assert.Equal(BridgeErrorCodes.ConflictStale,replay.ErrorCode);
        Assert.Equal("1",Stored("SELECT COUNT(*) FROM AttendanceRevisions"));
    }

    [Fact]
    public async Task OrdinaryAdmin_CanCorrectWithReason_AndAuditRetainsOriginal()
    {
        var svc = new ClosedDayCorrectionService(_db);
        var enabled = await svc.SetPolicyAsync(new JsonObject { ["enabled"] = true },
            Admin, CancellationToken.None);
        Assert.True(enabled.Success, enabled.UserMessage);
        var missingReason = Request();
        missingReason["reason"] = "";
        var denied = await svc.PreviewAsync(missingReason, Admin, CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError, denied.ErrorCode);
        Assert.Equal("موجود", Stored("SELECT StatusNameSnapshot FROM AttendanceRecords"));
        var preview = await svc.PreviewAsync(Request(), Admin, CancellationToken.None);
        Assert.True(preview.Success, preview.UserMessage);
        var token = Assert.IsType<JsonObject>(preview.Data)["previewToken"]!.GetValue<string>();
        var done = await svc.CommitAsync(new JsonObject { ["previewToken"] = token },
            Admin, CancellationToken.None);
        Assert.True(done.Success, done.UserMessage);
        Assert.Equal("غياب", Stored("SELECT StatusNameSnapshot FROM AttendanceRecords"));
        Assert.Equal("ordinary-admin-enabled-one-time-preview",
            Stored("SELECT AuthorizationMethod FROM AttendanceRevisions"));
        Assert.Equal("تصحيح معتمد بعد مراجعة المستندات الرسمية",
            Stored("SELECT Reason FROM AttendanceRevisions"));
        Assert.Equal("تصحيح معتمد بعد مراجعة المستندات الرسمية",
            Stored("SELECT Reason FROM AuditEvents WHERE OperationCode='CLOSED_DAY_CORRECTED'"));
        Assert.Equal(OriginalTime, Stored("SELECT CommittedAtUtc FROM AttendanceRecords"));
        Assert.Equal("1", Stored("SELECT IsClosed FROM PeriodClosures WHERE PeriodKey='2026-10-10'"));
        var forbidden = await svc.PreviewAsync(Request(), Supervisor, CancellationToken.None);
        Assert.False(forbidden.Success);
        Assert.Equal(BridgeErrorCodes.Unauthorized, forbidden.ErrorCode);
    }

    [Fact]
    public async Task WrongActorAndDisabledPolicyCannotUseIssuedPreview()
    {
        await Enable();
        var svc = new ClosedDayCorrectionService(_db);
        var preview = await svc.PreviewAsync(Request(),Ga,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        string token = Assert.IsType<JsonObject>(preview.Data)["previewToken"]!.GetValue<string>();
        var wrongActor = await svc.CommitAsync(new JsonObject { ["previewToken"]=token },
            OtherGa,CancellationToken.None);
        Assert.False(wrongActor.Success);
        var disabled = await svc.SetPolicyAsync(new JsonObject
        {
            ["enabled"]=false, ["reason"]="إيقاف السماح الاستثنائي بعد المراجعة"
        },Ga,CancellationToken.None);
        Assert.True(disabled.Success,disabled.UserMessage);
        var rejected = await svc.CommitAsync(new JsonObject { ["previewToken"]=token },
            Ga,CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Equal("موجود",Stored("SELECT StatusNameSnapshot FROM AttendanceRecords"));
        Assert.Equal("0",Stored("SELECT COUNT(*) FROM AttendanceRevisions"));
    }

    [Fact]
    public async Task ConcurrentRevisionBetweenPreviewAndCommitFailsWithoutCorruptingAudit()
    {
        await Enable();
        var svc=new ClosedDayCorrectionService(_db);
        var preview=await svc.PreviewAsync(Request(),Ga,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        string token=Assert.IsType<JsonObject>(preview.Data)["previewToken"]!.GetValue<string>();
        using (var c=_db.CreateOpenConnection())
        using (var cmd=c.CreateCommand())
        {
            cmd.CommandText="UPDATE AttendanceRecords SET RevisionNumber=RevisionNumber+1 WHERE PersonId='TST-CLOSED-P1';";
            await cmd.ExecuteNonQueryAsync();
        }
        var rejected=await svc.CommitAsync(new JsonObject { ["previewToken"]=token },
            Ga,CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Equal(BridgeErrorCodes.ConflictStale,rejected.ErrorCode);
        Assert.Equal("0",Stored("SELECT COUNT(*) FROM AttendanceRevisions"));
        Assert.Equal("موجود",Stored("SELECT StatusNameSnapshot FROM AttendanceRecords"));
    }

    [Fact]
    public async Task TransferDayAndUnclosedDayNeverQualify()
    {
        await Enable();
        var svc=new ClosedDayCorrectionService(_db);
        using (var c=_db.CreateOpenConnection())
        using (var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                UPDATE PeriodClosures SET IsClosed=0 WHERE PeriodKey='2026-10-10';
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var open=await svc.PreviewAsync(Request(),Ga,CancellationToken.None);
        Assert.False(open.Success);
        Assert.Equal(BridgeErrorCodes.HistoricDayLocked,open.ErrorCode);
        using (var c=_db.CreateOpenConnection())
        using (var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                UPDATE PeriodClosures SET IsClosed=1 WHERE PeriodKey='2026-10-10';
                INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,
                    ServiceNumberASnapshot,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                    DestinationExternalName,EffectiveLocalDate,CommittedAtUtc,ActorUserId,IdempotencyKey)
                VALUES('TST-CLOSED-M1','TST-CLOSED-P1','شخص اختبار مغلق','C-1',
                  2,'SITE-UNSPECIFIED','غير محدد','جهة أخرى','2026-10-10',
                  '2026-10-10T12:00:00Z','TST-OLD-ACTOR','TST-CLOSED-MV');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var moved=await svc.PreviewAsync(Request(),Ga,CancellationToken.None);
        Assert.False(moved.Success);
        Assert.Equal(BridgeErrorCodes.MissingData,moved.ErrorCode);
        Assert.Equal("0",Stored("SELECT COUNT(*) FROM AttendanceRevisions"));
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
