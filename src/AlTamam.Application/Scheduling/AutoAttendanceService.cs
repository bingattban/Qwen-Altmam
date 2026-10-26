using System.Globalization;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Scheduling;

/// <summary>Immutable result of one 20:00 automatic-attendance attempt (scheduled or catch-up).</summary>
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
    string SummaryArabic);

/// <summary>
/// Durable, database-authoritative implementation of the canonical 20:00 carry-forward rule
/// documented in docs/implementation/PHASE07_20H_RULE.md. Never trusts UI state: exclusions
/// (transfer-effective dates, archived persons, closed periods), manual precedence and ledger
/// idempotency are all enforced against SQLCipher inside a single write transaction per date.
/// </summary>
public sealed class AutoAttendanceService
{
    public const int DefaultDueHour = 20;
    public const int DefaultCatchUpDays = 7;
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

    /// <summary>True when the local clock has passed the canonical due instant (>= 20:00).</summary>
    public bool IsDueNow() => LocalNow().TimeOfDay >= new TimeSpan(DefaultDueHour, 0, 0);

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

        // The approved business cutoff is fixed at 20:00 local; old persisted hour
        // overrides must not silently change this rule.
        int catchUpDays = await GetSettingIntAsync("AutoAttendanceCatchUpDays", DefaultCatchUpDays, ct).ConfigureAwait(false);
        bool dayDue = localNow.TimeOfDay >= new TimeSpan(DefaultDueHour, 0, 0);

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
            if (!r.WasAlreadyCompleted && r.BlockedClosedDay) break; // stop ascending catch-up at a blocked date
        }
        await PersistLastObservedAsync(nowUtc, ct).ConfigureAwait(false);
        return results;
    }

    /// <summary>Runs (or replays from the ledger) the automatic attendance for exactly one business date.</summary>
    public async Task<AutoAttendanceRunResult> RunForDateAsync(DateOnly businessDate, bool isCatchUp, CancellationToken ct = default)
    {
        string jobKey = JobKey(businessDate);
        AutoAttendanceRunResult? existing = await ReadLedgerAsync(jobKey, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing with { WasAlreadyCompleted = true };

        string source = isCatchUp ? "20h-catchup" : "20h-auto";
        DateTimeOffset startedUtc = _time.GetUtcNow();
        string dueLocal = Iso(businessDate) + "T20:00:00";

        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            // Closed-date lock enforced by the database authority (DAY or MONTH closure).
            using (var closed = connection.CreateCommand())
            {
                closed.Transaction = tx;
                closed.CommandText = @"SELECT EXISTS(SELECT 1 FROM PeriodClosures
                                        WHERE IsClosed = 1 AND PeriodKey IN ($day, $month));";
                closed.Parameters.AddWithValue("$day", Iso(businessDate));
                closed.Parameters.AddWithValue("$month", businessDate.ToString("yyyy-MM", CultureInfo.InvariantCulture));
                if (Convert.ToInt32(await closed.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1)
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
            var people = new List<(string PersonId, string AssignmentId, string SiteId, string SiteName)>();
            using (var roster = connection.CreateCommand())
            {
                roster.Transaction = tx;
                roster.CommandText = @"
                    SELECT p.PersonId, a.AssignmentId, a.SiteId, s.VisibleName
                      FROM Persons p
                      JOIN PersonAssignments a ON a.PersonId = p.PersonId
                       AND a.ValidFromLocalDate <= $date
                       AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate > $date)
                      JOIN Sites s ON s.SiteId = a.SiteId
                     WHERE p.ActiveFlag = 1 AND p.OrganizationalStatus=1
                       AND p.FirstRecordedLocalDate <= $date
                     ORDER BY p.PersonId;";
                roster.Parameters.AddWithValue("$date", Iso(businessDate));
                using SqliteDataReader readers = await roster.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await readers.ReadAsync(ct).ConfigureAwait(false))
                    people.Add((readers.GetString(0), readers.GetString(1), readers.GetString(2), readers.GetString(3)));
            }

            foreach (var person in people)
            {
                // Transfer-effective-date exclusion (canonical AttendanceDayPolicy mirrored in SQL).
                using (var movement = connection.CreateCommand())
                {
                    movement.Transaction = tx;
                    movement.CommandText = @"SELECT EXISTS(SELECT 1 FROM PersonnelMovements
                                               WHERE PersonId = $p AND EffectiveLocalDate = $date);";
                    movement.Parameters.AddWithValue("$p", person.PersonId);
                    movement.Parameters.AddWithValue("$date", Iso(businessDate));
                    if (Convert.ToInt32(await movement.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1)
                    {
                        skipTransfer++;
                        continue;
                    }
                }

                // Manual precedence: never overwrite any legitimate existing row for the date.
                using (var existsToday = connection.CreateCommand())
                {
                    existsToday.Transaction = tx;
                    existsToday.CommandText = @"SELECT Source FROM AttendanceRecords
                                                WHERE PersonId = $p AND AttendanceLocalDate = $date;";
                    existsToday.Parameters.AddWithValue("$p", person.PersonId);
                    existsToday.Parameters.AddWithValue("$date", Iso(businessDate));
                    object? existingSource = await existsToday.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if (existingSource is not null and not DBNull)
                    {
                        skipManual++;
                        continue;
                    }
                }

                // Carried-forward status selection from yesterday's committed row.
                string statusId, statusName, statusSymbol, statusColor;
                using (var yesterday = connection.CreateCommand())
                {
                    yesterday.Transaction = tx;
                    yesterday.CommandText = @"SELECT StatusId, StatusNameSnapshot, StatusSymbolSnapshot, StatusColorSnapshot
                                                FROM AttendanceRecords WHERE PersonId = $p AND AttendanceLocalDate = $yday;";
                    yesterday.Parameters.AddWithValue("$p", person.PersonId);
                    yesterday.Parameters.AddWithValue("$yday", Iso(businessDate.AddDays(-1)));
                    using SqliteDataReader yReader = await yesterday.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (!await yReader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        if (configuredDefault is null)
                        {
                            // Do not close the ledger or partially commit the day.
                            // When the administrator selects a fallback, the next
                            // scheduler tick can retry the same date safely.
                            throw new InvalidOperationException(
                                "تعذر التحضير التلقائي: يجب أن تحدد الإدارة حالة افتراضية نشطة في الإعدادات لعدم وجود تحضير أمس.");
                        }
                        statusId = configuredDefault.Value.Id;
                        statusName = configuredDefault.Value.Name;
                        statusSymbol = configuredDefault.Value.Symbol;
                        statusColor = configuredDefault.Value.Color;
                        defaulted++;
                    }
                    else
                    {
                        statusId = yReader.GetString(0);
                        statusName = yReader.GetString(1);
                        statusSymbol = yReader.GetString(2);
                        statusColor = yReader.GetString(3);
                    }
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
                insert.Parameters.AddWithValue("$actor", SystemActorUserId);
                insert.Parameters.AddWithValue("$now", startedUtc.ToString("O"));
                insert.Parameters.AddWithValue("$idem", $"AUTO20H-{Iso(businessDate)}-{person.PersonId}");
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                copied++;
            }

            DateTimeOffset completedUtc = _time.GetUtcNow();
            string summary = $"تشغيل {source} ليوم {Iso(businessDate)}: اعتُمد {copied}، منها {defaulted} بالحالة الافتراضية لعدم وجود تحضير أمس، استثناء نقل {skipTransfer}، تحضّر يدوياً مسبقاً {skipManual}.";

            // Actor attribution + tamper-evident audit event inside the SAME transaction (07.03).
            await Bridge.BridgeAuditWriter.WriteAsync(connection, tx, _time,
                operationCode: "AUTO_ATTENDANCE_20H",
                operationArabicName: "التحضير التلقائي الساعة الثامنة مساءً",
                actorUserId: SystemActorUserId,
                actorUsername: "النظام (جدولة 20:00)",
                actorDisplayName: null,
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
                copied, skipNoYesterday, skipTransfer, skipManual, 0, summary);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the EightPmScheduler screen facts: last ledger rows plus next scheduled due time.</summary>
    public async Task<LedgerSnapshot> ReadStatusAsync(int take, CancellationToken ct = default)
    {
        DateTimeOffset nowUtc = _time.GetUtcNow();
        DateTime localNow = LocalNow();
        DateOnly today = DateOnly.FromDateTime(localNow);
        bool dayDue = localNow.TimeOfDay >= new TimeSpan(DefaultDueHour, 0, 0);
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
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), 0, 0, reader.GetString(6)));
            }
        }

        bool lastObservedValid = true;
        string? lastObserved = await GetSettingAsync("LastObservedUtcTimestamp", ct).ConfigureAwait(false);
        if (lastObserved is not null && DateTimeOffset.TryParse(lastObserved, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset stored) && stored > nowUtc + ClockRollbackTolerance)
            lastObservedValid = false;

        DateOnly? lastCompleted = runs.Count > 0 ? runs[0].BusinessDate : null;
        int pending = 0;
        if (lastCompleted is not null)
        {
            DateOnly start = lastCompleted.Value.AddDays(1);
            DateOnly end = dayDue ? today : today.AddDays(-1);
            if (end >= start) pending = (end.DayNumber - start.DayNumber) + 1;
        }

        string nextDueWall = nextDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T20:00:00";
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

    internal static string JobKey(DateOnly businessDate) => "AUTO20H-" + Iso(businessDate);
    internal static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DateTime LocalNow() => _time.GetLocalNow().DateTime;

    internal List<DateOnly> EnumeratePendingDates(DateOnly today, bool dayDue, int catchUpDays,
        DateOnly? lastCompleted, out int unrecoverable)
    {
        unrecoverable = 0;
        DateOnly end = dayDue ? today : today.AddDays(-1);
        if (lastCompleted is null)
        {
            // Fresh database: only the current due day (if due) is eligible; no back-fill without a baseline.
            return dayDue ? [today] : [];
        }
        DateOnly firstPending = lastCompleted.Value.AddDays(1);
        if (end < firstPending) return [];
        int span = end.DayNumber - firstPending.DayNumber + 1;
        if (span > catchUpDays)
        {
            unrecoverable = span - catchUpDays;
            firstPending = end.AddDays(-(catchUpDays - 1));
        }
        var list = new List<DateOnly>();
        for (DateOnly d = firstPending; d <= end; d = d.AddDays(1)) list.Add(d);
        return list;
    }

    private async Task<AutoAttendanceRunResult?> ReadLedgerAsync(string jobKey, CancellationToken ct)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT BusinessDate, IsCatchUpRun, CopiedCount, SkippedNoYesterdayCount,
                                   SkippedTransferExemptCount, SummaryArabic
                            FROM AutoAttendanceRuns WHERE JobKey = $job;";
        cmd.Parameters.AddWithValue("$job", jobKey);
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var date = DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
        return new AutoAttendanceRunResult(jobKey, date, true, false,
            reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), 0, 0, reader.GetString(5));
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

    private async Task<int> GetSettingIntAsync(string key, int fallback, CancellationToken ct)
        => int.TryParse(await GetSettingAsync(key, ct).ConfigureAwait(false), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

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
