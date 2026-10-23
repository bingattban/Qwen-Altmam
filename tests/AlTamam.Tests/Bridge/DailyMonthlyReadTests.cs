using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Read-only integration tests using encrypted, synthetic local records.</summary>
public sealed class DailyMonthlyReadTests : IAsyncLifetime, IDisposable
{
    private readonly string _file = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_file, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Persons (PersonId, CurrentVisibleName, NormalizedName, CurrentServiceNumberA,
                                NormalizedServiceNumberA, FirstRecordedLocalDate, FirstRecordedAtUtc,
                                OrganizationalStatus, ActiveFlag, RowVersion)
            VALUES ('TST-P-1','شخص اختباري','شخص اختباري','X001','X001',
                    '2026-10-01','2026-10-01T07:00:00Z',1,1,1);
            INSERT INTO PersonAssignments (AssignmentId, PersonId, SiteId, ValidFromLocalDate,
                                           ValidToLocalDate, SourceMovementId, CreatedAtUtc)
            VALUES ('TST-A-1','TST-P-1','SITE-UNSPECIFIED','2026-10-01',
                    NULL,NULL,'2026-10-01T07:00:00Z');
            INSERT INTO AttendanceRecords (PersonId, AttendanceLocalDate, StatusId,
                                           StatusNameSnapshot, StatusSymbolSnapshot, StatusColorSnapshot,
                                           AssignmentIdAtEffectiveDate, SiteIdAtEffectiveDate,
                                           SiteNameSnapshot, Source, CreatedByUserId, CommittedAtUtc, IdempotencyKey)
            VALUES ('TST-P-1','2026-10-19','STS-ABSENT','غياب','غ','#EF4444',
                    'TST-A-1','SITE-UNSPECIFIED','غير محدد','manual','TST-U',
                    '2026-10-19T07:00:00Z','TST-IDEM-READ-1');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Dashboard_UsesSavedStatusAndSiteFilter()
    {
        var svc = new DailyMonthlyReadService(_db);
        var payload = new JsonObject { ["businessDate"] = "2026-10-19", ["siteId"] = "" };
        var summary = await svc.DashboardSummaryAsync(payload, CancellationToken.None);
        Assert.True(summary.Success);
        var data = Assert.IsType<JsonObject>(summary.Data);
        Assert.Equal(1L, data["totalForce"]!.GetValue<long>());
        Assert.Equal(0L, data["unprepared"]!.GetValue<long>());
        var statuses = Assert.IsType<JsonArray>(data["statusCounts"]);
        var absence = statuses.OfType<JsonObject>().Single(s => s["statusId"]!.GetValue<string>() == "STS-ABSENT");
        Assert.Equal(1L, absence["count"]!.GetValue<long>());

        var roster = await svc.DailyRosterAsync(new JsonObject
        {
            ["businessDate"] = "2026-10-19", ["siteId"] = "SITE-UNSPECIFIED",
            ["statusId"] = "STS-ABSENT", ["page"] = 1, ["pageSize"] = 10
        }, CancellationToken.None);
        Assert.True(roster.Success);
        var rows = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(roster.Data)["items"]);
        Assert.Single(rows);
        Assert.Equal("X001", rows[0]!["serviceNumberA"]!.GetValue<string>());
        var filtered = await svc.DailyRosterAsync(new JsonObject
        {
            ["businessDate"] = "2026-10-19", ["siteId"] = "SITE-UNKNOWN",
            ["statusId"] = "STS-ABSENT"
        }, CancellationToken.None);
        Assert.Equal(0L, Assert.IsType<JsonObject>(filtered.Data)["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task Monthly_IsReadOnlyAndRetainsSavedDailySnapshot()
    {
        var svc = new DailyMonthlyReadService(_db);
        var result = await svc.MonthlyGridAsync(new JsonObject
        {
            ["year"] = 2026, ["month"] = 10, ["page"] = 1, ["pageSize"] = 10
        }, CancellationToken.None);
        Assert.True(result.Success);
        var content = Assert.IsType<JsonObject>(result.Data);
        Assert.Equal(31, content["daysInMonth"]!.GetValue<int>());
        var items = Assert.IsType<JsonArray>(content["items"]);
        Assert.Single(items);
        var days = Assert.IsType<JsonObject>(items[0]!["days"]);
        Assert.Equal("غياب", days["2026-10-19"]!["name"]!.GetValue<string>());

        var periods = await svc.ArchivePeriodsAsync(CancellationToken.None);
        Assert.Contains(Assert.IsType<JsonArray>(periods.Data),
            node => node!.GetValue<string>() == "2026-10");
    }

    [Fact]
    public async Task InvalidReadDate_IsRejectedWithoutMutation()
    {
        var result = await new DailyMonthlyReadService(_db).DailyRosterAsync(
            new JsonObject { ["businessDate"] = "invalid" }, CancellationToken.None);
        Assert.False(result.Success);
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_file);
    }
}
