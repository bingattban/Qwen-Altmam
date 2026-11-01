using AlTamam.Application.Scheduling;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Calendar boundaries and real SQLCipher persistence for Phase 07.</summary>
public sealed class Phase07CalendarClosureTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private readonly FrozenClock _time = new(new DateTimeOffset(2026, 10, 15, 7, 59, 0, TimeSpan.Zero));

    private sealed class FrozenClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(int year, int month, int day, int hour, int minute = 0)
            => _now = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
    }

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
              CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
              FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES ('TST-PH7-P1','شخص اختبار','شخص اختبار','930001','930001',
              '2026-10-14','2026-10-14T00:00:00Z',1,1,1);
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
              ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES ('TST-PH7-A1','TST-PH7-P1','SITE-UNSPECIFIED',
              '2026-10-14',NULL,NULL,'2026-10-14T00:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
              StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
              AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
              Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES ('TST-PH7-P1','2026-10-14','STS-ABSENT','غياب','غ','#EF4444',
              'TST-PH7-A1','SITE-UNSPECIFIED','غير محدد','manual',
              'TST-PH7-ADMIN','2026-10-14T07:00:00Z',1,'TST-PH7-YESTERDAY');
            """;
        await q.ExecuteNonQueryAsync();
    }

    private long Count(string table, string? condition = null)
    {
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM " + table + (condition is null ? "" : " WHERE " + condition);
        return Convert.ToInt64(q.ExecuteScalar());
    }

    [Fact]
    public async Task BeforeAtAndAfterEight_PreserveYesterday_AndNeverRunAtLock()
    {
        var scheduler = new AutoAttendanceService(_db, _time);
        Assert.Empty(await scheduler.RunPendingAsync());
        Assert.Equal(0, Count("AutoAttendanceRuns"));
        Assert.Equal(1, Count("PeriodClosures", "PeriodKey='2026-10-14' AND PeriodType='DAY'"));
        _time.Set(2026, 10, 15, 8);
        var onTime = await scheduler.RunPendingAsync();
        Assert.Single(onTime);
        Assert.Equal(1, onTime[0].CopiedCount);
        Assert.Equal(0, onTime[0].SkippedManualCount);
        Assert.Equal(1, Count("AutoAttendanceRuns"));
        _time.Set(2026, 10, 15, 20);
        Assert.Empty(await scheduler.RunPendingAsync());
        Assert.Equal(1, Count("AutoAttendanceRuns"));
        Assert.Equal(1, Count("AttendanceRecords", "AttendanceLocalDate='2026-10-14' AND Source='manual'"));
        Assert.Equal(1, Count("AttendanceRecords", "AttendanceLocalDate='2026-10-15' AND Source='08h-auto'"));
    }

    [Fact]
    public async Task MidnightClosesDayAndPriorMonth_WithoutRewritingPastRecords()
    {
        var scheduler = new AutoAttendanceService(_db, _time);
        _time.Set(2026, 10, 15, 8);
        await scheduler.RunPendingAsync();
        _time.Set(2026, 11, 1, 0);
        Assert.Empty(await scheduler.RunPendingAsync());
        Assert.Equal(1, Count("PeriodClosures", "PeriodKey='2026-10' AND PeriodType='MONTH' AND IsClosed=1"));
        Assert.Equal(1, Count("PeriodClosures", "PeriodKey='2026-10-15' AND PeriodType='DAY' AND IsClosed=1"));
        Assert.Equal(1, Count("AttendanceRecords", "AttendanceLocalDate='2026-10-15' AND Source='08h-auto'"));
        _time.Set(2026, 11, 1, 8);
        await scheduler.RunPendingAsync();
        Assert.Equal(1, Count("AutoAttendanceRuns", "BusinessDate='2026-10-15'"));
        Assert.Equal(0, Count("AutoAttendanceRuns", "BusinessDate='2026-10-31'"));
    }

    [Fact]
    public async Task ConcurrentSameDayExecution_IsExactlyOnce()
    {
        var scheduler = new AutoAttendanceService(_db, _time);
        var outcomes = await Task.WhenAll(
            scheduler.RunForDateAsync(new DateOnly(2026, 10, 15), false),
            scheduler.RunForDateAsync(new DateOnly(2026, 10, 15), false));
        Assert.Single(outcomes, x => !x.WasAlreadyCompleted);
        Assert.Single(outcomes, x => x.WasAlreadyCompleted);
        Assert.Equal(1, Count("AutoAttendanceRuns"));
        Assert.Equal(1, Count("AttendanceRecords", "AttendanceLocalDate='2026-10-15'"));
        Assert.Equal(1, Count("AttendanceRecords", "AttendanceLocalDate='2026-10-14'"));
    }


    [Fact]
    public async Task ExistingHumanAttendance_IsNeverOverwritten_AndSkipIsDurable()
    {
        using (var c = _db.CreateOpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                  StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                  AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                  Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES ('TST-PH7-P1','2026-10-15','STS-PRESENT','موجود','م','#38BDF8',
                  'TST-PH7-A1','SITE-UNSPECIFIED','غير محدد','manual',
                  'TST-PH7-ADMIN','2026-10-15T07:30:00Z',1,'TST-PH7-MANUAL');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var scheduler = new AutoAttendanceService(_db, _time);
        var result = await scheduler.RunForDateAsync(new DateOnly(2026, 10, 15), false);
        Assert.Equal(0, result.CopiedCount);
        Assert.Equal(1, result.SkippedManualCount);
        var replay = await new AutoAttendanceService(_db, _time)
            .RunForDateAsync(new DateOnly(2026, 10, 15), false);
        Assert.True(replay.WasAlreadyCompleted);
        Assert.Equal(1, replay.SkippedManualCount);
        Assert.True(replay.HasDetailedCounts);
        Assert.Equal(1L, Count("AutoAttendanceRuns", "SkippedManualCount=1 AND FailedCount=0"));
        Assert.Equal(1L, Count("AttendanceRecords",
            "AttendanceLocalDate='2026-10-15' AND Source='manual'"));
    }

    [Fact]
    public async Task RetiredYesterdayStatus_UsesOnlyConfiguredActiveFallback()
    {
        using (var c = _db.CreateOpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE AttendanceStatusDefinitions SET ActiveFlag=0 WHERE StatusId='STS-ABSENT';
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES('AutoAttendanceDefaultStatusId','STS-PERMISSION','2026-10-14T00:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var r = await new AutoAttendanceService(_db, _time)
            .RunForDateAsync(new DateOnly(2026, 10, 15), false);
        Assert.Equal(1, r.CopiedCount);
        Assert.Equal(1, r.DefaultedCount);
        Assert.Equal(1L, Count("AttendanceRecords",
            "AttendanceLocalDate='2026-10-14' AND StatusId='STS-ABSENT'"));
        Assert.Equal(1L, Count("AttendanceRecords",
            "AttendanceLocalDate='2026-10-15' AND StatusId='STS-PERMISSION'"));
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
