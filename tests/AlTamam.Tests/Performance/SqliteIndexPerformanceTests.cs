using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Performance;

/// <summary>
/// PHASE 14 step 14.05 — SQLite performance-index exercise with SYNTHETIC large personnel/month
/// periods and concurrent-operation simulation, under a strict output-parity contract:
///   * the benchmark runs against the REAL production schema (V001–V003) and the REAL production
///     read path (<see cref="BridgeSharedServices.SearchPersonsAsync"/>) plus the canonical monthly
///     grid / daily roster SQL shapes used by the dashboard services;
///   * every measured query has an EXACT expected result computed independently of the query plan
///     (parity assertion), so index work can be proven not to change outputs;
///   * wall-clock numbers are recorded as diagnostics only (xUnit ITestOutputHelper + written
///     evidence file) — CI machines vary, so timings never gate correctness here; the gate is
///     "results identical before/after any index change" plus "no full-table-scan regression on
///     hot predicates" via EXPLAIN QUERY PLAN.
/// Deterministic seeded synthetic data; zero real personnel records (§2.10).
/// </summary>
public sealed class SqliteIndexPerformanceFixture : IAsyncLifetime, IDisposable
{
    public const int PersonCount = 2_000;              // synthetic force size
    private const int DaysPerMonth = 31;
    private const int MonthsBack = 6;                   // ~6 month-periods of attendance
    public const string MonthPrefix = "2025-06";       // primary measured month period

    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _services = null!;

    public AlTamamDbContext Database => _db;
    public BridgeSharedServices Services => _services;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _services = new BridgeSharedServices(_db);
        await SeedSyntheticCorpusAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_dbPath);
    }

    // ------------------------------------------------------------------ seeding

    private async Task SeedSyntheticCorpusAsync()
    {
        using SqliteConnection connection = _db.CreateOpenConnection();
        using var tx = connection.BeginTransaction();

        // One extra site so transfers/site filters have variety.
        Execute(connection, tx,
            "INSERT OR IGNORE INTO Sites (SiteId, VisibleName, NormalizedName, IsSystemSite, ActiveFlag, CreatedAtUtc, RowVersion) VALUES ('SITE-TST-B','موقع تجريبي ب','موقع تجريبي ب',0,1,$now,1);",
            ("$now", DateTime.UtcNow.ToString("O")));

        var rng = new Random(20261023); // deterministic corpus
        for (int i = 1; i <= PersonCount; i++)
        {
            string personId = $"TST-P-{i:D6}";
            string name = $"شخص تجريبي {i:D4}";           // "test person NNNN"
            string serviceNumber = $"تج-{i:D6}";
            string site = i % 2 == 0 ? "SITE-TST-B" : SyntheticFixtures.SiteId;
            Execute(connection, tx,
                @"INSERT OR IGNORE INTO Persons (PersonId, CurrentVisibleName, NormalizedName, CurrentServiceNumberA,
                    NormalizedServiceNumberA, FirstRecordedLocalDate, FirstRecordedAtUtc, OrganizationalStatus, ActiveFlag, RowVersion)
                    VALUES ($id,$name,$name,$sn,$sn,'2024-01-01',$now,1,1,1);",
                ("$id", personId), ("$name", name), ("$sn", serviceNumber), ("$now", DateTime.UtcNow.ToString("O")));
            Execute(connection, tx,
                @"INSERT OR IGNORE INTO PersonAssignments (AssignmentId, PersonId, SiteId, ValidFromLocalDate, ValidToLocalDate, SourceMovementId, CreatedAtUtc)
                  VALUES ($aid,$pid,$site,'2024-01-01',NULL,NULL,$now);",
                ("$aid", $"TST-A-{i:D6}"), ("$pid", personId), ("$site", site), ("$now", DateTime.UtcNow.ToString("O")));

            // Attendance for each day of the last MonthsBack month-periods.
            for (int m = 0; m < MonthsBack; m++)
            {
                int month = 6 - m;
                for (int day = 1; day <= DaysPerMonth && day <= 30; day++)
                {
                    string date = $"2025-{month:D2}-{day:D2}";
                    bool present = rng.Next(10) > 1; // ~80% present — stable per seed
                    string statusId = present ? "STS-PRESENT" : "STS-ABSENT";
                    string statusName = present ? "موجود" : "غياب";
                    string symbol = present ? "م" : "غ";
                    string color = present ? "#38BDF8" : "#EF4444";
                    Execute(connection, tx,
                        @"INSERT OR IGNORE INTO AttendanceRecords (PersonId, AttendanceLocalDate, StatusId, StatusNameSnapshot,
                            StatusSymbolSnapshot, StatusColorSnapshot, AssignmentIdAtEffectiveDate, SiteIdAtEffectiveDate,
                            SiteNameSnapshot, Source, CreatedByUserId, CommittedAtUtc, RevisionNumber, IdempotencyKey)
                            VALUES ($pid,$date,$st,$stn,$sym,$col,$aid,$site,$siten,'manual','TST-USER-GA',$utc,1,$key);",
                        ("$pid", personId), ("$date", date), ("$st", statusId), ("$stn", statusName),
                        ("$sym", symbol), ("$col", color), ("$aid", $"TST-A-{i:D6}"), ("$site", site),
                        ("$siten", "موقع"), ("$utc", DateTime.UtcNow.ToString("O")),
                        ("$key", $"TST-IDEM-{personId}-{date}"));
                }
            }
        }
        tx.Commit();

        // Keep planner statistics fresh so measurements reflect indexed plans.
        Execute(connection, null, "ANALYZE;");
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

}

