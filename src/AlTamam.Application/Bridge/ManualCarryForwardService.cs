using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Explicit single/bulk carry-forward from yesterday for TODAY's unprepared
/// personnel only. Requires the normal immutable preview token and two-step
/// confirmation. The 20:00 scheduler retains its independent authority.
/// </summary>
public sealed class ManualCarryForwardService
{
    private const string Operation = "ATTENDANCE-carry";
    private const int MaxBatch = 100;
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    private sealed record Selection(
        string PersonId, string Name, string Number, string AssignmentId, string SiteId,
        string SiteName, string StatusId, string StatusName, string Symbol, string Color,
        bool UsedDefault);

    public ManualCarryForwardService(AlTamamDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    private string Today() => _time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private bool IsAfterCutoff() => _time.GetLocalNow().TimeOfDay >= TimeSpan.FromHours(20);
    private static string Read(JsonObject? value, string key)
    {
        try { return value?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static string[] ReadPeople(JsonObject? payload)
    {
        if (payload?["personIds"] is not JsonArray array || array.Count is < 1 or > MaxBatch)
            return [];
        try
        {
            string[] ids = array.Select(n => n?.GetValue<string>()?.Trim() ?? "").ToArray();
            return ids.All(id => !string.IsNullOrWhiteSpace(id)) &&
                   ids.Distinct(StringComparer.Ordinal).Count() == ids.Length ? ids : [];
        }
        catch { return []; }
    }

    private static BridgeResponse Error(string message, string code = BridgeErrorCodes.ConflictStale) =>
        BridgeHelpers.Error(code, message);

    private static async Task<bool> IsClosedAsync(SqliteConnection connection, SqliteTransaction? tx,
        string date, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM PeriodClosures WHERE IsClosed=1
                AND ((PeriodType='DAY' AND PeriodKey=$date)
                  OR (PeriodType='MONTH' AND PeriodKey=$month)));
            """;
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$month", date[..7]);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0;
    }

    private async Task<(Selection? Person, string? Reason)> SelectAsync(SqliteConnection connection,
        SqliteTransaction? tx, string personId, string date, string siteFilter, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT p.CurrentVisibleName, p.CurrentServiceNumberA,
                   a.AssignmentId, a.SiteId, s.VisibleName,
                   current.RecordId,
                   previous.StatusId, previous.StatusNameSnapshot,
                   previous.StatusSymbolSnapshot, previous.StatusColorSnapshot,
                   def.StatusId, def.VisibleName, def.ShortSymbol, def.ChosenColorHex
            FROM Persons p
            JOIN PersonAssignments a ON a.PersonId=p.PersonId
                AND a.ValidFromLocalDate <= $date
                AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate > $date)
            JOIN Sites s ON s.SiteId=a.SiteId
            LEFT JOIN AttendanceRecords current ON current.PersonId=p.PersonId
                AND current.AttendanceLocalDate=$date
            LEFT JOIN AttendanceRecords previous ON previous.PersonId=p.PersonId
                AND previous.AttendanceLocalDate=$yesterday
            LEFT JOIN AppSettings setting ON setting.SettingKey=$setting
            LEFT JOIN AttendanceStatusDefinitions def
                ON def.StatusId=setting.SettingValue AND def.ActiveFlag=1
            WHERE p.PersonId=$id AND p.ActiveFlag=1 AND p.OrganizationalStatus=1
                AND p.FirstRecordedLocalDate <= $date
                AND NOT EXISTS(SELECT 1 FROM PersonnelMovements m
                    WHERE m.PersonId=p.PersonId AND m.EffectiveLocalDate=$date
                      AND m.MovementType IN (1,2,3));
            """;
        cmd.Parameters.AddWithValue("$id", personId);
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$yesterday", DateOnly.ParseExact(date, "yyyy-MM-dd",
            CultureInfo.InvariantCulture).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$setting", AutoAttendancePolicyService.DefaultStatusKey);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (null, "الفرد غير نشط أو يوم نقله أو انتسابه غير صالح");
        string name = reader.GetString(0), number = reader.GetString(1);
        string label = $"«{name}» (الرقم (ع): {number})";
        if (!reader.IsDBNull(5))
            return (null, label + ": سبق تحضيره اليوم");
        if (siteFilter.Length > 0 && reader.GetString(3) != siteFilter)
            return (null, label + ": لا ينتمي إلى الموقع المختار");
        bool fallback = reader.IsDBNull(6);
        if (fallback && reader.IsDBNull(10))
            return (null, label + ": لا توجد حالة في أمس ولم تحدد الإدارة حالة افتراضية نشطة");
        int offset = fallback ? 10 : 6;
        var choice = new Selection(personId, name, number,
            reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(offset), reader.GetString(offset+1),
            reader.GetString(offset+2), reader.GetString(offset+3), fallback);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return (null, label + ": فترات انتساب متداخلة تمنع التحضير");
        return (choice, null);
    }

    public async Task<BridgeResponse> PreviewAsync(JsonObject? payload, BridgePrincipal actor,
        CancellationToken ct)
    {
        string date = Read(payload, "businessDate");
        string siteFilter = Read(payload, "siteId");
        string[] ids = ReadPeople(payload);
        if (ids.Length == 0)
            return Error("اختر فرداً أو أكثر (حتى 100) دون تكرار.",
                BridgeErrorCodes.ValidationError);
        if (date != Today() || IsAfterCutoff())
            return Error("اعتماد حالة أمس يدويًا متاح لليوم الحالي قبل الساعة الثامنة فقط.",
                BridgeErrorCodes.HistoricDayLocked);

        using var connection = _db.CreateOpenConnection();
        if (await IsClosedAsync(connection, null, date, ct).ConfigureAwait(false))
            return Error("اليوم أو الشهر مغلق؛ لا يمكن اعتماد التحضير.",
                BridgeErrorCodes.HistoricDayLocked);
        var people = new JsonArray();
        foreach (string id in ids)
        {
            var (person, error) = await SelectAsync(connection, null, id, date, siteFilter, ct)
                .ConfigureAwait(false);
            if (person is null) return Error("تعذر اعتماد " + (error ?? "الفرد " + id) + "؛ أُلغيت الدفعة.");
            people.Add(ToJson(person));
        }
        var sealedPreview = new JsonObject
        {
            ["businessDate"] = date,
            ["siteId"] = siteFilter,
            ["mode"] = "carry",
            ["persons"] = people
        };
        string token = BridgePreviewTokens.Issue(_db, _time, Operation,
            actor.UserId, sealedPreview, 0, ct);
        JsonObject response = (JsonObject)sealedPreview.DeepClone();
        response["previewToken"] = token;
        response["count"] = ids.Length;
        response["statusName"] = "حسب حالة كل فرد في أمس أو الحالة الافتراضية";
        return BridgeHelpers.Ok(response);
    }

    private static JsonObject ToJson(Selection selection) => new()
    {
        ["personId"] = selection.PersonId,
        ["name"] = selection.Name,
        ["serviceNumberA"] = selection.Number,
        ["assignmentId"] = selection.AssignmentId,
        ["siteId"] = selection.SiteId,
        ["siteName"] = selection.SiteName,
        ["previousStatusName"] = "غير محضر",
        ["proposedStatusId"] = selection.StatusId,
        ["proposedStatusName"] = selection.StatusName,
        ["proposedStatusSymbol"] = selection.Symbol,
        ["proposedStatusColor"] = selection.Color,
        ["usedDefault"] = selection.UsedDefault
    };

    public async Task<BridgeResponse> CommitAsync(JsonObject? payload, BridgePrincipal actor,
        CancellationToken ct)
    {
        string token = Read(payload, "previewToken");
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c)))
            return Error("رمز معاينة صالح مطلوب.", BridgeErrorCodes.ValidationError);
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            // A successful commit can be retried after the manual lock:
            // only an identical durable receipt replays, never a new write.
            var (replay, receipt) = await MutationIdempotency.StartAsync(
                connection, tx, "CommitManualCarryForward", actor.UserId, payload, ct)
                .ConfigureAwait(false);
            if (replay is not null) return replay;
            if (IsAfterCutoff()) return Error("انتهت مهلة الاعتماد اليدوي الساعة الثامنة.",
                BridgeErrorCodes.HistoricDayLocked);
            using var find = connection.CreateCommand();
            find.Transaction = tx;
            find.CommandText = """
                SELECT PayloadCanonicalJson FROM PreviewTokens
                WHERE Token=$token AND Operation=$operation AND ActorUserId=$actor
                  AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
                """;
            find.Parameters.AddWithValue("$token", token);
            find.Parameters.AddWithValue("$operation", Operation);
            find.Parameters.AddWithValue("$actor", actor.UserId);
            find.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
            string? raw = await find.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (raw is null) return Error("المعاينة منتهية أو مستخدمة سابقًا.");
            var preview = JsonNode.Parse(raw) as JsonObject;
            string date = Read(preview, "businessDate"), site = Read(preview, "siteId");
            if (date != Today() || Read(preview, "mode") != "carry" ||
                preview?["persons"] is not JsonArray list || list.Count is <1 or > MaxBatch)
                return Error("المعاينة غير صالحة لهذا اليوم.", BridgeErrorCodes.HistoricDayLocked);
            if (await IsClosedAsync(connection, tx, date, ct).ConfigureAwait(false))
                return Error("أُغلق اليوم بعد المعاينة.", BridgeErrorCodes.HistoricDayLocked);
            var selected = new List<Selection>();
            foreach (var item in list)
            {
                if (item is not JsonObject expected)
                    return Error("تحتوي المعاينة على بيانات غير صالحة.");
                string personId = Read(expected, "personId");
                var (current, error) = await SelectAsync(connection, tx, personId, date, site, ct)
                    .ConfigureAwait(false);
                if (current is null) return Error("تغيرت بيانات " + (error ?? personId) +
                    " بعد المعاينة؛ لم يُعتمد أي فرد.");
                if (!JsonNode.DeepEquals(ToJson(current), expected))
                    return Error($"تغيرت حالة أمس أو موقع «{current.Name}» (الرقم (ع): {current.Number}) بعد المعاينة؛ أعد المراجعة.");
                selected.Add(current);
            }
            if (selected.Select(p=>p.PersonId).Distinct(StringComparer.Ordinal).Count()!=selected.Count)
                return Error("قائمة المعاينة تحتوي فرداً مكرراً؛ أُلغيت العملية.");
            long? consumed = await BridgePreviewTokens.ConsumeAsync(_db, _time,
                connection, tx, token, Operation, actor.UserId, 0, ct).ConfigureAwait(false);
            if (consumed != 0) return Error("المعاينة منتهية أو سبق اعتمادها.");

            string timestamp = _time.GetUtcNow().ToString("O");
            string correlation = Guid.NewGuid().ToString("N");
            foreach (Selection person in selected)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO AttendanceRecords
                      (PersonId,AttendanceLocalDate,StatusId,StatusNameSnapshot,
                       StatusSymbolSnapshot,StatusColorSnapshot,AssignmentIdAtEffectiveDate,
                       SiteIdAtEffectiveDate,SiteNameSnapshot,Source,CreatedByUserId,
                       CommittedAtUtc,RevisionNumber,IdempotencyKey)
                    VALUES ($id,$date,$status,$name,$symbol,$color,$assignment,
                            $site,$siteName,'manual',$actor,$now,1,$key);
                    """;
                insert.Parameters.AddWithValue("$id", person.PersonId);
                insert.Parameters.AddWithValue("$date", date);
                insert.Parameters.AddWithValue("$status", person.StatusId);
                insert.Parameters.AddWithValue("$name", person.StatusName);
                insert.Parameters.AddWithValue("$symbol", person.Symbol);
                insert.Parameters.AddWithValue("$color", person.Color);
                insert.Parameters.AddWithValue("$assignment", person.AssignmentId);
                insert.Parameters.AddWithValue("$site", person.SiteId);
                insert.Parameters.AddWithValue("$siteName", person.SiteName);
                insert.Parameters.AddWithValue("$actor", actor.UserId);
                insert.Parameters.AddWithValue("$now", timestamp);
                insert.Parameters.AddWithValue("$key", token + ":" + person.PersonId);
                if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Attendance commit did not affect exactly one row.");
                await BridgeAuditWriter.WriteAsync(connection,tx,_time,
                    "ATTENDANCE_CARRY_FORWARD","اعتماد حالة أمس يدويًا", actor.UserId,
                    actor.Username, null, "Person", person.PersonId, person.Name,
                    null, new JsonObject
                    {
                        ["date"] = date,
                        ["statusId"] = person.StatusId,
                        ["statusName"] = person.StatusName,
                        ["serviceNumberA"] = person.Number,
                        ["usedDefault"] = person.UsedDefault
                    }.ToJsonString(),
                    "اعتماد حالة اليوم من أمس أو الحالة الافتراضية بتأكيد المستخدم",null,
                    date,correlation,ct).ConfigureAwait(false);
            }
            var outcome = new JsonObject
            {
                ["count"] = selected.Count,
                ["date"] = date,
                ["carriedForward"] = true
            };
            await MutationIdempotency.StoreAsync(connection, tx, receipt, outcome, _time, ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome, events: BridgeHelpers.InvalidationEvent(
                UiInvalidationEvents.AttendanceCommitted,date,selected.Select(p=>p.PersonId)));
        },ct).ConfigureAwait(false);
    }
}
