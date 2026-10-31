using System.Globalization;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Scheduling;

/// <summary>Immutable result of one 08:00 automatic-attendance attempt (scheduled or catch-up).</summary>
public sealed record AutoAttendanceRunResult(
    string JobKey,
    DateOnly BusinessDate,
    bool WasAlreadyCompleted,
    bool BlockedClosedDay,
    int CopiedCount,
    int SkippedNoYesterdayCount,
    int SkippedTransferExemptCount,
    int SkippedManualCount,
    int UnrecoverableMissedCount,
    string SummaryArabic,
    bool IsCatchUpRun = false);

/// <summary>
/// Durable, database-authoritative implementation of the canonical 08:00 carry-forward rule
/// for individuals with no current-day attendance. Legacy 20:00 ledgers remain valid. Never trusts UI state: exclusions
/// (transfer-effective dates, archived persons, closed periods), manual precedence and ledger
/// idempotency are all enforced against SQLCipher inside a single write transaction per date.
/// </summary>
public sealed class AutoAttendanceService
{
    public const int DefaultDueHour = 8;
    public const int DefaultCatchUpDays = 1;
    public const string SystemActorUserId = "USR-SYSTEM";
    private static readonly TimeSpan ClockRollbackTolerance = TimeSpan.FromMinutes(1);

    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public AutoAttendanceService(AlTamamDbContext database, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _db = database;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True when the local clock has passed the configured due time.</summary>
    public bool IsDueNow() => LocalNow().TimeOfDay >=
        GetDueTimeAsync().GetAwaiter().GetResult();

    public async Task<TimeSpan> GetDueTimeAsync(CancellationToken ct = default)
    {
        string configured=await Bridge.AutoAttendancePolicyService.ReadClockAsync(
            _db,Bridge.AutoAttendancePolicyService.DueTimeKey,
            Bridge.AutoAttendancePolicyService.DefaultDueTime,ct).ConfigureAwait(false);
        return TimeOnly.ParseExact(configured,"HH:mm",CultureInfo.InvariantCulture).ToTimeSpan();
    }

    /// <summary>
    /// Startup/tick entry point: performs bounded catch-up for missed business dates after the last
    /// completed ledger date, then the current-day run when due. Refuses to run on suspected clock
    /// rollback. Returns one result per processed date (empty when nothing is pending).
    /// </summary>
    public async Task<IReadOnlyList<AutoAttendanceRunResult>> RunPendingAsync(CancellationToken ct = default)
    {
        DateTimeOffset nowUtc = _time.GetUtcNow();
        DateTime localNow = LocalNow();
        DateOnly today = DateOnly.FromDateTime(localNow);

        if (await IsClockRolledBackAsync(nowUtc, ct).ConfigureAwait(false))
            throw new BridgeProtocolException(BridgeErrorCodes.ClockRollbackSuspected,
                "رُصد ارتداد في ساعة النظام؛ أُوقف التحضير التلقائي حتى تصحيح الساعة.");

        // The configured 24-hour local schedule is read from persisted settings.
        // The 08:00 default remains only when no administrator value exists.
        // Owner policy: unattended recovery may reach yesterday only. Older missing
        // days need a separate explicitly authorized seven-day administrator action.
        int catchUpDays = DefaultCatchUpDays;
        bool dayDue = localNow.TimeOfDay >= await GetDueTimeAsync(ct).ConfigureAwait(false);

        List<DateOnly> pending = EnumeratePendingDates(today, dayDue, catchUpDays,
            await GetLastCompletedBusinessDateAsync(ct).ConfigureAwait(false), out int unrecoverable);

        var results = new List<AutoAttendanceRunResult>();
        foreach (DateOnly date in pending)
        {
            AutoAttendanceRunResult r = await RunForDateAsync(date, isCatchUp: date < today || !dayDue, ct).ConfigureAwait(false);
            if (unrecoverable > 0 && results.Count == 0)
                r = r with { UnrecoverableMissedCount = unrecoverable,
                             SummaryArabic = $"{r.SummaryArabic} — فوات غير قابل للتدارك: {unrecoverable} يوم." };
            results.Add(r);
            // A blocked yesterday must not prevent independent current-day preparation.
            if (!r.WasAlreadyCompleted && r.BlockedClosedDay && date == today) break;
        }
        await PersistLastObservedAsync(nowUtc, ct).ConfigureAwait(false);
        return results;
    }

    /// <summary>Runs (or replays from the ledger) the automatic attendance for exactly one business date.</summary>
    public async Task<AutoAttendanceRunResult> RunForDateAsync(DateOnly businessDate, bool isCatchUp, CancellationToken ct = default)
    {
        AutoAttendanceRunResult? existing = await ReadLedgerAsync(JobKey(businessDate), ct).ConfigureAwait(false);
        if (existing is not null) return existing with { WasAlreadyCompleted = true };
        return await _db.ExecuteInWriteTransactionAsync((connection, tx) =>
            RunForDateInTransactionAsync(businessDate, isCatchUp, connection, tx, ct), ct).ConfigureAwait(false);
    }

    // The administrator path uses the SAME attendance algorithm within its own
    // atomic preview/receipt/audit transaction; no parallel business rules.
    internal async Task<AutoAttendanceRunResult> RunForDateInTransactionAsync(
        DateOnly businessDate, bool isCatchUp, SqliteConnection connection, SqliteTransaction tx,
        CancellationToken ct, AlTamam.Application.Bridge.BridgePrincipal? administrator = null)
    {
        string jobKey = JobKey(businessDate);
        string source = administrator is null ? (isCatchUp ? "08h-catchup" : "08h-auto")
            : "08h-admin-catchup";
        DateTimeOffset startedUtc = _time.GetUtcNow();
        string configuredDue = Bridge.AutoAttendancePolicyService.DefaultDueTime;
        // Read the configured clock via the existing SQLCipher transaction.
        using (var due = connection.CreateCommand())
        {
            due.Transaction = tx;
            due.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
            due.Parameters.AddWithValue("$key", Bridge.AutoAttendancePolicyService.DueTimeKey);
            string? stored = await due.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (Bridge.AutoAttendancePolicyService.IsValidClock(stored)) configuredDue = stored!;
        }
        string dueLocal = Iso(businessDate) + "T" + configuredDue + ":00";

            // Closed-date lock enforced by the database authority (DAY or MONTH closure).
            using (var closed = connection.CreateCommand())
            {
                closed.Transaction = tx;
                closed.CommandText = @"SELECT EXISTS(SELECT 1 FROM PeriodClosures
                                        WHERE IsClosed = 1 AND PeriodKey IN ($day, $month));";
                closed.Parameters.AddWithValue("$day", Iso(businessDate));
                closed.Parameters.AddWithValue("$month", businessDate.ToString("yyyy-MM", CultureInfo.InvariantCulture));
                if (administrator is null && Convert.ToInt32(await closed.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1)
                {
                    return new AutoAttendanceRunResult(jobKey, businessDate, false, true, 0, 0, 0, 0, 0,
                        $"التحضير التلقائي مرفوض: تاريخ الأعمال {Iso(businessDate)} مغلق إدارياً.");
                }
            }

            // Resolve the administrator's active fallback StatusId inside the SAME
            // transaction as attendance, so deactivation/renaming cannot race.
            (string Id,string Name,string Symbol,string Color)? configuredDefault = null;
            using (var setting = connection.CreateCommand())
            {
                setting.Transaction = tx;
                setting.CommandText = """
                    SELECT s.StatusId,s.VisibleName,s.ShortSymbol,s.ChosenColorHex
                    FROM AppSettings a
                    JOIN AttendanceStatusDefinitions s ON s.StatusId=a.SettingValue AND s.ActiveFlag=1
                    WHERE a.SettingKey=$key;
                    """;
                setting.Parameters.AddWithValue("$key",Bridge.AutoAttendancePolicyService.DefaultStatusKey);
                using var chosen = await setting.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await chosen.ReadAsync(ct).ConfigureAwait(false))
                    configuredDefault=(chosen.GetString(0),chosen.GetString(1),
                        chosen.GetString(2),chosen.GetString(3));
            }

            // One row per eligible active person with an open assignment covering the date.
            int copied = 0, defaulted = 0, skipNoYesterday = 0, skipTransfer = 0, skipManual = 0;
            // Four indexed set reads replace thousands of scalar queries.
            // Avoid a correlated join: measured SQLCipher timings showed it was
            // slower than the original N+1 algorithm on 2,000 synthetic people.
            var people = new List<(string PersonId, string AssignmentId, string SiteId, string SiteName)>();
            using (var roster = connection.CreateCommand())
            {
                roster.Transaction = tx;
                roster.CommandText = """
                    SELECT p.PersonId, a.AssignmentId, a.SiteId, s.VisibleName
                    FROM Persons p
                    JOIN PersonAssignments a ON a.PersonId=p.PersonId
                        AND a.ValidFromLocalDate <= $date
                        AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate > $date)
                    JOIN Sites s ON s.SiteId=a.SiteId
                    WHERE ((p.ActiveFlag=1 AND p.OrganizationalStatus=1) OR $admin=1)
                      AND p.FirstRecordedLocalDate <= $date
                    ORDER BY p.PersonId;
                    """;
                roster.Parameters.AddWithValue("$date", Iso(businessDate));
                roster.Parameters.AddWithValue("$admin", administrator is null ? 0 : 1);
                using var reader = await roster.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    people.Add((reader.GetString(0), reader.GetString(1),
                        reader.GetString(2), reader.GetString(3)));
            }

            var transferredToday = new HashSet<string>(StringComparer.Ordinal);
            using (var movements = connection.CreateCommand())
            {
                movements.Transaction = tx;
                movements.CommandText = """
                    SELECT PersonId FROM PersonnelMovements
                    WHERE EffectiveLocalDate=$date AND MovementType IN (1,2,3);
                    """;
                movements.Parameters.AddWithValue("$date", Iso(businessDate));
                using var reader = await movements.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    transferredToday.Add(reader.GetString(0));
            }

            var existingToday = new HashSet<string>(StringComparer.Ordinal);
            using (var committed = connection.CreateCommand())
            {
                committed.Transaction = tx;
                committed.CommandText = """
                    SELECT PersonId FROM AttendanceRecords WHERE AttendanceLocalDate=$date;
                    """;
                committed.Parameters.AddWithValue("$date", Iso(businessDate));
                using var reader = await committed.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    existingToday.Add(reader.GetString(0));
            }

            var previousStatuses = new Dictionary<string,
                (string Id, string Name, string Symbol, string Color)>(StringComparer.Ordinal);
            using (var previous = connection.CreateCommand())
            {
                previous.Transaction = tx;
                previous.CommandText = """
                    SELECT PersonId, StatusId, StatusNameSnapshot,
                           StatusSymbolSnapshot, StatusColorSnapshot
                    FROM AttendanceRecords WHERE AttendanceLocalDate=$yday;
                    """;
                previous.Parameters.AddWithValue("$yday", Iso(businessDate.AddDays(-1)));
                using var reader = await previous.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    previousStatuses.Add(reader.GetString(0),
                        (reader.GetString(1), reader.GetString(2),
                         reader.GetString(3), reader.GetString(4)));
            }

            foreach (var person in people)
            {
                if (transferredToday.Contains(person.PersonId))
                {
                    skipTransfer++;
                    continue;
                }
                if (existingToday.Contains(person.PersonId))
                {
                    skipManual++;
                    continue;
                }

                string statusId, statusName, statusSymbol, statusColor;
                if (!previousStatuses.TryGetValue(person.PersonId, out var previous))
                {
                    if (configuredDefault is null)
                    {
                        // Do not block other people who have a valid previous-day status.
                        // A person without yesterday or a configured fallback stays unprepared.
                        skipNoYesterday++;
                        continue;
                    }
                    statusId = configuredDefault.Value.Id;
                    statusName = configuredDefault.Value.Name;
                    statusSymbol = configuredDefault.Value.Symbol;
                    statusColor = configuredDefault.Value.Color;
                    defaulted++;
                }
                else
                {
                    statusId = previous.Id;
                    statusName = previous.Name;
                    statusSymbol = previous.Symbol;
                    statusColor = previous.Color;
                }

                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = @"INSERT INTO AttendanceRecords
                    (PersonId, AttendanceLocalDate, StatusId, StatusNameSnapshot, StatusSymbolSnapshot, StatusColorSnapshot,
                     AssignmentIdAtEffectiveDate, SiteIdAtEffectiveDate, SiteNameSnapshot, Source, CreatedByUserId,
                     CommittedAtUtc, RevisionNumber, IdempotencyKey)
                    VALUES ($p, $date, $st, $stn, $sts, $stc, $assign, $site, $siteName, $source, $actor, $now, 1, $idem);";
                insert.Parameters.AddWithValue("$p", person.PersonId);
                insert.Parameters.AddWithValue("$date", Iso(businessDate));
                insert.Parameters.AddWithValue("$st", statusId);
                insert.Parameters.AddWithValue("$stn", statusName);
                insert.Parameters.AddWithValue("$sts", statusSymbol);
                insert.Parameters.AddWithValue("$stc", statusColor);
                insert.Parameters.AddWithValue("$assign", person.AssignmentId);
                insert.Parameters.AddWithValue("$site", person.SiteId);
                insert.Parameters.AddWithValue("$siteName", person.SiteName);
                insert.Parameters.AddWithValue("$source", source);
                insert.Parameters.AddWithValue("$actor", administrator?.UserId ?? SystemActorUserId);
                insert.Parameters.AddWithValue("$now", startedUtc.ToString("O"));
                insert.Parameters.AddWithValue("$idem", $"AUTO08H-{Iso(businessDate)}-{person.PersonId}");
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                copied++;
            }

            DateTimeOffset completedUtc = _time.GetUtcNow();
            string summary = $"تشغيل {source} ليوم {Iso(businessDate)}: اعتُمد {copied}، منها {defaulted} بالحالة الافتراضية لعدم وجود تحضير أمس، استثناء نقل {skipTransfer}، تحضّر يدوياً مسبقاً {skipManual}.";

            // Actor attribution + tamper-evident audit event inside the SAME transaction (07.03).
            await Bridge.BridgeAuditWriter.WriteAsync(connection, tx, _time,
                operationCode: "AUTO_ATTENDANCE_08H",
                operationArabicName: "التحضير التلقائي وفق الوقت المحدد",
                actorUserId: administrator?.UserId ?? SystemActorUserId,
                actorUsername: administrator?.Username ?? "النظام (جدولة تلقائية)",
                actorDisplayName: administrator?.Username,
                targetEntityType: "AutoAttendanceRun",
                targetEntityId: jobKey,
                targetDisplay: $"تحضير تلقائي ليوم {Iso(businessDate)}",
                oldValueJson: null,
                newValueJson: new System.Text.Json.Nodes.JsonObject
                {
                    ["businessDate"] = Iso(businessDate),
                    ["source"] = source,
                    ["copied"] = copied,
                    ["defaultedNoYesterday"] = defaulted,
                    ["defaultStatusId"] = configuredDefault?.Id,
                    ["skippedNoYesterday"] = skipNoYesterday,
                    ["skippedTransferExempt"] = skipTransfer,
                    ["skippedManual"] = skipManual
                }.ToJsonString(),
                description: summary,
                reason: null,
                effectiveLocalDate: Iso(businessDate),
                correlationId: startedUtc.ToUnixTimeMilliseconds().ToString("D", CultureInfo.InvariantCulture),
                ct).ConfigureAwait(false);

            using (var ledger = connection.CreateCommand())
            {
                ledger.Transaction = tx;
                ledger.CommandText = @"INSERT INTO AutoAttendanceRuns
                    (JobKey, BusinessDate, DueTimeLocal, StartedAtUtc, CompletedAtUtc, IsCatchUpRun,
                     CopiedCount, SkippedNoYesterdayCount, SkippedTransferExemptCount, SummaryArabic)
                    VALUES ($job, $date, $due, $start, $end, $catch, $copied, $noY, $transfer, $summary);";
                ledger.Parameters.AddWithValue("$job", jobKey);
                ledger.Parameters.AddWithValue("$date", Iso(businessDate));
                ledger.Parameters.AddWithValue("$due", dueLocal);
                ledger.Parameters.AddWithValue("$start", startedUtc.ToString("O"));
                ledger.Parameters.AddWithValue("$end", completedUtc.ToString("O"));
                ledger.Parameters.AddWithValue("$catch", isCatchUp ? 1 : 0);
                ledger.Parameters.AddWithValue("$copied", copied);
                ledger.Parameters.AddWithValue("$noY", skipNoYesterday);
                ledger.Parameters.AddWithValue("$transfer", skipTransfer);
                ledger.Parameters.AddWithValue("$summary", summary);
                await ledger.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return new AutoAttendanceRunResult(jobKey, businessDate, false, false,
                copied, skipNoYesterday, skipTransfer, skipManual, 0, summary, isCatchUp);
    }

    /// <summary>Reads the scheduling diagnostics: last ledger rows plus next scheduled due time.</summary>
    public async Task<LedgerSnapshot> ReadStatusAsync(int take, CancellationToken ct = default)
    {
        DateTimeOffset nowUtc = _time.GetUtcNow();
        DateTime localNow = LocalNow();
        DateOnly today = DateOnly.FromDateTime(localNow);
        bool dayDue = localNow.TimeOfDay >= await GetDueTimeAsync(ct).ConfigureAwait(false);
        DateOnly nextDueDate = dayDue ? today.AddDays(1) : today;

        var runs = new List<AutoAttendanceRunResult>();
        using (var connection = _db.CreateOpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"SELECT JobKey, BusinessDate, IsCatchUpRun, CopiedCount, SkippedNoYesterdayCount,
                                       SkippedTransferExemptCount, SummaryArabic
                                FROM AutoAttendanceRuns ORDER BY BusinessDate DESC LIMIT $take;";
            cmd.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 60));
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                DateOnly d = DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                runs.Add(new AutoAttendanceRunResult(reader.GetString(0), d, true, false,
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), 0, 0,
                    reader.GetString(6), reader.GetInt32(2) == 1));
            }
        }

        bool lastObservedValid = true;
        string? lastObserved = await GetSettingAsync("LastObservedUtcTimestamp", ct).ConfigureAwait(false);
        if (lastObserved is not null && DateTimeOffset.TryParse(lastObserved, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset stored) && stored > nowUtc + ClockRollbackTolerance)
            lastObservedValid = false;

        DateOnly? lastCompleted = runs.Count > 0 ? runs[0].BusinessDate : null;
        // Report the actual bounded automatic queue, not an unsupported historic
        // backlog that the scheduler is forbidden to process.
        int pending = EnumeratePendingDates(today, dayDue, DefaultCatchUpDays,
            lastCompleted, out _).Count;

        string dueText=await Bridge.AutoAttendancePolicyService.ReadClockAsync(
            _db,Bridge.AutoAttendancePolicyService.DueTimeKey,
            Bridge.AutoAttendancePolicyService.DefaultDueTime,ct).ConfigureAwait(false);
        string nextDueWall = nextDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T" + dueText + ":00";
        return new LedgerSnapshot(runs, nowUtc, localNow.ToString("yyyy-MM-ddTHH:mm:ss"),
            nextDueWall, dayDue, lastObservedValid, pending);
    }

    public sealed record LedgerSnapshot(
        IReadOnlyList<AutoAttendanceRunResult> Runs,
        DateTimeOffset NowUtc,
        string LocalNowIso,
        string NextDueLocalWallClock,
        bool CurrentDayDue,
        bool ClockIntegrityOk,
        int PendingCatchUpDays);

    // ------------------------------------------------------------------ internals

    internal static string JobKey(DateOnly businessDate) => "AUTO08H-" + Iso(businessDate);
    internal static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DateTime LocalNow() => _time.GetLocalNow().DateTime;

    public List<DateOnly> EnumeratePendingDates(DateOnly today, bool dayDue, int catchUpDays,
        DateOnly? lastCompleted, out int unrecoverable)
    {
        unrecoverable = 0;
        // On first setup there is no reliable earlier baseline to reconstruct.
        if (lastCompleted is null) return dayDue ? [today] : [];
        DateOnly yesterday = today.AddDays(-1);
        DateOnly firstMissing = lastCompleted.Value.AddDays(1);
        if (firstMissing < yesterday)
            unrecoverable = yesterday.DayNumber - firstMissing.DayNumber;
        // The obsolete catchUpDays setting cannot extend the automatic window.
        _ = catchUpDays;
        var pending = new List<DateOnly>(2);
        if (firstMissing <= yesterday) pending.Add(yesterday);
        if (dayDue && firstMissing <= today) pending.Add(today);
        return pending;
    }

    private async Task<AutoAttendanceRunResult?> ReadLedgerAsync(string jobKey, CancellationToken ct)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT JobKey, BusinessDate, IsCatchUpRun, CopiedCount, SkippedNoYesterdayCount,
                                   SkippedTransferExemptCount, SummaryArabic
                            FROM AutoAttendanceRuns WHERE BusinessDate = $date
                            ORDER BY CASE WHEN JobKey=$job THEN 0 ELSE 1 END LIMIT 1;";
        cmd.Parameters.AddWithValue("$job", jobKey);
        cmd.Parameters.AddWithValue("$date", jobKey["AUTO08H-".Length..]);
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var date = DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
        return new AutoAttendanceRunResult(reader.GetString(0), date, true, false,
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), 0, 0,
            reader.GetString(6), reader.GetInt32(2) == 1);
    }

    private async Task<DateOnly?> GetLastCompletedBusinessDateAsync(CancellationToken ct)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(BusinessDate) FROM AutoAttendanceRuns;";
        object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (value is null or DBNull) return null;
        return DateOnly.Parse(value.ToString()!, CultureInfo.InvariantCulture);
    }

    private async Task<bool> IsClockRolledBackAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        string? stored = await GetSettingAsync("LastObservedUtcTimestamp", ct).ConfigureAwait(false);
        if (stored is null) return false;
        return DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset last)
               && nowUtc < last - ClockRollbackTolerance;
    }

    private async Task<string?> GetSettingAsync(string key, CancellationToken ct)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? null : value.ToString();
    }

    private async Task PersistLastObservedAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO AppSettings (SettingKey, SettingValue, UpdatedAtUtc)
                                VALUES ('LastObservedUtcTimestamp', $v, $t)
                                ON CONFLICT(SettingKey) DO UPDATE SET SettingValue = $v, UpdatedAtUtc = $t;";
            cmd.Parameters.AddWithValue("$v", nowUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$t", nowUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }
}
