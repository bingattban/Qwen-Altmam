using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Domain.Policies;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Read-only, paged, parameterized daily/monthly projections over SQLCipher.
/// No writes, fabricated counts, cache-only status, or mutable identity join keys.
/// </summary>
public sealed class DailyMonthlyReadService
{
    private readonly AlTamamDbContext _db;
    public DailyMonthlyReadService(AlTamamDbContext db) => _db = db;

    private static string ReadString(JsonObject? obj, string key)
    {
        try { return obj?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static int ReadInt(JsonObject? obj, string key, int fallback)
    {
        try { return obj?[key]?.GetValue<int>() ?? fallback; }
        catch { return fallback; }
    }

    private static bool TryBusinessDate(JsonObject? payload, out DateOnly date)
    {
        string candidate = ReadString(payload, "businessDate");
        return DateOnly.TryParseExact(candidate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
    }

    private static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static BridgeResponse BadDate() => BridgeHelpers.Error(
        BridgeErrorCodes.ValidationError, "تاريخ العمل غير صالح. استخدم yyyy-MM-dd.");

    public async Task<BridgeResponse> DashboardSummaryAsync(JsonObject? payload, CancellationToken ct)
    {
        if (!TryBusinessDate(payload, out var date)) return BadDate();
        string siteId = ReadString(payload, "siteId");
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ar.StatusId,
                   EXISTS(SELECT 1 FROM PersonnelMovements m
                          WHERE m.PersonId=p.PersonId AND m.EffectiveLocalDate=$date
                            AND m.MovementType=1) AS IsInternalTransferDay,
                   COUNT(*),
                   SUM(CASE WHEN ar.IdempotencyKey LIKE 'AUTO08H-%'
                                 OR ar.IdempotencyKey LIKE 'AUTO20H-%'
                            THEN 1 ELSE 0 END)
            FROM Persons p
            LEFT JOIN PersonAssignments a ON a.AssignmentId = (
                SELECT innerA.AssignmentId FROM PersonAssignments innerA
                WHERE innerA.PersonId = p.PersonId
                  AND innerA.ValidFromLocalDate <= $date
                  AND (innerA.ValidToLocalDate IS NULL OR innerA.ValidToLocalDate > $date)
                ORDER BY innerA.ValidFromLocalDate DESC LIMIT 1
            )
            LEFT JOIN AttendanceRecords ar ON ar.PersonId = p.PersonId
                                          AND ar.AttendanceLocalDate = $date
            WHERE p.FirstRecordedLocalDate <= $date AND p.ActiveFlag = 1
              AND p.OrganizationalStatus = 1
              AND NOT EXISTS (
                SELECT 1 FROM PersonnelMovements departure
                WHERE departure.PersonId=p.PersonId
                  AND departure.MovementType IN (2,3)
                  AND departure.EffectiveLocalDate <= $date
                  AND NOT EXISTS (
                    SELECT 1 FROM PersonnelMovements comeback
                    WHERE comeback.PersonId=p.PersonId
                      AND comeback.MovementType=4
                      AND comeback.EffectiveLocalDate >= departure.EffectiveLocalDate
                      AND comeback.EffectiveLocalDate <= $date))
              AND ($site = '' OR COALESCE(a.SiteId, '') = $site)
            GROUP BY ar.StatusId, IsInternalTransferDay;
            """;
        cmd.Parameters.AddWithValue("$date", Iso(date));
        cmd.Parameters.AddWithValue("$site", siteId);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        long force = 0, unprepared = 0, transferDay = 0, autoPrepared = 0;
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long amount = reader.GetInt64(2);
                force += amount;
                if (reader.GetInt64(1) != 0)
                {
                    // Membership counts from transfer date; ordinary attendance
                    // at destination starts on the following business day.
                    transferDay += amount;
                }
                else if (reader.IsDBNull(0)) unprepared += amount;
                else totals[reader.GetString(0)] = totals.GetValueOrDefault(reader.GetString(0)) + amount;
                if (reader.GetInt64(1) == 0) autoPrepared += reader.GetInt64(3);
            }
        }

        using var statusesCmd = connection.CreateCommand();
        statusesCmd.CommandText = """
            SELECT StatusId, VisibleName, ShortSymbol, ChosenColorHex
            FROM AttendanceStatusDefinitions
            WHERE ActiveFlag = 1 ORDER BY SortOrder, StatusId;
            """;
        var statuses = new JsonArray();
        using (var reader = await statusesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string key = reader.GetString(0);
                statuses.Add(new JsonObject
                {
                    ["statusId"] = key,
                    ["name"] = reader.GetString(1),
                    ["symbol"] = reader.GetString(2),
                    ["color"] = reader.GetString(3),
                    ["count"] = totals.GetValueOrDefault(key)
                });
            }
        }
        return BridgeHelpers.Ok(new JsonObject
        {
            ["businessDate"] = Iso(date),
            ["siteId"] = siteId,
            ["totalForce"] = force,
            ["unprepared"] = unprepared,
            ["automaticallyPrepared"] = autoPrepared,
            ["internalTransferDay"] = transferDay,
            ["attendanceEligible"] = force - transferDay,
            ["completionPercentage"] = force == transferDay ? 0 :
                (int)Math.Round(100m * (force - transferDay - unprepared) /
                    (force - transferDay), MidpointRounding.AwayFromZero),
            ["statusCounts"] = statuses
        });
    }

    public async Task<BridgeResponse> DailyRosterAsync(JsonObject? payload, CancellationToken ct)
    {
        if (!TryBusinessDate(payload, out var date)) return BadDate();
        string siteId = ReadString(payload, "siteId");
        string statusId = ReadString(payload, "statusId");
        string rawTerm = ReadString(payload, "term");
        if(rawTerm.Length > 100)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,"البحث لا يتجاوز 100 حرف.");
        string term = ArabicTextNormalizer.NormalizeForSearch(rawTerm);
        int page = Math.Clamp(ReadInt(payload, "page", 1), 1, 100000);
        int pageSize = Math.Clamp(ReadInt(payload, "pageSize", 50), 1, 200);
        const string fromSql = """
            FROM Persons p
            LEFT JOIN PersonAssignments a ON a.AssignmentId = (
                SELECT x.AssignmentId FROM PersonAssignments x
                WHERE x.PersonId = p.PersonId AND x.ValidFromLocalDate <= $date
                  AND (x.ValidToLocalDate IS NULL OR x.ValidToLocalDate > $date)
                ORDER BY x.ValidFromLocalDate DESC LIMIT 1)
            LEFT JOIN Sites s ON s.SiteId = a.SiteId
            LEFT JOIN AttendanceRecords ar ON ar.PersonId = p.PersonId
                                          AND ar.AttendanceLocalDate = $date
            LEFT JOIN PersonnelMovements m ON m.PersonId=p.PersonId
                AND m.EffectiveLocalDate=$date AND m.MovementType=1
            WHERE p.FirstRecordedLocalDate <= $date AND p.ActiveFlag = 1
              AND p.OrganizationalStatus = 1
              AND NOT EXISTS (
                SELECT 1 FROM PersonnelMovements departure
                WHERE departure.PersonId=p.PersonId
                  AND departure.MovementType IN (2,3)
                  AND departure.EffectiveLocalDate <= $date
                  AND NOT EXISTS (
                    SELECT 1 FROM PersonnelMovements comeback
                    WHERE comeback.PersonId=p.PersonId
                      AND comeback.MovementType=4
                      AND comeback.EffectiveLocalDate >= departure.EffectiveLocalDate
                      AND comeback.EffectiveLocalDate <= $date))
              AND ($site = '' OR COALESCE(a.SiteId, '') = $site)
              AND ($term = '' OR p.NormalizedName LIKE $term ESCAPE '\'
                   OR p.NormalizedServiceNumberA LIKE $term ESCAPE '\')
              AND ($status = '' OR ar.StatusId = $status
                   OR ($status = '__UNPREPARED__' AND ar.StatusId IS NULL
                        AND m.MovementId IS NULL)
                   OR ($status = '__AUTO_PREPARED__' AND m.MovementId IS NULL
                        AND (ar.IdempotencyKey LIKE 'AUTO08H-%'
                             OR ar.IdempotencyKey LIKE 'AUTO20H-%'))
                   OR ($status = '__TRANSFER_DAY__' AND m.MovementId IS NOT NULL)
                   OR ($status = '__ELIGIBLE__' AND m.MovementId IS NULL))
            """;
        using var connection = _db.CreateOpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) " + fromSql;
        BindFilters(count, Iso(date), siteId, statusId, term);
        long total = Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.PersonId, p.CurrentServiceNumberA, p.CurrentVisibleName,
                   COALESCE(s.VisibleName, 'غير محدد'),
                   ar.StatusId, ar.StatusNameSnapshot, ar.StatusColorSnapshot,
                    m.MovementId, m.SourceSiteNameSnapshot, m.DestinationSiteNameSnapshot
            """ + "\n" + fromSql + "\n" + """
            ORDER BY p.CurrentVisibleName, p.PersonId LIMIT $limit OFFSET $offset;
            """;
        BindFilters(cmd, Iso(date), siteId, statusId, term);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);
        var rows = new JsonArray();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new JsonObject
            {
                ["personId"] = reader.GetString(0),
                ["serviceNumberA"] = reader.GetString(1),
                ["name"] = reader.GetString(2),
                ["siteName"] = reader.GetString(3),
                ["statusId"] = reader.IsDBNull(7)
                    ? (reader.IsDBNull(4) ? null : reader.GetString(4))
                    : "__TRANSFER_DAY__",
                ["statusName"] = reader.IsDBNull(7)
                    ? (reader.IsDBNull(5) ? "غير محضر" : reader.GetString(5))
                    : "تم نقله من " + reader.GetString(8) + " إلى " + reader.GetString(9),
                ["statusColor"] = reader.IsDBNull(7)
                    ? (reader.IsDBNull(6) ? null : reader.GetString(6))
                    : "#D8B4FE",
                ["transferDay"] = !reader.IsDBNull(7)
            });
        }
        return BridgeHelpers.Ok(new JsonObject
        {
            ["items"] = rows, ["total"] = total, ["page"] = page,
            ["pageSize"] = pageSize, ["hasNext"] = (long)page * pageSize < total
        });
    }

    private static void BindFilters(SqliteCommand cmd, string date, string site, string status, string term)
    {
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$site", site);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$term", term.Length == 0 ? "" :
            "%" + PersonRosterService.EscapeLike(term) + "%");
    }

