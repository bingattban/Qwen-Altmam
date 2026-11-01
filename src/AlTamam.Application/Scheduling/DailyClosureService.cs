using System.Globalization;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Scheduling;

/// <summary>
/// Persist local midnight DAY closure and prior-month MONTH closure in SQLCipher.
/// The actual timestamp is the observed execution time (not a fabricated midnight
/// timestamp when the desktop application was closed). Existing closures, including
/// older historical rows, are never changed.
/// </summary>
public sealed class DailyClosureService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public DailyClosureService(AlTamamDbContext db, TimeProvider? time = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = time ?? TimeProvider.System;
    }

    public async Task CloseExpiredPeriodsAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = _time.GetLocalNow();
        DateOnly today = DateOnly.FromDateTime(now.DateTime);
        string day = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string yesterday = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string month = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        string lastMonth = today.AddDays(-today.Day).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        string stamped = _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            // Close at least yesterday; recover older recorded days after downtime.
            // Dates without historical events are NOT fabricated as attendance runs.
            using (var days = connection.CreateCommand())
            {
                days.Transaction = tx;
                days.CommandText = """
                    INSERT OR IGNORE INTO PeriodClosures
                        (PeriodKey,PeriodType,IsClosed,ClosedAtUtc,ClosedByActor,PolicyVersion)
                    SELECT d,'DAY',1,$now,'USR-SYSTEM','phase07-midnight'
                    FROM (
                        SELECT $yesterday AS d
                        UNION SELECT AttendanceLocalDate FROM AttendanceRecords
                        UNION SELECT BusinessDate FROM AutoAttendanceRuns
                    ) WHERE d < $today;
                    """;
                days.Parameters.AddWithValue("$today", day);
                days.Parameters.AddWithValue("$yesterday", yesterday);
                days.Parameters.AddWithValue("$now", stamped);
                await days.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            // A past month is archived even when no attendance was recorded in it;
            // existing period rows are preserved exactly as originally written.
            using (var months = connection.CreateCommand())
            {
                months.Transaction = tx;
                months.CommandText = """
                    INSERT OR IGNORE INTO PeriodClosures
                        (PeriodKey,PeriodType,IsClosed,ClosedAtUtc,ClosedByActor,PolicyVersion)
                    SELECT m,'MONTH',1,$now,'USR-SYSTEM','phase07-month-end'
                    FROM (
                        SELECT $lastMonth AS m
                        UNION SELECT SUBSTR(PeriodKey,1,7) FROM PeriodClosures WHERE PeriodType='DAY'
                    ) WHERE m < $month;
                    """;
                months.Parameters.AddWithValue("$month", month);
                months.Parameters.AddWithValue("$lastMonth", lastMonth);
                months.Parameters.AddWithValue("$now", stamped);
                await months.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return 0;
        }, ct).ConfigureAwait(false);
    }
}
