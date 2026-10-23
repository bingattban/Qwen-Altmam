using System.Globalization;
using System.Text.Json.Nodes;
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
            SELECT ar.StatusId, COUNT(*)
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
              AND ($site = '' OR COALESCE(a.SiteId, '') = $site)
            GROUP BY ar.StatusId;
            """;
        cmd.Parameters.AddWithValue("$date", Iso(date));
        cmd.Parameters.AddWithValue("$site", siteId);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        long force = 0, unprepared = 0;
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long amount = reader.GetInt64(1);
                force += amount;
                if (reader.IsDBNull(0)) unprepared += amount;
                else totals[reader.GetString(0)] = amount;
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
            ["statusCounts"] = statuses
        });
    }

    public async Task<BridgeResponse> DailyRosterAsync(JsonObject? payload, CancellationToken ct)
    {
        if (!TryBusinessDate(payload, out var date)) return BadDate();
        string siteId = ReadString(payload, "siteId");
        string statusId = ReadString(payload, "statusId");
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
            WHERE p.FirstRecordedLocalDate <= $date AND p.ActiveFlag = 1
              AND p.OrganizationalStatus = 1
              AND ($site = '' OR COALESCE(a.SiteId, '') = $site)
              AND ($status = '' OR ar.StatusId = $status)
            """;
        using var connection = _db.CreateOpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) " + fromSql;
        BindFilters(count, Iso(date), siteId, statusId);
        long total = Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.PersonId, p.CurrentServiceNumberA, p.CurrentVisibleName,
                   COALESCE(s.VisibleName, 'غير محدد'),
                   ar.StatusId, ar.StatusNameSnapshot, ar.StatusColorSnapshot
            """ + fromSql + """
            ORDER BY p.CurrentVisibleName, p.PersonId LIMIT $limit OFFSET $offset;
            """;
        BindFilters(cmd, Iso(date), siteId, statusId);
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
                ["statusId"] = reader.IsDBNull(4) ? null : reader.GetString(4),
                ["statusName"] = reader.IsDBNull(5) ? "غير محضر" : reader.GetString(5),
                ["statusColor"] = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return BridgeHelpers.Ok(new JsonObject
        {
            ["items"] = rows, ["total"] = total, ["page"] = page,
            ["pageSize"] = pageSize, ["hasNext"] = (long)page * pageSize < total
        });
    }

    private static void BindFilters(SqliteCommand cmd, string date, string site, string status)
    {
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$site", site);
        cmd.Parameters.AddWithValue("$status", status);
    }

    public async Task<BridgeResponse> ArchivePeriodsAsync(CancellationToken ct)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT SUBSTR(AttendanceLocalDate, 1, 7) AS period
            FROM AttendanceRecords ORDER BY period DESC;
            """;
        var result = new JsonArray();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return BridgeHelpers.Ok(result);
    }

    public async Task<BridgeResponse> MonthlyGridAsync(JsonObject? payload, CancellationToken ct)
    {
        int year = ReadInt(payload, "year", 0), month = ReadInt(payload, "month", 0);
        if (year < 1900 || year > 2200 || month < 1 || month > 12)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "اختر شهراً وسنة صالحين.");
        string siteId = ReadString(payload, "siteId");
        int page = Math.Clamp(ReadInt(payload, "page", 1), 1, 100000);
        int pageSize = Math.Clamp(ReadInt(payload, "pageSize", 50), 1, 100);
        var start = new DateOnly(year, month, 1);
        var next = start.AddMonths(1);
        string startIso = Iso(start), nextIso = Iso(next), endIso = Iso(next.AddDays(-1));
        const string fromSql = """
            FROM Persons p
            WHERE p.FirstRecordedLocalDate < $next
              AND EXISTS (SELECT 1 FROM PersonAssignments a
                          WHERE a.PersonId = p.PersonId
                            AND a.ValidFromLocalDate < $next
                            AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate > $start)
                            AND ($site = '' OR a.SiteId = $site))
            """;
        using var connection = _db.CreateOpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) " + fromSql;
        BindPeriod(count, startIso, nextIso, siteId);
        long total = Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));

        // A row per person for this page. Historical cells are sourced from the
        // committed date-specific attendance snapshots, not the currently selected status.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.PersonId, COALESCE((
                      SELECT h.ServiceNumberASnapshot FROM PersonIdentityHistory h
                      WHERE h.PersonId = p.PersonId AND h.ValidFromLocalDate <= $end
                      ORDER BY h.ValidFromLocalDate DESC LIMIT 1), p.CurrentServiceNumberA),
                   COALESCE((
                      SELECT h.NameSnapshot FROM PersonIdentityHistory h
                      WHERE h.PersonId = p.PersonId AND h.ValidFromLocalDate <= $end
                      ORDER BY h.ValidFromLocalDate DESC LIMIT 1), p.CurrentVisibleName),
                   COALESCE((
                      SELECT s.VisibleName FROM PersonAssignments a JOIN Sites s ON s.SiteId = a.SiteId
                      WHERE a.PersonId = p.PersonId AND a.ValidFromLocalDate < $next
                        AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate > $start)
                        AND ($site = '' OR a.SiteId = $site)
                      ORDER BY a.ValidFromLocalDate DESC LIMIT 1), 'غير محدد')
            """ + fromSql + """
            ORDER BY p.CurrentVisibleName, p.PersonId LIMIT $limit OFFSET $offset;
            """;
        BindPeriod(cmd, startIso, nextIso, siteId);
        cmd.Parameters.AddWithValue("$end", endIso);
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
                       ar.StatusSymbolSnapshot, ar.StatusColorSnapshot, ar.SiteIdAtEffectiveDate
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
                    ["color"] = reader.GetString(4)
                };
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

    private static void BindPeriod(SqliteCommand cmd, string start, string next, string site)
    {
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$next", next);
        cmd.Parameters.AddWithValue("$site", site);
    }
}
