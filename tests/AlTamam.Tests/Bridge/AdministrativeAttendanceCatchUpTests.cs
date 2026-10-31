using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class AdministrativeAttendanceCatchUpTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Ga = new("TST-CATCHUP-GA","ga",
        UserRole.GeneralAdministration,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly BridgePrincipal Admin = new("TST-CATCHUP-ADMIN","admin",
        UserRole.OrdinaryAdministration,"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly BridgePrincipal Supervisor = new("TST-CATCHUP-SUPERVISOR","supervisor",
        UserRole.Supervisor,"cccccccccccccccccccccccccccccccc");
    private DateOnly _today;
    private DateOnly Day(int offset) => _today.AddDays(offset);
    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _today = DateOnly.FromDateTime(DateTime.Now);
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
               NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
               OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-7DAY-PERSON','فرد اختبار','فرد اختبار','8675309','8675309',
               $since,'2026-01-01T00:00:00Z',1,1,1);
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
               ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-7DAY-ASSIGN','TST-7DAY-PERSON','SITE-UNSPECIFIED',
               $since,NULL,NULL,'2026-01-01T00:00:00Z');
            INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
            VALUES('AutoAttendanceDefaultStatusId','STS-PRESENT','2026-01-01T00:00:00Z');
            """;
        cmd.Parameters.AddWithValue("$since", Iso(Day(-9)));
        await cmd.ExecuteNonQueryAsync();
    }

    private long Count(string table)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static JsonObject Input(DateOnly date) => new()
    {
        ["businessDate"] = Iso(date),
        ["reason"] = "استدراك موثق لحالة تحضير ناقصة"
    };

    [Fact]
    public async Task OnlyBothAdministrationsCanPreviewOneOfSevenPreviousDays()
    {
        var svc = new AdministrativeAttendanceCatchUpService(_db);
        Assert.Equal(BridgeErrorCodes.Unauthorized,
            (await svc.PreviewAsync(Input(Day(-1)),Supervisor,default)).ErrorCode);
        foreach (int offset in new[] {0,-8,1})
            Assert.Equal(BridgeErrorCodes.ValidationError,
                (await svc.PreviewAsync(Input(Day(offset)),Ga,default)).ErrorCode);
        Assert.True((await svc.PreviewAsync(Input(Day(-7)),Ga,default)).Success);
        Assert.True((await svc.PreviewAsync(Input(Day(-1)),Admin,default)).Success);
        Assert.Equal(0,Count("AutoAttendanceRuns"));
        Assert.Equal(0,Count("AttendanceRecords"));
    }

    [Fact]
    public async Task PreviewConfirmCommitsOnlyMissingRecords_WithAuditAndExactRetry()
    {
        var svc = new AdministrativeAttendanceCatchUpService(_db);
        var date = Day(-2);
        var before = await svc.PreviewAsync(Input(date),Admin,default);
        Assert.True(before.Success,before.UserMessage);
        var preview = Assert.IsType<JsonObject>(before.Data);
        Assert.Equal(1, preview["pendingCount"]!.GetValue<int>());
        var commitPayload = new JsonObject
        {
            ["previewToken"] = preview["previewToken"]!.GetValue<string>(),
            ["operationId"] = "TST-CATCHUP-RETRY-00001"
        };
        Assert.Equal(BridgeErrorCodes.Unauthorized,
            (await svc.CommitAsync((JsonObject)commitPayload.DeepClone(),Supervisor,default)).ErrorCode);
        var done = await svc.CommitAsync(commitPayload,Admin,default);
        Assert.True(done.Success,done.UserMessage);
        Assert.Equal(1,Assert.IsType<JsonObject>(done.Data)["appliedCount"]!.GetValue<int>());
        Assert.Equal(1,Count("AttendanceRecords"));
        Assert.Equal(1,Count("AutoAttendanceRuns"));
        Assert.Equal(2,Count("AuditEvents")); // automated event and named admin approval
        var retry = await svc.CommitAsync((JsonObject)commitPayload.DeepClone(),Admin,default);
        Assert.True(retry.Success,retry.UserMessage);
        Assert.Equal(1,Count("AttendanceRecords"));
        Assert.Equal(2,Count("AuditEvents"));
        Assert.Equal(BridgeErrorCodes.ConflictStale,
            (await svc.CommitAsync(new JsonObject
            {
                ["previewToken"] = commitPayload["previewToken"]!.GetValue<string>(),
                ["operationId"] = "TST-CATCHUP-OTHER-00002"
            }, Admin,default)).ErrorCode);
        Assert.Equal(BridgeErrorCodes.ConflictStale,
            (await svc.PreviewAsync(Input(date),Ga,default)).ErrorCode);
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT Source FROM AttendanceRecords WHERE AttendanceLocalDate=$date;";
        q.Parameters.AddWithValue("$date",Iso(date));
        Assert.Equal("08h-admin-catchup",q.ExecuteScalar());
    }

    [Fact]
    public async Task StalePreviewNeverOverwritesManualAttendance()
    {
        var svc = new AdministrativeAttendanceCatchUpService(_db);
        DateOnly date = Day(-1);
        var response = await svc.PreviewAsync(Input(date),Ga,default);
        Assert.True(response.Success,response.UserMessage);
        using(var c = _db.CreateOpenConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                    StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                    AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                    Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES('TST-7DAY-PERSON',$day,'STS-ABSENT','غياب','غ','#EF4444',
                    'TST-7DAY-ASSIGN','SITE-UNSPECIFIED','غير محدد','manual','TST-CATCHUP-GA',
                    '2026-01-01T00:00:00Z',1,'TST-STALENESS-MANUAL');
                """;
            cmd.Parameters.AddWithValue("$day",Iso(date));
            await cmd.ExecuteNonQueryAsync();
        }
        var result = await svc.CommitAsync(new JsonObject
        {
            ["previewToken"]=Assert.IsType<JsonObject>(response.Data)["previewToken"]!.GetValue<string>(),
            ["operationId"]="TST-CATCHUP-STALE-00003"
        },Ga,default);
        Assert.False(result.Success);
        Assert.Equal(BridgeErrorCodes.ConflictStale,result.ErrorCode);
        Assert.Equal(1,Count("AttendanceRecords"));
        Assert.Equal(0,Count("AutoAttendanceRuns"));
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
