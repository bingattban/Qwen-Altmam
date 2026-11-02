using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Phase 07: the canonical automatic-attendance ledger is a read-only,
/// authenticated projection. No UI-generated "success" is accepted as evidence.
/// </summary>
public sealed class AutoAttendanceRunReadTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;
    private const string GaNonce = "11111111111111111111111111111111";
    private const string SvNonce = "33333333333333333333333333333333";
    private static readonly BridgePrincipal Ga =
        new("TST-RUN-GA", "run-admin", UserRole.GeneralAdministration, GaNonce);
    private static readonly BridgePrincipal Sv =
        new("TST-RUN-SV", "run-supervisor", UserRole.Supervisor, SvNonce);

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _dispatcher = BridgeHandlerCatalog.Create(new BridgeSharedServices(_db));
    }

    private Task<BridgeDispatchResult> Query(string nonce, JsonObject? payload = null)
    {
        var request = new JsonObject
        {
            ["requestId"] = "TST-RUN-" + Guid.NewGuid().ToString("N"),
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = "GetAutoAttendanceRun",
            ["sessionNonce"] = nonce,
            ["payload"] = payload ?? new JsonObject()
        };
        return _dispatcher.DispatchAsync(request.ToJsonString(), LocalWebUiOrigin.BaseUrl,
            true, n => Task.FromResult<BridgePrincipal?>(n switch
            {
                GaNonce => Ga, SvNonce => Sv, _ => null
            }));
    }

    private long Count(string table)
    {
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(q.ExecuteScalar());
    }

    private async Task SeedRun(string job, string date, bool catchUp, int copied)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AutoAttendanceRuns
            (JobKey,BusinessDate,DueTimeLocal,StartedAtUtc,CompletedAtUtc,IsCatchUpRun,
             CopiedCount,SkippedNoYesterdayCount,SkippedTransferExemptCount,SummaryArabic)
            VALUES($job,$date,$due,$start,$end,$catch,$copied,2,1,'نتيجة تنفيذ محفوظة');
            """;
        cmd.Parameters.AddWithValue("$job", job);
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$due", date + "T08:00:00");
        cmd.Parameters.AddWithValue("$start", date + "T08:00:00Z");
        cmd.Parameters.AddWithValue("$end", date + "T08:01:00Z");
        cmd.Parameters.AddWithValue("$catch", catchUp ? 1 : 0);
        cmd.Parameters.AddWithValue("$copied", copied);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public void AutomaticCatchUp_OnlyYesterday_AndNeverChainsHistoricMissedDays()
    {
        var svc = new AutoAttendanceService(_db);
        var today = new DateOnly(2026, 10, 30);
        var oldLedger = new DateOnly(2026, 8, 1);
        var pending = svc.EnumeratePendingDates(today, true, 7, oldLedger, out int skipped);
        Assert.Equal(new[] { today.AddDays(-1), today }, pending);
        Assert.Equal(today.AddDays(-1).DayNumber - oldLedger.AddDays(1).DayNumber, skipped);
        Assert.Equal(new[] { today.AddDays(-1) },
            svc.EnumeratePendingDates(today, false, 7, oldLedger, out _));
        Assert.Equal(new[] { today },
            svc.EnumeratePendingDates(today, true, 7, today.AddDays(-1), out _));
        Assert.Empty(svc.EnumeratePendingDates(today, false, 7, today.AddDays(-1), out _));
        Assert.Equal(new[] { today },
            svc.EnumeratePendingDates(today, true, 7, null, out _));
        Assert.Empty(svc.EnumeratePendingDates(today, false, 7, null, out _));
    }

    [Fact]
    public async Task EmptyLedger_IsTruthfullyEmptyAndNeverCreatesAttendanceOrAudit()
    {
        var beforeAttendance = Count("AttendanceRecords");
        var beforeAudits = Count("AuditEvents");
        var result = await Query(SvNonce);
        Assert.Equal(BridgeOutcomeKind.Success, result.Kind);
        var data = Assert.IsType<JsonObject>(result.Response.Data);
        Assert.False(data["hasCompletedRuns"]!.GetValue<bool>());
        Assert.Empty(Assert.IsType<JsonArray>(data["runs"]));
        Assert.NotNull(data["nextDueLocal"]);
        Assert.False(data["failureHistoryAvailable"]!.GetValue<bool>());
        Assert.False(data["todayReceiptPresent"]!.GetValue<bool>());
        Assert.Equal(data["currentDayDue"]!.GetValue<bool>()
            ? "due_without_receipt" : "not_due",
            data["todayStatus"]!.GetValue<string>());
        Assert.Null(data["todayEvidence"]);
        Assert.Equal(beforeAttendance, Count("AttendanceRecords"));
        Assert.Equal(beforeAudits, Count("AuditEvents"));
        Assert.Equal(0, Count("AutoAttendanceRuns"));
    }

    [Fact]
    public async Task LedgerReadsActualNewestRunAndCatchUpWithoutChangingRecordedFacts()
    {
        await SeedRun("AUTO08H-2026-10-20", "2026-10-20", false, 5);
        await SeedRun("AUTO08H-2026-10-21", "2026-10-21", true, 7);
        long before = Count("AutoAttendanceRuns");
        var result = await Query(GaNonce, new JsonObject { ["take"] = 1 });
        Assert.Equal(BridgeOutcomeKind.Success, result.Kind);
        var data = Assert.IsType<JsonObject>(result.Response.Data);
        Assert.True(data["hasCompletedRuns"]!.GetValue<bool>());
        var runs = Assert.IsType<JsonArray>(data["runs"]);
        var newest = Assert.IsType<JsonObject>(Assert.Single(runs));
        Assert.Equal("2026-10-21", newest["businessDate"]!.GetValue<string>());
        Assert.Equal(7, newest["copiedCount"]!.GetValue<int>());
        Assert.True(newest["isCatchUpRun"]!.GetValue<bool>());
        Assert.True(newest["completed"]!.GetValue<bool>());
        Assert.Equal("completed",newest["status"]!.GetValue<string>());
        Assert.False(data["todayReceiptPresent"]!.GetValue<bool>());
        Assert.Equal("2026-10-21", data["lastRunDate"]!.GetValue<string>());
        Assert.Equal(before, Count("AutoAttendanceRuns"));
        Assert.Equal(0, Count("AuditEvents"));
    }

    [Fact]
    public async Task TodayIsCompletedOnlyWhenDatabaseHasAnActualTodayReceipt()
    {
        string today=TimeProvider.System.GetLocalNow().ToString("yyyy-MM-dd");
        await SeedRun("AUTO08H-"+today,today,false,8);
        var result=await Query(GaNonce,new JsonObject{["take"]=10});
        Assert.True(result.Response.Success,result.Response.UserMessage);
        var data=Assert.IsType<JsonObject>(result.Response.Data);
        Assert.True(data["todayReceiptPresent"]!.GetValue<bool>());
        Assert.Equal("completed",data["todayStatus"]!.GetValue<string>());
        Assert.Equal("AutoAttendanceRuns",data["todayEvidence"]!.GetValue<string>());
        var item=Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(data["runs"])));
        Assert.Equal("completed",item["status"]!.GetValue<string>());
        Assert.Equal(1L,Count("AutoAttendanceRuns"));
        Assert.Equal(0L,Count("AttendanceRecords"));
        Assert.Equal(0L,Count("AuditEvents"));
    }

    [Fact]
    public async Task InvalidLimitsAndUnauthenticatedCallerAreRejectedWithoutWrites()
    {
        var invalid = await Query(GaNonce, new JsonObject { ["take"] = 0 });
        Assert.False(invalid.Response.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError, invalid.Response.ErrorCode);
        Assert.NotNull(invalid.Response.ValidationErrors?["take"]);
        var tooMany = await Query(GaNonce, new JsonObject { ["take"] = 1000 });
        Assert.False(tooMany.Response.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError, tooMany.Response.ErrorCode);
        Assert.NotNull(tooMany.Response.ValidationErrors?["take"]);
        var badType = await Query(GaNonce, new JsonObject { ["take"] = "nope" });
        Assert.False(badType.Response.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError, badType.Response.ErrorCode);
        Assert.NotNull(badType.Response.ValidationErrors?["take"]);
        Assert.Null(badType.Response.Data);
        var anonymous = await Query("ffffffffffffffffffffffffffffffff");
        Assert.Equal(BridgeOutcomeKind.Unauthorized, anonymous.Kind);
        Assert.Equal(0, Count("AutoAttendanceRuns"));
        Assert.Equal(0, Count("AuditEvents"));
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