    public async Task<BridgeResponse> ArchivePeriodsAsync(CancellationToken ct)
    {
        // A personnel roster is an archive even when nobody was prepared yet.
        // Attendance/movements alone omit entire months with real memberships
        // but zero committed events. Include every calendar month intersecting
        // a dated assignment [ValidFromLocalDate, ValidToLocalDate), capped at
        // today for open assignments. Never invent attendance or future months.
        var periods = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _db.CreateOpenConnection();
        using (var events = connection.CreateCommand())
        {
            events.CommandText = """
                SELECT AttendanceLocalDate AS eventDate FROM AttendanceRecords
                UNION ALL
                SELECT EffectiveLocalDate AS eventDate FROM PersonnelMovements;
                """;
            using var reader = await events.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string date = reader.GetString(0);
                if (date.Length >= 7) periods.Add(date[..7]);
            }
        }
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly tomorrow = today.AddDays(1);
        using (var memberships = connection.CreateCommand())
        {
            memberships.CommandText = """
                SELECT ValidFromLocalDate, ValidToLocalDate
                FROM PersonAssignments ORDER BY ValidFromLocalDate;
                """;
            using var reader = await memberships.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (!DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly start))
                    continue;
                DateOnly end = tomorrow;
                if (!reader.IsDBNull(1) &&
                    DateOnly.TryParseExact(reader.GetString(1), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly closedAt) &&
                    closedAt < end) end = closedAt;
                if (start >= end) continue;
                DateOnly lastIncluded = end.AddDays(-1);
                DateOnly month = new(start.Year, start.Month, 1);
                DateOnly lastMonth = new(lastIncluded.Year, lastIncluded.Month, 1);
                for (; month <= lastMonth; month = month.AddMonths(1))
                    periods.Add(month.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            }
        }
        var ordered = new JsonArray();
        foreach (string period in periods.OrderByDescending(x => x, StringComparer.Ordinal))
            ordered.Add(period);
        return BridgeHelpers.Ok(ordered);
    }

    public async Task<BridgeResponse> MonthlyGridAsync(JsonObject? payload, CancellationToken ct)
    {
        int year = ReadInt(payload, "year", 0), month = ReadInt(payload, "month", 0);
        if (year < 1900 || year > 2200 || month < 1 || month > 12)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "اختر شهراً وسنة صالحين.");
        string siteId = ReadString(payload, "siteId");
        string rawTerm = ReadString(payload, "term");
        if (rawTerm.Length > 100 || rawTerm.Any(char.IsControl))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "البحث الشهري لا يتجاوز 100 حرف ولا يقبل أحرف التحكم.");
        string term = ArabicTextNormalizer.NormalizeForSearch(rawTerm);
        int page = Math.Clamp(ReadInt(payload, "page", 1), 1, 100000);
        int pageSize = Math.Clamp(ReadInt(payload, "pageSize", 50), 1, 100);
        var start = new DateOnly(year, month, 1);
        var next = start.AddMonths(1);
        string startIso = Iso(start), nextIso = Iso(next), endIso = Iso(next.AddDays(-1));
        // The operational current-month grid contains active, unmoved personnel.
        // Archive mode explicitly replays everyone who served during that month,
        // including departures and movement cells, without touching historical data.
        bool explicitArchive = string.Equals(payload?["archiveMode"]?.ToJsonString(),
            "true", StringComparison.Ordinal);
        DateTime localNow = DateTime.Now;
        bool liveCurrentMonth = year == localNow.Year && month == localNow.Month && !explicitArchive;
        const string fromSql = """
            FROM Persons p
            WHERE p.FirstRecordedLocalDate < $next
              AND (
                EXISTS (SELECT 1 FROM PersonAssignments a
                        WHERE a.PersonId = p.PersonId
                          AND a.ValidFromLocalDate < $next
                          AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate > $start)
                          AND ($site = '' OR a.SiteId = $site))
                OR EXISTS(SELECT 1 FROM PersonnelMovements m
                         WHERE m.PersonId=p.PersonId AND m.EffectiveLocalDate >= $start
                           AND m.EffectiveLocalDate < $next
                           AND ($site='' OR m.SourceSiteId=$site OR m.DestinationSiteId=$site))
              )
              AND ($live=0 OR (
                    p.ActiveFlag=1 AND p.OrganizationalStatus=1
                    AND NOT EXISTS (
                      SELECT 1 FROM PersonnelMovements moved
                      WHERE moved.PersonId=p.PersonId
                        AND moved.EffectiveLocalDate >= $start
                        AND moved.EffectiveLocalDate < $next
                        AND moved.MovementType IN (1,2,3)
                        AND NOT EXISTS (
                          SELECT 1 FROM PersonnelMovements returned
                          WHERE returned.PersonId=moved.PersonId
                            AND returned.MovementType=4
                            AND returned.EffectiveLocalDate >= moved.EffectiveLocalDate
                            AND returned.EffectiveLocalDate < $next))
                  ))
              -- Match the identity actually displayed for the selected month,
              -- NEVER today's mutable name/number. Two people who later reuse
              -- الرقم (ع) remain separate because the key is immutable PersonId.
              AND ($term = '' OR EXISTS(
                  SELECT 1 FROM PersonIdentityHistory h
                  WHERE h.PersonId=p.PersonId
                    AND h.ValidFromLocalDate <= $end
                    AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate > $end)
                    AND (instr(altamam_normalize_search(h.NameSnapshot),$term)>0
                      OR instr(altamam_normalize_search(h.ServiceNumberASnapshot),$term)>0)
              ))
            """;
        using var connection = _db.CreateOpenConnection();
        // Register this read-only function on this connection ONLY. A portable
        // SQLite scalar avoids schema migrations or lossy search-time snapshots.
        connection.CreateFunction<string, string>(
            "altamam_normalize_search", text => ArabicTextNormalizer.NormalizeForSearch(text),
            isDeterministic: true);
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) " + fromSql;
        BindPeriod(count, startIso, nextIso, siteId, term, endIso);
        count.Parameters.AddWithValue("$live",liveCurrentMonth?1:0);
        long total = Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));

        // A row per person for this page. Historical cells are sourced from the
        // committed date-specific attendance snapshots, not the currently selected status.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.PersonId, COALESCE((
                      SELECT h.ServiceNumberASnapshot FROM PersonIdentityHistory h
                      WHERE h.PersonId = p.PersonId AND h.ValidFromLocalDate <= $end
                        AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate > $end)
                      ORDER BY h.ValidFromLocalDate DESC LIMIT 1), 'غير موثق تاريخياً'),
                   COALESCE((
                      SELECT h.NameSnapshot FROM PersonIdentityHistory h
                      WHERE h.PersonId = p.PersonId AND h.ValidFromLocalDate <= $end
                        AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate > $end)
                      ORDER BY h.ValidFromLocalDate DESC LIMIT 1), 'غير موثق تاريخياً'),
                   COALESCE((
                      SELECT COALESCE((SELECT hn.VisibleNameSnapshot FROM SiteNameHistory hn
                                   WHERE hn.SiteId = a.SiteId AND hn.ValidFromLocalDate <= $end
                                     AND (hn.ValidToLocalDate IS NULL OR hn.ValidToLocalDate > $end)
                                   ORDER BY hn.ValidFromLocalDate DESC LIMIT 1),
                                   CASE WHEN a.SiteId='SITE-UNSPECIFIED' THEN 'غير محدد'
                                        ELSE 'غير موثق تاريخياً' END)
                      FROM PersonAssignments a
                      WHERE a.PersonId = p.PersonId AND a.ValidFromLocalDate < $next
                        AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate > $start)
                        AND ($site = '' OR a.SiteId = $site)
                      ORDER BY a.ValidFromLocalDate DESC LIMIT 1), 'غير محدد')
            """ + "\n" + fromSql + "\n" + """
            -- Sort by the projected historical name (third selected column).
            -- The immutable tie-break keeps all Excel pages in matching order.
            ORDER BY 3 COLLATE NOCASE, p.PersonId LIMIT $limit OFFSET $offset;
            """;
        BindPeriod(cmd, startIso, nextIso, siteId, term, endIso);
        cmd.Parameters.AddWithValue("$live",liveCurrentMonth?1:0);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);
        var people = new List<(string Id, JsonObject Row)>();
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new JsonObject
                {
                    ["personId"] = reader.GetString(0),
                    ["serviceNumberA"] = reader.GetString(1),
                    ["name"] = reader.GetString(2),
                    ["siteName"] = reader.GetString(3),
                    ["days"] = new JsonObject()
                };
                people.Add((reader.GetString(0), row));
            }
        }
        if (people.Count > 0)
        {
            var ids = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var person in people) ids.Add(person.Id, person.Row);
            using var cells = connection.CreateCommand();
            var holders = new List<string>();
            for (int i = 0; i < people.Count; i++)
            {
                string key = "$p" + i;
                holders.Add(key);
                cells.Parameters.AddWithValue(key, people[i].Id);
            }
            cells.CommandText = """
                SELECT ar.PersonId, ar.AttendanceLocalDate, ar.StatusNameSnapshot,
                       ar.StatusSymbolSnapshot, ar.StatusColorSnapshot, ar.SiteIdAtEffectiveDate,
                       (SELECT h.NameSnapshot FROM PersonIdentityHistory h
                        WHERE h.PersonId=ar.PersonId AND h.ValidFromLocalDate<=ar.AttendanceLocalDate
                          AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate>ar.AttendanceLocalDate)
                        ORDER BY h.ValidFromLocalDate DESC LIMIT 1),
                       (SELECT h.ServiceNumberASnapshot FROM PersonIdentityHistory h
                        WHERE h.PersonId=ar.PersonId AND h.ValidFromLocalDate<=ar.AttendanceLocalDate
                          AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate>ar.AttendanceLocalDate)
                        ORDER BY h.ValidFromLocalDate DESC LIMIT 1),
                       ar.SiteNameSnapshot
                FROM AttendanceRecords ar
                WHERE ar.AttendanceLocalDate >= $start AND ar.AttendanceLocalDate < $next
                  AND ar.PersonId IN (
                """ + string.Join(",", holders) + ") ORDER BY ar.AttendanceLocalDate;";
            cells.Parameters.AddWithValue("$start", startIso);
            cells.Parameters.AddWithValue("$next", nextIso);
            using var reader = await cells.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (siteId.Length > 0 && reader.GetString(5) != siteId) continue;
                JsonObject days = (JsonObject)ids[reader.GetString(0)]["days"]!;
                days[reader.GetString(1)] = new JsonObject
                {
                    ["name"] = reader.GetString(2),
                    ["symbol"] = reader.GetString(3),
                    ["color"] = reader.GetString(4),
                    // A month may contain more than one identity/site version.
                    // Render each dated cell from its own historical identity and site snapshot.
                    ["personName"] = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ["serviceNumberA"] = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ["siteName"] = reader.GetString(8)
                };
            }
            reader.Close();
            // A move is its own monthly cell even when no attendance was made on
            // transfer day. Show source and destination perspectives separately.
            using var movements = connection.CreateCommand();
            movements.CommandText = """
                SELECT PersonId,EffectiveLocalDate,MovementType,SourceSiteId,
                       SourceSiteNameSnapshot,DestinationSiteId,
                       COALESCE(DestinationSiteNameSnapshot,DestinationExternalName,''),
                       PersonNameSnapshot,ServiceNumberASnapshot
                FROM PersonnelMovements
                WHERE EffectiveLocalDate >= $start AND EffectiveLocalDate < $next
                  AND PersonId IN (
                """ + string.Join(",",holders) + ") ORDER BY EffectiveLocalDate;";
            movements.Parameters.AddWithValue("$start",startIso);
            movements.Parameters.AddWithValue("$next",nextIso);
            for(int i=0;i<people.Count;i++)
                movements.Parameters.AddWithValue("$p"+i,people[i].Id);
            using var movementReader=await movements.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await movementReader.ReadAsync(ct).ConfigureAwait(false))
            {
                string sourceId=movementReader.GetString(3);
                string? targetId=movementReader.IsDBNull(5)?null:movementReader.GetString(5);
                if(siteId.Length>0 && siteId!=sourceId && siteId!=targetId)continue;
                string before=movementReader.GetString(4),after=movementReader.GetString(6);
                int movementType=movementReader.GetInt32(2);
                // Separation must remain visible historically even though the
                // person is no longer in today's active roster.
                string moveMark=movementType==1?"نقل (د)":movementType==2?"نقل (خ)":"";
                string label=movementType==3?"مفصول":
                    movementType==4?"عاد إلى القوة في "+after:
                    moveMark+" — "+(siteId.Length==0
                        ?"تم نقله من "+before+" إلى "+after
                        :siteId==sourceId?"تم نقله إلى "+after:"تم نقله من "+before);
                JsonObject days=(JsonObject)ids[movementReader.GetString(0)]["days"]!;
                string movementDate=movementReader.GetString(1);
                if(days[movementDate] is null)
                    days[movementDate]=new JsonObject{
                        ["name"]=label,
                        ["symbol"]=movementType==3?"ف":movementType==4?"عودة":moveMark,
                        ["color"]=movementType==3?"#64748B":movementType==4?"#16A34A":"#D8B4FE",
                        ["kind"]=movementType==3?"separation":movementType==4?"return":"movement",
                        ["personName"]=movementReader.GetString(7),
                        ["serviceNumberA"]=movementReader.GetString(8),
                        ["siteName"]=siteId.Length>0 && siteId==targetId ? after : before
                    };
                else if(movementType==4)
                {
                    // Return and ordinary attendance may both occur on admission
                    // date; preserve both facts in the SAME historical cell.
                    var existing=(JsonObject)days[movementDate]!;
                    existing["movementNote"]=label;
                    existing["movementKind"]="return";
                }
            }
        }
        var rows = new JsonArray();
        foreach (var person in people) rows.Add(person.Row);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["year"] = year, ["month"] = month,
            ["daysInMonth"] = DateTime.DaysInMonth(year, month),
            ["items"] = rows, ["total"] = total, ["page"] = page,
            ["hasNext"] = (long)page * pageSize < total
        });
    }

    /// <summary>
    /// Explains one historical monthly cell without mutating effective dates or records.
    /// Attendance and movement details come from committed snapshots, not today's catalogs.
    /// </summary>
    public async Task<BridgeResponse> MonthlyCellDetailsAsync(JsonObject? payload, CancellationToken ct)
    {
        string personId = ReadString(payload, "personId");
        string requestedSite = ReadString(payload, "siteId");
        if (personId.Length is < 1 or > 128 || !TryBusinessDate(payload, out var date))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "اختر فرداً وتاريخاً صالحين لعرض تفاصيل اليوم.");
        string dateIso = Iso(date);
        using var connection = _db.CreateOpenConnection();
        using var person = connection.CreateCommand();
        person.CommandText = """
            SELECT COALESCE((SELECT h.NameSnapshot FROM PersonIdentityHistory h
                   WHERE h.PersonId=p.PersonId AND h.ValidFromLocalDate <= $date
                     AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate > $date)
                   ORDER BY h.ValidFromLocalDate DESC LIMIT 1),'غير موثق تاريخياً'),
                   COALESCE((SELECT h.ServiceNumberASnapshot FROM PersonIdentityHistory h
                   WHERE h.PersonId=p.PersonId AND h.ValidFromLocalDate <= $date
                     AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate > $date)
                   ORDER BY h.ValidFromLocalDate DESC LIMIT 1),'غير موثق تاريخياً')
            FROM Persons p WHERE p.PersonId=$person AND p.FirstRecordedLocalDate <= $date;
            """;
        person.Parameters.AddWithValue("$person", personId);
        person.Parameters.AddWithValue("$date", dateIso);
        string name, serviceNumber;
        using (var r = await person.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
                return BridgeHelpers.Error(BridgeErrorCodes.MissingData, "لا يوجد فرد بهذا المعرف في التاريخ المحدد.");
            name = r.GetString(0);
            serviceNumber = r.GetString(1);
        }

        string? assignedSiteId = null;
        string siteName = "غير محدد";
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT a.SiteId, COALESCE((SELECT h.VisibleNameSnapshot FROM SiteNameHistory h
                       WHERE h.SiteId=a.SiteId AND h.ValidFromLocalDate <= $date
                         AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate > $date)
                       ORDER BY h.ValidFromLocalDate DESC LIMIT 1),
                    CASE WHEN a.SiteId='SITE-UNSPECIFIED' THEN 'غير محدد'
                         ELSE 'غير موثق تاريخياً' END)
                FROM PersonAssignments a JOIN Sites s ON s.SiteId=a.SiteId
                WHERE a.PersonId=$person AND a.ValidFromLocalDate <= $date
                  AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate > $date)
                ORDER BY a.ValidFromLocalDate DESC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$person", personId);
            cmd.Parameters.AddWithValue("$date", dateIso);
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                assignedSiteId = r.GetString(0);
                siteName = r.GetString(1);
            }
        }

        JsonObject? attendance = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT StatusId,StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                       SiteIdAtEffectiveDate,SiteNameSnapshot,Source,CreatedByUserId,
                       CommittedAtUtc,RevisionNumber
                FROM AttendanceRecords WHERE PersonId=$person AND AttendanceLocalDate=$date;
                """;
            cmd.Parameters.AddWithValue("$person", personId);
            cmd.Parameters.AddWithValue("$date", dateIso);
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
                attendance = new JsonObject
                {
                    ["statusId"] = r.GetString(0), ["statusName"] = r.GetString(1),
                    ["symbol"] = r.GetString(2), ["color"] = r.GetString(3),
                    ["siteId"] = r.GetString(4), ["siteName"] = r.GetString(5),
                    ["source"] = r.GetString(6), ["actorUserId"] = r.GetString(7),
                    ["committedAtUtc"] = r.GetString(8), ["revisionNumber"] = r.GetInt32(9)
                };
        }

        JsonObject? movement = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT MovementId,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                       DestinationSiteId,DestinationSiteNameSnapshot,DestinationExternalName,
                       SeparationReason,CommittedAtUtc,ActorUserId
                FROM PersonnelMovements WHERE PersonId=$person AND EffectiveLocalDate=$date
                ORDER BY CommittedAtUtc DESC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$person", personId);
            cmd.Parameters.AddWithValue("$date", dateIso);
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
                movement = new JsonObject
                {
                    ["movementId"] = r.GetString(0), ["type"] = r.GetInt32(1),
                    ["sourceSiteId"] = r.GetString(2), ["sourceSiteName"] = r.GetString(3),
                    ["destinationSiteId"] = r.IsDBNull(4) ? null : r.GetString(4),
                    ["destinationSiteName"] = r.IsDBNull(5) ? null : r.GetString(5),
                    ["destinationExternalName"] = r.IsDBNull(6) ? null : r.GetString(6),
                    ["separationReason"] = r.IsDBNull(7) ? null : r.GetString(7),
                    ["committedAtUtc"] = r.GetString(8), ["actorUserId"] = r.GetString(9)
                };
        }

        // The UI's site filter is a security-sensitive historical perspective.
        // Do not return a record merely because a PersonId from another site was submitted.
        bool matchesSite = requestedSite.Length == 0 || assignedSiteId == requestedSite ||
            attendance?["siteId"]?.GetValue<string>() == requestedSite ||
            movement?["sourceSiteId"]?.GetValue<string>() == requestedSite ||
            movement?["destinationSiteId"]?.GetValue<string>() == requestedSite;
        if ((!matchesSite) || (assignedSiteId is null && attendance is null && movement is null))
            return BridgeHelpers.Error(BridgeErrorCodes.MissingData, "لا توجد واقعة لهذا الفرد في الموقع والتاريخ المحددين.");

        var revisions = new JsonArray();
        if (attendance is not null)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT BeforeStatusNameSnapshot,AfterStatusNameSnapshot,Reason,ActorUserId,
                       ExecutedAtUtc,AuthorizationMethod
                FROM AttendanceRevisions WHERE PersonId=$person AND TargetLocalDate=$date
                ORDER BY RevisionId LIMIT 100;
                """;
            cmd.Parameters.AddWithValue("$person", personId);
            cmd.Parameters.AddWithValue("$date", dateIso);
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                revisions.Add(new JsonObject
                {
                    ["beforeStatus"] = r.GetString(0), ["afterStatus"] = r.GetString(1),
                    ["reason"] = r.GetString(2), ["actorUserId"] = r.GetString(3),
                    ["executedAtUtc"] = r.GetString(4), ["authorizationMethod"] = r.GetString(5)
                });
        }
        return BridgeHelpers.Ok(new JsonObject
        {
            ["personId"] = personId, ["businessDate"] = dateIso,
            ["personName"] = name, ["serviceNumberA"] = serviceNumber,
            ["siteId"] = assignedSiteId, ["siteName"] = siteName,
            ["attendance"] = attendance, ["movement"] = movement,
            ["revisions"] = revisions
        });
    }

    private static void BindPeriod(SqliteCommand cmd, string start, string next,
        string site, string term, string end)
    {
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$next", next);
        cmd.Parameters.AddWithValue("$site", site);
        cmd.Parameters.AddWithValue("$term", term);
        cmd.Parameters.AddWithValue("$end", end);
    }
}
