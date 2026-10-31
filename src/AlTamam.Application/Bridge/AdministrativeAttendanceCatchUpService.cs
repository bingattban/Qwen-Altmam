using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Application.Scheduling;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Explicit, two-step administrator backfill of MISSING attendance on ONE business date
/// in the last seven prior local calendar days. Never edits a committed status.
/// Its preview, idempotency receipt, canonical audit and attendance writes are ACID.
/// </summary>
public sealed class AdministrativeAttendanceCatchUpService
{
    private const string PreviewOperation = "ADMIN-SEVEN-DAY-ATTENDANCE-CATCHUP";
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public AdministrativeAttendanceCatchUpService(AlTamamDbContext db, TimeProvider? time = null)
    { _db = db; _time = time ?? TimeProvider.System; }

    private static string Read(JsonObject? data, string key)
    {
        try { return data?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static bool Allowed(BridgePrincipal actor) =>
        actor.Role is UserRole.GeneralAdministration or UserRole.OrdinaryAdministration;

    private bool ValidateDate(string raw, out DateOnly selected)
    {
        DateOnly today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out selected)
               && selected < today && selected >= today.AddDays(-7);
    }

    private static BridgeResponse Error(string code, string message) =>
        BridgeHelpers.Error(code, message);

    // Version fingerprint includes the exact historical assignment and day/yesterday
    // status set, preventing silent stale previews even when counts happen to match.
    private static async Task<(string Fingerprint, int Pending, int Eligible)> SnapshotAsync(
        SqliteConnection connection, SqliteTransaction? tx, DateOnly date, CancellationToken ct)
    {
        string day = AutoAttendanceService.Iso(date);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT p.PersonId, a.AssignmentId,
                   COALESCE(today.StatusId,''),
                   COALESCE(previous.StatusId,''),
                   COALESCE(CAST(m.MovementType AS TEXT),'')
            FROM Persons p
            JOIN PersonAssignments a ON a.PersonId=p.PersonId
                 AND a.ValidFromLocalDate<=$day
                 AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate>$day)
            LEFT JOIN AttendanceRecords today ON today.PersonId=p.PersonId
                 AND today.AttendanceLocalDate=$day
            LEFT JOIN AttendanceRecords previous ON previous.PersonId=p.PersonId
                 AND previous.AttendanceLocalDate=$yesterday
            LEFT JOIN PersonnelMovements m ON m.PersonId=p.PersonId
                 AND m.EffectiveLocalDate=$day AND m.MovementType IN (1,2,3)
            WHERE p.FirstRecordedLocalDate<=$day
            ORDER BY p.PersonId,a.AssignmentId;
            """;
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$yesterday", AutoAttendanceService.Iso(date.AddDays(-1)));
        int pending = 0, eligible = 0;
        var serialized = new StringBuilder(day);
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                serialized.Append('|');
                for (int i = 0; i < 5; i++)
                    serialized.Append(reader.GetString(i)).Append(';');
                if (reader.GetString(4).Length > 0) continue; // movement day
                eligible++;
                if (reader.GetString(2).Length == 0) pending++;
            }
        }
        using var setting = connection.CreateCommand();
        setting.Transaction = tx;
        setting.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
        setting.Parameters.AddWithValue("$key", AutoAttendancePolicyService.DefaultStatusKey);
        serialized.Append("!fallback=");
        serialized.Append(await setting.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? "");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized.ToString())));
        return (hash, pending, eligible);
    }

    private static async Task<bool> HasLedgerAsync(SqliteConnection c, SqliteTransaction? tx,
        string day, CancellationToken ct)
    {
        using var check = c.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT EXISTS(SELECT 1 FROM AutoAttendanceRuns WHERE BusinessDate=$date);";
        check.Parameters.AddWithValue("$date", day);
        return Convert.ToInt32(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0;
    }

    public async Task<BridgeResponse> PreviewAsync(JsonObject? payload, BridgePrincipal actor, CancellationToken ct)
    {
        if (!Allowed(actor)) return Error(BridgeErrorCodes.Unauthorized,
            "استدراك الأيام السابقة مخصص للإدارة العامة والإدارة العادية.");
        string raw = Read(payload, "businessDate"), reason = Read(payload, "reason");
        if (!ValidateDate(raw, out var day) || reason.Length is < 10 or > 400 ||
            reason.Any(char.IsControl))
            return Error(BridgeErrorCodes.ValidationError,
                "اختر يوماً من الأيام السبعة السابقة، مع سبب واضح من 10 إلى 400 حرف.");
        using var c = _db.CreateOpenConnection();
        if (await HasLedgerAsync(c, null, raw, ct).ConfigureAwait(false))
            return Error(BridgeErrorCodes.ConflictStale, "سبق اعتماد التحضير التلقائي لهذا التاريخ.");
        var snapshot = await SnapshotAsync(c, null, day, ct).ConfigureAwait(false);
        if (snapshot.Pending == 0)
            return Error(BridgeErrorCodes.MissingData, "لا توجد حالات غير محضرة لهذا التاريخ.");
        var canonical = new JsonObject
        {
            ["businessDate"] = raw,
            ["reason"] = reason,
            ["fingerprint"] = snapshot.Fingerprint,
            ["pendingCount"] = snapshot.Pending,
            ["eligibleCount"] = snapshot.Eligible
        };
        string token = BridgePreviewTokens.Issue(_db, _time, PreviewOperation,
            actor.UserId, canonical, snapshot.Pending, ct);
        var result = (JsonObject)canonical.DeepClone();
        result["previewToken"] = token;
        result["requiresExplicitConfirmation"] = true;
        return BridgeHelpers.Ok(result);
    }

    public async Task<BridgeResponse> CommitAsync(JsonObject? payload, BridgePrincipal actor, CancellationToken ct)
    {
        if (!Allowed(actor)) return Error(BridgeErrorCodes.Unauthorized,
            "استدراك الأيام السابقة مخصص للإدارة العامة والإدارة العادية.");
        string token = Read(payload, "previewToken");
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c)))
            return Error(BridgeErrorCodes.ValidationError, "رمز المعاينة غير صالح.");
        return await _db.ExecuteInWriteTransactionAsync(async (c, tx) =>
        {
            var (replay, receipt) = await MutationIdempotency.StartAsync(
                c, tx, "CommitAdministrativeAttendanceCatchUp", actor.UserId, payload, ct)
                .ConfigureAwait(false);
            if (replay is not null) return replay;
            using var find = c.CreateCommand();
            find.Transaction = tx;
            find.CommandText = """
                SELECT PayloadCanonicalJson,BaseRevision FROM PreviewTokens
                WHERE Token=$token AND Operation=$op AND ActorUserId=$actor
                  AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
                """;
            find.Parameters.AddWithValue("$token", token);
            find.Parameters.AddWithValue("$op", PreviewOperation);
            find.Parameters.AddWithValue("$actor", actor.UserId);
            find.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
            using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Error(BridgeErrorCodes.ConflictStale, "انتهت المعاينة أو سبق اعتمادها.");
            string canonical = reader.GetString(0);
            long expected = reader.GetInt64(1);
            reader.Close();
            var preview = JsonNode.Parse(canonical) as JsonObject;
            string dateText = Read(preview, "businessDate");
            if (!ValidateDate(dateText, out var date))
                return Error(BridgeErrorCodes.HistoricDayLocked,
                    "انتهت نافذة الأيام السبعة السابقة؛ أعد المعاينة لتاريخ مسموح.");
            if (await HasLedgerAsync(c, tx, dateText, ct).ConfigureAwait(false))
                return Error(BridgeErrorCodes.ConflictStale, "اعتمد هذا التاريخ مسبقاً؛ لم يُكتب شيء.");
            var now = await SnapshotAsync(c, tx, date, ct).ConfigureAwait(false);
            if (now.Pending != expected ||
                now.Fingerprint != Read(preview, "fingerprint"))
                return Error(BridgeErrorCodes.ConflictStale,
                    "تغير كشف التاريخ أو حالات الأمس منذ المعاينة؛ راجع البيانات مجدداً.");
            long? consumed = await BridgePreviewTokens.ConsumeAsync(
                _db, _time, c, tx, token, PreviewOperation, actor.UserId, expected, ct)
                .ConfigureAwait(false);
            if (consumed != expected)
                return Error(BridgeErrorCodes.ConflictStale, "رمز المعاينة مستخدم أو منتهي.");
            var executed = await new AutoAttendanceService(_db, _time)
                .RunForDateInTransactionAsync(date, true, c, tx, ct, actor).ConfigureAwait(false);
            if (executed.BlockedClosedDay || executed.WasAlreadyCompleted)
                throw new InvalidOperationException("Administrative catch-up execution must not complete on a blocked or duplicate date.");
            var result = new JsonObject
            {
                ["businessDate"] = dateText, ["appliedCount"] = executed.CopiedCount,
                ["pendingBefore"] = now.Pending, ["summary"] = executed.SummaryArabic,
                ["isCatchUpRun"] = true
            };
            // The automated algorithm writes its canonical audit and durable ledger in
            // this transaction; the actor's explicit reason is retained separately.
            await BridgeAuditWriter.WriteAsync(c, tx, _time,
                "ADMIN_ATTENDANCE_CATCHUP_APPROVED", "اعتماد استدراك التحضير الإداري",
                actor.UserId, actor.Username, actor.Username,
                "AutoAttendanceRun", AutoAttendanceService.JobKey(date),
                "استدراك التحضير لتاريخ " + dateText, null, result.ToJsonString(),
                "اعتماد الإداري للتحضير الناقص بعد معاينة وتأكيد موثقين",
                Read(preview, "reason"), dateText, Guid.NewGuid().ToString("N"), ct)
                .ConfigureAwait(false);
            await MutationIdempotency.StoreAsync(c, tx, receipt, result, _time, ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(result,
                events: BridgeHelpers.InvalidationEvent(
                    UiInvalidationEvents.AttendanceCommitted, dateText, []));
        }, ct).ConfigureAwait(false);
    }
}