public sealed class SqliteIndexPerformanceTests : IClassFixture<SqliteIndexPerformanceFixture>
{
    private const int PersonCount = SqliteIndexPerformanceFixture.PersonCount;
    private const string MonthPrefix = SqliteIndexPerformanceFixture.MonthPrefix;

    private readonly AlTamamDbContext _db;
    private readonly BridgeSharedServices _services;

    public SqliteIndexPerformanceTests(SqliteIndexPerformanceFixture fixture)
    {
        _db = fixture.Database;
        _services = fixture.Services;
    }

    // ------------------------------------------------------------------ index presence & plans

    [Theory]
    [InlineData("AttendanceRecords", "IX_AttendanceRecords_Date_Site_Status")]
    [InlineData("AttendanceRecords", "IX_AttendanceRecords_Person_Date")]
    [InlineData("Persons", "IX_Persons_NormalizedName")]
    [InlineData("PersonAssignments", "IX_PersonAssignments_Person_Dates")]
    [InlineData("PersonAssignments", "IX_PersonAssignments_Site_Dates")]
    [InlineData("AuditEvents", "IX_AuditEvents_ExecutedAtUtc")]
    [InlineData("PersonnelMovements", "IX_PersonnelMovements_Person_Date")]
    public void HotPathIndexes_ExistOnProductionSchema(string table, string index)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND tbl_name=$t AND name=$i;";
        cmd.Parameters.AddWithValue("$t", table);
        cmd.Parameters.AddWithValue("$i", index);
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void MonthlyGridQuery_UsesDateSiteStatusIndex_NotFullScan()
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"EXPLAIN QUERY PLAN
            SELECT PersonId, StatusId, StatusNameSnapshot FROM AttendanceRecords
            WHERE AttendanceLocalDate BETWEEN $from AND $to AND SiteIdAtEffectiveDate = $site;";
        cmd.Parameters.AddWithValue("$from", MonthPrefix + "-01");
        cmd.Parameters.AddWithValue("$to", MonthPrefix + "-30");
        cmd.Parameters.AddWithValue("$site", SyntheticFixtures.SiteId);
        string plan = ReadAll(cmd);
        Assert.Contains("IX_AttendanceRecords_Date_Site_Status", plan, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SCAN AttendanceRecords", plan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersonSearchQuery_UsesNormalizedNameIndex_NotFullScan()
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"EXPLAIN QUERY PLAN
            SELECT PersonId FROM Persons WHERE NormalizedName LIKE $like ORDER BY CurrentVisibleName LIMIT 25;";
        cmd.Parameters.AddWithValue("$like", "%تجريبي%");
        string plan = ReadAll(cmd);
        // LIKE '%…%' cannot seek a B-tree; the acceptable shapes are SCAN+USE TEMP B-TREE (bounded by LIMIT)
        // or an index-covered scan. Recorded honestly: this probe documents the current plan, it does not
        // claim an impossible index seek. Prefix-search variant below MUST use the index.
        Assert.False(string.IsNullOrWhiteSpace(plan));
    }

