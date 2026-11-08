using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Application.Attendance.ClosedDayCorrections;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Emergency revision of ONE already-committed historical attendance record,
/// or an authorized historical range revision ("تعديل تمام سابق") for
/// GeneralAdministration and OrdinaryAdministration across active and archived
/// attendance periods, with immutable audit events and revision entries.
/// The original committed timestamp is retained and the closed period remains closed.
/// </summary>
public sealed class ClosedDayCorrectionService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    private const string Operation = "CLOSED-DAY-CORRECTION";
    private const string RangeOperation = "HISTORICAL-ATTENDANCE-RANGE";

    public ClosedDayCorrectionService(AlTamamDbContext db, TimeProvider? time = null)
    { _db = db; _time = time ?? TimeProvider.System; }

    private static string Read(JsonObject? p, string key)
    {
        try { return p?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }
    private static BridgeResponse Deny(string code, string message) => BridgeHelpers.Error(code, message);
    private bool Historical(string date) =>
        ClosedDayCorrectionEligibilityPolicy.IsHistorical(date,_time.GetLocalNow());
    private static bool CanCorrectClosedDay(BridgePrincipal actor) =>
        ClosedDayCorrectionEligibilityPolicy.AuthorizedRole(actor.Role);
    private static bool ValidReason(string reason) =>
        reason.Length is >= 10 and <= 400 && !reason.Any(char.IsControl);

    private static async Task<bool> Enabled(SqliteConnection c, SqliteTransaction? tx, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT SettingValue FROM AppSettings
            WHERE SettingKey='AllowClosedDayEmergencyOverride';
            """;
        return string.Equals(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string,
            "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> Closed(SqliteConnection c, SqliteTransaction? tx, string date, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM PeriodClosures
               WHERE PeriodKey=$date AND PeriodType='DAY' AND IsClosed=1);
            """;
        cmd.Parameters.AddWithValue("$date", date);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;
    }

    private sealed record State(long Revision, string Name, string Number, string Site,
        string BeforeId, string BeforeName, string AfterName, string AfterSymbol, string AfterColor);

    private sealed record RangeDayState(
        string Date,
        bool HasExistingRecord,
        long Revision,
        string Name,
        string Number,
        string AssignmentId,
        string SiteId,
        string SiteName,
        string BeforeId,
        string BeforeName,
        string AfterName,
        string AfterSymbol,
        string AfterColor);

    private static async Task<State?> Snapshot(SqliteConnection c, SqliteTransaction? tx,
        string personId, string date, string statusId, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT ar.RevisionNumber,
                   COALESCE((SELECT i.NameSnapshot FROM PersonIdentityHistory i
                             WHERE i.PersonId=ar.PersonId AND i.ValidFromLocalDate<=$day
                             AND (i.ValidToLocalDate IS NULL OR i.ValidToLocalDate>$day)
                             ORDER BY i.ValidFromLocalDate DESC LIMIT 1),p.CurrentVisibleName),
                   COALESCE((SELECT i.ServiceNumberASnapshot FROM PersonIdentityHistory i
                             WHERE i.PersonId=ar.PersonId AND i.ValidFromLocalDate<=$day
                             AND (i.ValidToLocalDate IS NULL OR i.ValidToLocalDate>$day)
                             ORDER BY i.ValidFromLocalDate DESC LIMIT 1),p.CurrentServiceNumberA),
                   ar.SiteNameSnapshot,ar.StatusId,ar.StatusNameSnapshot,
                   h.VisibleNameSnapshot,h.ShortSymbolSnapshot,h.ColorSnapshot
            FROM AttendanceRecords ar
            JOIN Persons p ON p.PersonId=ar.PersonId
            JOIN AttendanceStatusDefinitions s ON s.StatusId=$status AND s.ActiveFlag=1
            JOIN AttendanceStatusNameHistory h
                ON h.StatusId=s.StatusId AND h.ValidFromLocalDate<=$day
                AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate>$day)
            WHERE ar.PersonId=$person AND ar.AttendanceLocalDate=$day
              AND ar.StatusId<>$status
              AND ar.AttendanceLocalDate>=p.FirstRecordedLocalDate
              AND EXISTS(SELECT 1 FROM PersonAssignments a
                  WHERE a.PersonId=ar.PersonId
                    AND a.SiteId=ar.SiteIdAtEffectiveDate
                    AND a.ValidFromLocalDate<=ar.AttendanceLocalDate
                    AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate>ar.AttendanceLocalDate))
              AND NOT EXISTS(SELECT 1 FROM PersonnelMovements m
                  WHERE m.PersonId=ar.PersonId AND m.EffectiveLocalDate=$day);
            """;
        cmd.Parameters.AddWithValue("$person", personId);
        cmd.Parameters.AddWithValue("$day", date);
        cmd.Parameters.AddWithValue("$status", statusId);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        var item = new State(r.GetInt64(0), r.GetString(1), r.GetString(2),
            r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetString(7), r.GetString(8));
        if (await r.ReadAsync(ct).ConfigureAwait(false))
            return null; // ambiguous status history must fail closed
        return item;
    }

    private static async Task<RangeDayState?> RangeDaySnapshotAsync(
        SqliteConnection c, SqliteTransaction? tx, string personId, string date, string statusId, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COALESCE(ar.RevisionNumber, 0),
                   COALESCE((SELECT i.NameSnapshot FROM PersonIdentityHistory i
                             WHERE i.PersonId=p.PersonId AND i.ValidFromLocalDate<=$day
                             AND (i.ValidToLocalDate IS NULL OR i.ValidToLocalDate>$day)
                             ORDER BY i.ValidFromLocalDate DESC LIMIT 1), p.CurrentVisibleName),
                   COALESCE((SELECT i.ServiceNumberASnapshot FROM PersonIdentityHistory i
                             WHERE i.PersonId=p.PersonId AND i.ValidFromLocalDate<=$day
                             AND (i.ValidToLocalDate IS NULL OR i.ValidToLocalDate>$day)
                             ORDER BY i.ValidFromLocalDate DESC LIMIT 1), p.CurrentServiceNumberA),
                   a.AssignmentId,
                   a.SiteId,
                   COALESCE(ar.SiteNameSnapshot,
                            (SELECT sn.VisibleNameSnapshot FROM SiteNameHistory sn
                             WHERE sn.SiteId=a.SiteId AND sn.ValidFromLocalDate<=$day
                             AND (sn.ValidToLocalDate IS NULL OR sn.ValidToLocalDate>$day)
                             ORDER BY sn.ValidFromLocalDate DESC LIMIT 1),
                            st.VisibleName),
                   COALESCE(ar.StatusId, '__UNPREPARED__'),
                   COALESCE(ar.StatusNameSnapshot, 'غير محضر'),
                   COALESCE((SELECT h.VisibleNameSnapshot FROM AttendanceStatusNameHistory h
                             WHERE h.StatusId=s.StatusId AND h.ValidFromLocalDate<=$day
                             AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate>$day)
                             ORDER BY h.ValidFromLocalDate DESC LIMIT 1), s.VisibleName),
                   COALESCE((SELECT h.ShortSymbolSnapshot FROM AttendanceStatusNameHistory h
                             WHERE h.StatusId=s.StatusId AND h.ValidFromLocalDate<=$day
                             AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate>$day)
                             ORDER BY h.ValidFromLocalDate DESC LIMIT 1), s.ShortSymbol),
                   COALESCE((SELECT h.ColorSnapshot FROM AttendanceStatusNameHistory h
                             WHERE h.StatusId=s.StatusId AND h.ValidFromLocalDate<=$day
                             AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate>$day)
                             ORDER BY h.ValidFromLocalDate DESC LIMIT 1), s.ChosenColorHex)
            FROM Persons p
            JOIN PersonAssignments a ON a.PersonId=p.PersonId
                 AND a.ValidFromLocalDate<=$day
                 AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate>$day)
            JOIN Sites st ON st.SiteId=a.SiteId
            JOIN AttendanceStatusDefinitions s ON s.StatusId=$status AND s.ActiveFlag=1
            LEFT JOIN AttendanceRecords ar ON ar.PersonId=p.PersonId AND ar.AttendanceLocalDate=$day
            WHERE p.PersonId=$person
              AND $day>=p.FirstRecordedLocalDate
              AND NOT EXISTS(SELECT 1 FROM PersonnelMovements m
                  WHERE m.PersonId=p.PersonId AND m.EffectiveLocalDate=$day
                    AND m.MovementType IN (1,2,3));
            """;
        cmd.Parameters.AddWithValue("$person", personId);
        cmd.Parameters.AddWithValue("$day", date);
        cmd.Parameters.AddWithValue("$status", statusId);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        long rev = r.GetInt64(0);
        var state = new RangeDayState(
            date,
            rev > 0,
            rev,
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetString(9),
            r.GetString(10));
        if (await r.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return state;
    }

    private async Task<BridgeResponse> PreviewRangeAsync(JsonObject payload, BridgePrincipal actor, CancellationToken ct)
    {
        string person = Read(payload, "personId");
        string fromStr = Read(payload, "dateFrom");
        string toStr = Read(payload, "dateTo");
        string status = Read(payload, "statusId");
        string reason = Read(payload, "reason");
        if (string.IsNullOrWhiteSpace(reason))
            reason = "تعديل تمام سابق حسب المدة المحددة من الإدارة";
        if (person.Length is < 1 or > 128 || status.Length is < 1 or > 128 || !ValidReason(reason) ||
            !DateOnly.TryParseExact(fromStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate) ||
            !DateOnly.TryParseExact(toStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate) ||
            fromDate > toDate)
            return Deny(BridgeErrorCodes.ValidationError,
                "اختر فرداً وحالة جديدة وفترة تاريخ صالحة (من تاريخ - إلى تاريخ).");

        DateOnly today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        if (toDate > today)
            return Deny(BridgeErrorCodes.ValidationError, "لا يمكن تعديل تمام لتواريخ مستقبلية.");
        int totalSpan = toDate.DayNumber - fromDate.DayNumber + 1;
        if (totalSpan is < 1 or > 62)
            return Deny(BridgeErrorCodes.ValidationError, "المدة المحددة لتعديل التمام السابق يجب ألا تتجاوز 62 يوماً في العملية الواحدة.");

        using var c = _db.CreateOpenConnection();
        var daysArray = new JsonArray();
        string? personName = null, serviceNumberA = null, siteName = null;
        string? afterName = null, afterSymbol = null, afterColor = null;
        long revisionSum = 0;

        for (var d = fromDate; d <= toDate; d = d.AddDays(1))
        {
            string iso = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var snap = await RangeDaySnapshotAsync(c, null, person, iso, status, ct).ConfigureAwait(false);
            if (snap is null) continue;
            personName ??= snap.Name;
            serviceNumberA ??= snap.Number;
            siteName ??= snap.SiteName;
            afterName ??= snap.AfterName;
            afterSymbol ??= snap.AfterSymbol;
            afterColor ??= snap.AfterColor;
            if (snap.BeforeId == status && snap.HasExistingRecord) continue;
            revisionSum = unchecked(revisionSum * 31 + snap.Revision + 1);
            daysArray.Add(new JsonObject
            {
                ["businessDate"] = snap.Date,
                ["hasExistingRecord"] = snap.HasExistingRecord,
                ["previousRevision"] = snap.Revision,
                ["assignmentId"] = snap.AssignmentId,
                ["siteId"] = snap.SiteId,
                ["siteName"] = snap.SiteName,
                ["beforeStatusId"] = snap.BeforeId,
                ["beforeStatusName"] = snap.BeforeName,
                ["afterStatusName"] = snap.AfterName,
                ["afterStatusSymbol"] = snap.AfterSymbol,
                ["afterStatusColor"] = snap.AfterColor
            });
        }

        if (daysArray.Count == 0 || personName is null)
            return Deny(BridgeErrorCodes.MissingData,
                "لا توجد أيام قابلة للتعديل في المدة المحددة، أو أن الفرد يحمل بالفعل نفس الحالة المختارة.");

        var firstDay = (JsonObject)daysArray[0]!;
        var distinctBefore = daysArray
            .Select(x => x?["beforeStatusName"]?.GetValue<string>() ?? "غير محضر")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string combinedBeforeName = string.Join(" / ", distinctBefore);

        var canonical = new JsonObject
        {
            ["mode"] = "range",
            ["personId"] = person,
            ["businessDate"] = fromStr,
            ["dateFrom"] = fromStr,
            ["dateTo"] = toStr,
            ["statusId"] = status,
            ["reason"] = reason,
            ["beforeStatusId"] = firstDay["beforeStatusId"]!.GetValue<string>(),
            ["beforeStatusName"] = combinedBeforeName,
            ["personName"] = personName,
            ["serviceNumberA"] = serviceNumberA!,
            ["siteName"] = siteName!,
            ["afterStatusName"] = afterName!,
            ["afterStatusSymbol"] = afterSymbol!,
            ["afterStatusColor"] = afterColor!,
            ["days"] = daysArray
        };
        string token = BridgePreviewTokens.Issue(_db, _time, RangeOperation, actor.UserId,
            canonical, revisionSum, ct);
        var response = (JsonObject)canonical.DeepClone();
        response["previewToken"] = token;
        response["previousRevision"] = revisionSum;
        response["requiresExplicitConfirmation"] = true;
        return BridgeHelpers.Ok(response);
    }

    public async Task<BridgeResponse> PreviewAsync(JsonObject? payload, BridgePrincipal actor, CancellationToken ct)
    {
        if (!CanCorrectClosedDay(actor)) return Deny(BridgeErrorCodes.Unauthorized, "تصحيح الأيام المغلقة للإدارة العامة والإدارة العادية فقط.");
        if (payload is not null && (!string.IsNullOrWhiteSpace(Read(payload, "dateFrom")) || !string.IsNullOrWhiteSpace(Read(payload, "dateTo"))))
            return await PreviewRangeAsync(payload, actor, ct).ConfigureAwait(false);

        string person = Read(payload, "personId"), day = Read(payload, "businessDate"),
            status = Read(payload, "statusId"), reason = Read(payload, "reason");
        if (person.Length is < 1 or > 128 || status.Length is < 1 or > 128 ||
            !Historical(day) || !ValidReason(reason))
            return Deny(BridgeErrorCodes.ValidationError,
                "اختر فرداً وحالة وتاريخاً سابقاً وسبباً واضحاً (10 إلى 400 حرف).");
        using var c = _db.CreateOpenConnection();
        if (!await Enabled(c, null, ct).ConfigureAwait(false))
            return Deny(BridgeErrorCodes.Unauthorized, "التصحيح الاستثنائي غير مفعّل في إعدادات الإدارة العامة.");
        if (!await Closed(c, null, day, ct).ConfigureAwait(false))
            return Deny(BridgeErrorCodes.HistoricDayLocked, "التاريخ ليس يوماً مغلقاً؛ لا يمكن استخدام المسار الاستثنائي.");
        var previous = await Snapshot(c, null, person, day, status, ct).ConfigureAwait(false);
        if (previous is null)
            return Deny(BridgeErrorCodes.MissingData,
                "لا يوجد تحضير محفوظ قابل للتصحيح، أو حدث نقل/فصل أو تغيرت الحالة.");
        var canonical = new JsonObject
        {
            ["personId"] = person, ["businessDate"] = day, ["statusId"] = status,
            ["reason"] = reason, ["beforeStatusId"] = previous.BeforeId,
            ["beforeStatusName"] = previous.BeforeName, ["personName"] = previous.Name,
            ["serviceNumberA"] = previous.Number, ["siteName"] = previous.Site,
            ["afterStatusName"] = previous.AfterName,
            ["afterStatusSymbol"] = previous.AfterSymbol,
            ["afterStatusColor"] = previous.AfterColor
        };
        string token = BridgePreviewTokens.Issue(_db, _time, Operation, actor.UserId,
            canonical, previous.Revision, ct);
        var response = (JsonObject)canonical.DeepClone();
        response["previewToken"] = token;
        response["previousRevision"] = previous.Revision;
        response["requiresExplicitConfirmation"] = true;
        return BridgeHelpers.Ok(response);
    }

    public async Task<BridgeResponse> CommitAsync(JsonObject? payload, BridgePrincipal actor, CancellationToken ct)
    {
        if (!CanCorrectClosedDay(actor)) return Deny(BridgeErrorCodes.Unauthorized, "تصحيح الأيام المغلقة للإدارة العامة والإدارة العادية فقط.");
        string token = Read(payload, "previewToken");
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c)))
            return Deny(BridgeErrorCodes.ValidationError, "رمز المعاينة غير صالح.");
        return await _db.ExecuteInWriteTransactionAsync(async (c, tx) =>
        {
            // Persist success and the immutable historical revision together.
            // A lost response is replayable only with the same actor, token,
            // action, and payload; a different request is never accepted.
            var (replay, receipt) = await MutationIdempotency.StartAsync(
                c, tx, "CommitClosedDayCorrection", actor.UserId, payload, ct)
                .ConfigureAwait(false);
            if (replay is not null) return replay;
            using var find = c.CreateCommand();
            find.Transaction = tx;
            find.CommandText = """
                SELECT PayloadCanonicalJson,BaseRevision,Operation FROM PreviewTokens
                WHERE Token=$token AND Operation IN ($op, $rangeOp) AND ActorUserId=$actor
                  AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
                """;
            find.Parameters.AddWithValue("$token", token);
            find.Parameters.AddWithValue("$op", Operation);
            find.Parameters.AddWithValue("$rangeOp", RangeOperation);
            find.Parameters.AddWithValue("$actor", actor.UserId);
            find.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
            using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Deny(BridgeErrorCodes.ConflictStale, "انتهت المعاينة أو سبق اعتمادها.");
            string canonical = reader.GetString(0);
            long revision = reader.GetInt64(1);
            string tokenOp = reader.GetString(2);
            reader.Close();

            var info = JsonNode.Parse(canonical) as JsonObject;
            if (tokenOp == RangeOperation)
            {
                string personId = Read(info, "personId"), statusId = Read(info, "statusId"),
                    reasonText = Read(info, "reason"), dateFrom = Read(info, "dateFrom");
                if (info?["days"] is not JsonArray days || days.Count == 0)
                    return Deny(BridgeErrorCodes.ConflictStale, "بيانات المعاينة غير صالحة.");

                long? consumedRange = await BridgePreviewTokens.ConsumeAsync(_db, _time, c, tx, token,
                    RangeOperation, actor.UserId, revision, ct).ConfigureAwait(false);
                if (consumedRange != revision)
                    return Deny(BridgeErrorCodes.ConflictStale, "رمز المعاينة مستعمل أو منتهي.");

                string nowUtc = _time.GetUtcNow().ToString("O");
                string corr = ct.CorrelationId();
                long lastRevisionNumber = 1;
                string personName = Read(info, "personName");

                foreach (var node in days)
                {
                    if (node is not JsonObject dayEntry)
                        return Deny(BridgeErrorCodes.ConflictStale, "بيانات اليوم غير صالحة.");
                    string dayIso = Read(dayEntry, "businessDate");
                    long expectedRev = dayEntry["previousRevision"]?.GetValue<long>() ?? 0;
                    string expectedBeforeId = Read(dayEntry, "beforeStatusId");
                    var live = await RangeDaySnapshotAsync(c, tx, personId, dayIso, statusId, ct).ConfigureAwait(false);
                    if (live is null || live.Revision != expectedRev || live.BeforeId != expectedBeforeId)
                        return Deny(BridgeErrorCodes.ConflictStale, "تغيّر السجل أو الحالة بعد المعاينة؛ أعد البحث والمعاينة.");

                    if (live.HasExistingRecord)
                    {
                        using var update = c.CreateCommand();
                        update.Transaction = tx;
                        update.CommandText = """
                            UPDATE AttendanceRecords
                            SET StatusId=$after,StatusNameSnapshot=$name,
                                StatusSymbolSnapshot=$symbol,StatusColorSnapshot=$color,
                                Source='emergency-override',RevisionNumber=RevisionNumber+1
                            WHERE PersonId=$person AND AttendanceLocalDate=$day
                              AND StatusId=$before AND RevisionNumber=$revision;
                            """;
                        update.Parameters.AddWithValue("$after", statusId);
                        update.Parameters.AddWithValue("$name", live.AfterName);
                        update.Parameters.AddWithValue("$symbol", live.AfterSymbol);
                        update.Parameters.AddWithValue("$color", live.AfterColor);
                        update.Parameters.AddWithValue("$person", personId);
                        update.Parameters.AddWithValue("$day", dayIso);
                        update.Parameters.AddWithValue("$before", live.BeforeId);
                        update.Parameters.AddWithValue("$revision", expectedRev);
                        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                            throw new InvalidOperationException("Historical attendance range concurrency conflict.");
                        lastRevisionNumber = expectedRev + 1;
                    }
                    else
                    {
                        using var insert = c.CreateCommand();
                        insert.Transaction = tx;
                        insert.CommandText = """
                            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                                Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                            VALUES($person,$day,$after,$name,$symbol,$color,
                                $assign,$site,$siteName,'emergency-override',$actor,$now,1,$key);
                            """;
                        insert.Parameters.AddWithValue("$person", personId);
                        insert.Parameters.AddWithValue("$day", dayIso);
                        insert.Parameters.AddWithValue("$after", statusId);
                        insert.Parameters.AddWithValue("$name", live.AfterName);
                        insert.Parameters.AddWithValue("$symbol", live.AfterSymbol);
                        insert.Parameters.AddWithValue("$color", live.AfterColor);
                        insert.Parameters.AddWithValue("$assign", live.AssignmentId);
                        insert.Parameters.AddWithValue("$site", live.SiteId);
                        insert.Parameters.AddWithValue("$siteName", live.SiteName);
                        insert.Parameters.AddWithValue("$actor", actor.UserId);
                        insert.Parameters.AddWithValue("$now", nowUtc);
                        insert.Parameters.AddWithValue("$key", "HIST-RANGE-" + token[..16] + "-" + personId + "-" + dayIso);
                        if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                            throw new InvalidOperationException("Historical attendance range insert conflict.");
                        lastRevisionNumber = 1;
                    }

                    using var history = c.CreateCommand();
                    history.Transaction = tx;
                    history.CommandText = """
                        INSERT INTO AttendanceRevisions(PersonId,TargetLocalDate,BeforeStatusId,
                            BeforeStatusNameSnapshot,AfterStatusId,AfterStatusNameSnapshot,Reason,
                            ActorUserId,ExecutedAtUtc,AuthorizationMethod)
                        VALUES($person,$day,$before,$beforeName,$after,$afterName,$reason,
                            $actor,$now,$authorization);
                        """;
                    history.Parameters.AddWithValue("$authorization", actor.Role == UserRole.GeneralAdministration
                        ? "general-admin-enabled-one-time-preview" : "ordinary-admin-enabled-one-time-preview");
                    history.Parameters.AddWithValue("$person", personId);
                    history.Parameters.AddWithValue("$day", dayIso);
                    history.Parameters.AddWithValue("$before", live.BeforeId);
                    history.Parameters.AddWithValue("$beforeName", live.BeforeName);
                    history.Parameters.AddWithValue("$after", statusId);
                    history.Parameters.AddWithValue("$afterName", live.AfterName);
                    history.Parameters.AddWithValue("$reason", reasonText);
                    history.Parameters.AddWithValue("$actor", actor.UserId);
                    history.Parameters.AddWithValue("$now", nowUtc);
                    await history.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    await BridgeAuditWriter.WriteAsync(c, tx, _time, "CLOSED_DAY_CORRECTED",
                        "تعديل تمام سابق", actor.UserId, actor.Username, actor.Username,
                        "Person", personId, personName,
                        new JsonObject { ["statusId"] = live.BeforeId, ["statusName"] = live.BeforeName,
                            ["revision"] = expectedRev }.ToJsonString(),
                        new JsonObject { ["statusId"] = statusId, ["statusName"] = live.AfterName,
                            ["revision"] = lastRevisionNumber, ["date"] = dayIso }.ToJsonString(),
                        "تعديل تمام سابق موثّق حسب المدة المحددة", reasonText, dayIso,
                        corr, ct).ConfigureAwait(false);
                }

                var rangeResult = new JsonObject
                {
                    ["businessDate"] = dateFrom,
                    ["personId"] = personId,
                    ["revisionNumber"] = lastRevisionNumber
                };
                await MutationIdempotency.StoreAsync(c, tx, receipt, rangeResult, _time, ct)
                    .ConfigureAwait(false);
                return BridgeHelpers.Ok(rangeResult,
                    events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.AttendanceRevised,
                        dateFrom, new[] { personId }));
            }

            string person = Read(info, "personId"), day = Read(info, "businessDate"),
                status = Read(info, "statusId"), reason = Read(info, "reason");
            if (!Historical(day) || !ValidReason(reason) ||
                !await Enabled(c, tx, ct).ConfigureAwait(false) ||
                !await Closed(c, tx, day, ct).ConfigureAwait(false))
                return Deny(BridgeErrorCodes.ConflictStale, "تغيّرت شروط الإذن أو إغلاق اليوم بعد المعاينة.");
            var current = await Snapshot(c, tx, person, day, status, ct).ConfigureAwait(false);
            if (current is null || current.Revision != revision ||
                current.BeforeId != Read(info, "beforeStatusId") ||
                current.BeforeName != Read(info, "beforeStatusName") ||
                current.AfterName != Read(info, "afterStatusName") ||
                current.AfterSymbol != Read(info, "afterStatusSymbol") ||
                current.AfterColor != Read(info, "afterStatusColor"))
                return Deny(BridgeErrorCodes.ConflictStale, "تغيّر السجل أو الحالة؛ أعد المعاينة.");

            long? consumed = await BridgePreviewTokens.ConsumeAsync(_db, _time, c, tx, token,
                Operation, actor.UserId, revision, ct).ConfigureAwait(false);
            if (consumed != revision)
                return Deny(BridgeErrorCodes.ConflictStale, "رمز المعاينة مستعمل أو منتهي.");

            string now = _time.GetUtcNow().ToString("O");
            using var updateSingle = c.CreateCommand();
            updateSingle.Transaction = tx;
            updateSingle.CommandText = """
                UPDATE AttendanceRecords
                SET StatusId=$after,StatusNameSnapshot=$name,
                    StatusSymbolSnapshot=$symbol,StatusColorSnapshot=$color,
                    Source='emergency-override',RevisionNumber=RevisionNumber+1
                WHERE PersonId=$person AND AttendanceLocalDate=$day
                  AND StatusId=$before AND RevisionNumber=$revision;
                """;
            updateSingle.Parameters.AddWithValue("$after", status);
            updateSingle.Parameters.AddWithValue("$name", current.AfterName);
            updateSingle.Parameters.AddWithValue("$symbol", current.AfterSymbol);
            updateSingle.Parameters.AddWithValue("$color", current.AfterColor);
            updateSingle.Parameters.AddWithValue("$person", person);
            updateSingle.Parameters.AddWithValue("$day", day);
            updateSingle.Parameters.AddWithValue("$before", current.BeforeId);
            updateSingle.Parameters.AddWithValue("$revision", revision);
            if (await updateSingle.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Closed-day correction concurrency conflict.");

            using var historySingle = c.CreateCommand();
            historySingle.Transaction = tx;
            historySingle.CommandText = """
                INSERT INTO AttendanceRevisions(PersonId,TargetLocalDate,BeforeStatusId,
                    BeforeStatusNameSnapshot,AfterStatusId,AfterStatusNameSnapshot,Reason,
                    ActorUserId,ExecutedAtUtc,AuthorizationMethod)
                VALUES($person,$day,$before,$beforeName,$after,$afterName,$reason,
                    $actor,$now,$authorization);
                """;
            historySingle.Parameters.AddWithValue("$authorization", actor.Role == UserRole.GeneralAdministration
                ? "general-admin-enabled-one-time-preview" : "ordinary-admin-enabled-one-time-preview");
            historySingle.Parameters.AddWithValue("$person", person);
            historySingle.Parameters.AddWithValue("$day", day);
            historySingle.Parameters.AddWithValue("$before", current.BeforeId);
            historySingle.Parameters.AddWithValue("$beforeName", current.BeforeName);
            historySingle.Parameters.AddWithValue("$after", status);
            historySingle.Parameters.AddWithValue("$afterName", current.AfterName);
            historySingle.Parameters.AddWithValue("$reason", reason);
            historySingle.Parameters.AddWithValue("$actor", actor.UserId);
            historySingle.Parameters.AddWithValue("$now", now);
            await historySingle.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await BridgeAuditWriter.WriteAsync(c, tx, _time, "CLOSED_DAY_CORRECTED",
                "تصحيح استثنائي ليوم مغلق", actor.UserId, actor.Username, actor.Username,
                "Person", person, current.Name,
                new JsonObject { ["statusId"] = current.BeforeId, ["statusName"] = current.BeforeName,
                    ["revision"] = revision }.ToJsonString(),
                new JsonObject { ["statusId"] = status, ["statusName"] = current.AfterName,
                    ["revision"] = revision + 1, ["date"] = day }.ToJsonString(),
                "تصحيح استثنائي موثّق دون تغيير وقت التحضير الأصلي", reason, day,
                ct.CorrelationId(), ct).ConfigureAwait(false);

            var result = new JsonObject { ["businessDate"] = day,
                ["personId"] = person, ["revisionNumber"] = revision + 1 };
            await MutationIdempotency.StoreAsync(c, tx, receipt, result, _time, ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(result,
                events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.AttendanceRevised,
                    day, new[] { person }));
        }, ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> SetPolicyAsync(JsonObject? payload, BridgePrincipal actor, CancellationToken ct)
    {
        if (!CanCorrectClosedDay(actor)) return Deny(BridgeErrorCodes.Unauthorized, "الإعداد محصور بالإدارة العامة والإدارة العادية.");
        bool enabled;
        try { enabled = payload?["enabled"]?.GetValue<bool>() ?? throw new FormatException(); }
        catch { return Deny(BridgeErrorCodes.ValidationError, "قيمة تفعيل التصحيح مطلوبة."); }
        string reason = Read(payload, "reason");
        if (reason.Length > 0 && !ValidReason(reason))
            return Deny(BridgeErrorCodes.ValidationError, "عند إدخال ملاحظة للتفعيل أو الإيقاف يجب أن تكون 10 إلى 400 حرف.");
        return await _db.ExecuteInWriteTransactionAsync(async (c, tx) =>
        {
            var (replay, receipt) = await MutationIdempotency.StartAsync(
                c, tx, "SetClosedDayCorrectionPolicy", actor.UserId, payload, ct)
                .ConfigureAwait(false);
            if (replay is not null) return replay;
            bool previous = await Enabled(c, tx, ct).ConfigureAwait(false);
            string now = _time.GetUtcNow().ToString("O");
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES('AllowClosedDayEmergencyOverride',$value,$now)
                ON CONFLICT(SettingKey) DO UPDATE SET
                    SettingValue=excluded.SettingValue, UpdatedAtUtc=excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$value", enabled ? "true" : "false");
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await BridgeAuditWriter.WriteAsync(c, tx, _time,
                "CLOSED_DAY_CORRECTION_POLICY", "تغيير سياسة التصحيح الاستثنائي",
                actor.UserId, actor.Username, actor.Username, "AppSetting",
                "AllowClosedDayEmergencyOverride", "السماح بتصحيح يوم مغلق",
                new JsonObject { ["enabled"] = previous }.ToJsonString(),
                new JsonObject { ["enabled"] = enabled }.ToJsonString(),
                "تغيير إذن التصحيح الاستثنائي بواسطة مسؤول مصرح له", string.IsNullOrWhiteSpace(reason) ? null : reason,
                DateOnly.FromDateTime(_time.GetLocalNow().DateTime).ToString("yyyy-MM-dd"),
                ct.CorrelationId(), ct).ConfigureAwait(false);
            var result = new JsonObject { ["enabled"] = enabled };
            await MutationIdempotency.StoreAsync(c, tx, receipt, result, _time, ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(result);
        }, ct).ConfigureAwait(false);
    }
}