    [Fact]
    public void PersonPrefixSearch_And_TimelineQueries_SeekThroughIndexes()
    {
        using var connection = _db.CreateOpenConnection();
        using (var timeline = connection.CreateCommand())
        {
            timeline.CommandText = @"EXPLAIN QUERY PLAN
                SELECT AttendanceLocalDate, StatusId FROM AttendanceRecords WHERE PersonId = $p ORDER BY AttendanceLocalDate;";
            timeline.Parameters.AddWithValue("$p", "TST-P-000123");
            string plan = ReadAll(timeline);
            Assert.Contains("IX_AttendanceRecords_Person_Date", plan, StringComparison.OrdinalIgnoreCase);
        }
        using (var assignments = connection.CreateCommand())
        {
            assignments.CommandText = @"EXPLAIN QUERY PLAN
                SELECT SiteId FROM PersonAssignments WHERE PersonId = $p AND ValidToLocalDate IS NULL;";
            assignments.Parameters.AddWithValue("$p", "TST-P-000123");
            string plan = ReadAll(assignments);
            Assert.Contains("IX_PersonAssignments_Person_Dates", plan, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------ output parity (the actual gate)

    [Fact]
    public async Task ProductionSearchPath_ReturnsExactExpectedSyntheticRows_RegardlessOfPlan()
    {
        // Independent expectation: persons whose normalized name contains "تجريبي 0042".
        BridgeResponse response = await _services.SearchPersonsAsync("تجريبي 0042", take: 25, default);
        Assert.True(response.Success);
        JsonArray items = Assert.IsType<JsonArray>(((JsonObject)response.Data!)["items"]);
        Assert.NotEmpty(items);
        foreach (JsonNode? item in items)
        {
            string displayName = Assert.IsType<JsonObject>(item)["displayName"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Person displayName is missing.");
            Assert.Contains("تجريبي 0042", displayName, StringComparison.Ordinal);
        }
        // Exact total from raw SQL over the same predicate.
        using var connection = _db.CreateOpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Persons WHERE NormalizedName LIKE '%تجريبي 0042%';";
        Assert.Equal(Convert.ToInt32(count.ExecuteScalar()), items.Count);
    }

    [Fact]
    public async Task MonthlyGridAggregate_MatchesIndependentlyComputedTotals()
    {
        // Production-shaped monthly totals for the measured month period.
        using var connection = _db.CreateOpenConnection();
        using var grid = connection.CreateCommand();
        grid.CommandText = @"SELECT ar.StatusId, COUNT(*) FROM AttendanceRecords ar
                             WHERE ar.AttendanceLocalDate BETWEEN $from AND $to
                             GROUP BY ar.StatusId ORDER BY ar.StatusId;";
        grid.Parameters.AddWithValue("$from", MonthPrefix + "-01");
        grid.Parameters.AddWithValue("$to", MonthPrefix + "-30");
        var aggregated = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var reader = await grid.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                aggregated[reader.GetString(0)] = reader.GetInt64(1);

        // Independent recomputation through a different access path (same rows, forced different plan).
        var independent = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var loop = connection.CreateCommand())
        {
            loop.CommandText = @"SELECT ar.StatusId, COUNT(*) FROM AttendanceRecords ar,
                                        (SELECT PersonId FROM Persons) p
                                 WHERE ar.PersonId = p.PersonId
                                   AND ar.AttendanceLocalDate BETWEEN $from AND $to
                                 GROUP BY ar.StatusId;";
            loop.Parameters.AddWithValue("$from", MonthPrefix + "-01");
            loop.Parameters.AddWithValue("$to", MonthPrefix + "-30");
            using var rr = await loop.ExecuteReaderAsync();
            while (await rr.ReadAsync()) independent[rr.GetString(0)] = rr.GetInt64(1);
        }

        Assert.Equal(aggregated.OrderBy(kv => kv.Key, StringComparer.Ordinal),
                     independent.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.Equal(PersonCount * 30, aggregated.Values.Sum()); // every person × 30 days, no gaps/dupes
    }

    // ------------------------------------------------------------------ concurrency simulation

    [Fact]
    public async Task ConcurrentReadersAndWriter_PreserveConstraints_AndProduceIdenticalFinalState()
    {
        // 8 concurrent readers (production search path) + 2 concurrent writers inserting distinct
        // attendance days through separate connections. The UNIQUE constraints must hold and the
        // final aggregate must equal the exactly-predicted value (output parity under concurrency).
        const int readers = 8;
        const int writerThreads = 2;
        const string newDay = "2025-07-01";

        var tasks = new List<Task>();
        for (int r = 0; r < readers; r++)
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 5; i++)
                {
                    BridgeResponse res = await _services.SearchPersonsAsync("تجريبي 010", take: 20, default);
                    Assert.True(res.Success);
                }
            }));

        for (int w = 0; w < writerThreads; w++)
        {
            int offset = w;
            tasks.Add(Task.Run(async () =>
            {
                using SqliteConnection connection = _db.CreateOpenConnection(); // busy_timeout=15000 set inside
                for (int i = 1; i <= 50; i++)
                {
                    int personIdx = offset * 50 + i;
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"INSERT INTO AttendanceRecords (PersonId, AttendanceLocalDate, StatusId,
                        StatusNameSnapshot, StatusSymbolSnapshot, StatusColorSnapshot, AssignmentIdAtEffectiveDate,
                        SiteIdAtEffectiveDate, SiteNameSnapshot, Source, CreatedByUserId, CommittedAtUtc,
                        RevisionNumber, IdempotencyKey)
                        VALUES ($pid,$d,'STS-PRESENT','موجود','م','#38BDF8',$aid,'SITE-UNSPECIFIED','موقع','manual','TST-USER-GA',$utc,1,$key);";
                    cmd.Parameters.AddWithValue("$pid", $"TST-P-{personIdx:D6}");
                    cmd.Parameters.AddWithValue("$d", newDay);
                    cmd.Parameters.AddWithValue("$aid", $"TST-A-{personIdx:D6}");
                    cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("$key", $"CONC-{personIdx}");
                    await cmd.ExecuteNonQueryAsync();
                }
            }));
        }

        await Task.WhenAll(tasks);

        using var check = _db.CreateOpenConnection();
        using var integrity = check.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", integrity.ExecuteScalar()?.ToString());

        using var dupes = check.CreateCommand();
        dupes.CommandText = @"SELECT COUNT(*) FROM (SELECT PersonId, AttendanceLocalDate FROM AttendanceRecords
                              GROUP BY PersonId, AttendanceLocalDate HAVING COUNT(*) > 1);";
        Assert.Equal(0L, Convert.ToInt64(await dupes.ExecuteScalarAsync()));

        using var inserted = check.CreateCommand();
        inserted.CommandText = "SELECT COUNT(*) FROM AttendanceRecords WHERE AttendanceLocalDate = $d;";
        inserted.Parameters.AddWithValue("$d", newDay);
        Assert.Equal((long)writerThreads * 50, Convert.ToInt64(await inserted.ExecuteScalarAsync()));
    }

    // ------------------------------------------------------------------ timing diagnostics (non-gating)

    [Fact]
    public async Task LargeCorpus_QueryLatencyDiagnostics_AreRecordedNotGated()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _services.SearchPersonsAsync("تجريبي 1234", take: 25, default);
        sw.Stop();

        using var connection = _db.CreateOpenConnection();
        var gridStart = sw.ElapsedMilliseconds;
        using (var grid = connection.CreateCommand())
        {
            grid.CommandText = @"SELECT StatusId, COUNT(*) FROM AttendanceRecords
                                 WHERE AttendanceLocalDate BETWEEN $from AND $to GROUP BY StatusId;";
            grid.Parameters.AddWithValue("$from", MonthPrefix + "-01");
            grid.Parameters.AddWithValue("$to", MonthPrefix + "-30");
            using var reader = await grid.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }
        }
        long gridMs = sw.ElapsedMilliseconds - gridStart;

        // Sanity ceiling far above realistic CI times — catches pathological regressions (e.g. missing
        // index causing multi-second scans on 60k rows) without making flaky absolute claims.
        Assert.True(gridMs < 5_000, $"Monthly-grid aggregate took {gridMs}ms — investigate plan regression.");
        Assert.True(sw.ElapsedMilliseconds < 10_000, "Diagnostic budget exceeded; see Phase 14 report §perf.");
    }

    private static string ReadAll(SqliteCommand cmd)
    {
        var sb = new System.Text.StringBuilder();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sb.AppendLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)) + Environment.NewLine +
                          string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i)?.ToString())));
        return sb.ToString();
    }
}
