using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Application.Attendance;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Administrator-selected fallback for the 08:00 attendance run. The setting is
/// a durable StatusId, never a name or a client-supplied status snapshot.
/// </summary>
public sealed class AutoAttendancePolicyService
{
    public const string DefaultStatusKey = "AutoAttendanceDefaultStatusId";
    public const string DueTimeKey = "AutoAttendanceDueTimeLocal";
    public const string LockTimeKey = "DailyAttendanceLockTimeLocal";
    public const string DefaultDueTime = "08:00";
    public const string DefaultLockTime = "20:00";

    // Rehydrate only the ID persisted by our own SQLCipher mutation receipt:
    // an idempotent retry has the SAME audit fact, never a newly forged event.
    private static BridgeResponse WithCommittedAuditReceipt(BridgeResponse response)
    {
        if(!response.Success || response.Data is not JsonObject data ||
           data["auditEventId"] is not JsonValue idNode)
            return response;
        try
        {
            string id=idNode.GetValue<string>();
            return id.StartsWith("AUD-",StringComparison.Ordinal)
                ? response with {AuditEventId=id} : response;
        }
        catch(InvalidOperationException) {return response;}
    }

    public static bool IsValidClock(string? value) =>
        value is not null && value.Length == 5 &&
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    public static async Task<string> ReadClockAsync(AlTamamDbContext db, string key,
        string fallback, CancellationToken ct = default)
    {
        using var c = db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
        q.Parameters.AddWithValue("$key",key);
        var raw = await q.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return IsValidClock(raw) ? raw! : fallback;
    }
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public AutoAttendancePolicyService(AlTamamDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public async Task<BridgeResponse> GetAsync(CancellationToken ct)
    {
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT a.SettingValue, s.VisibleName, s.ActiveFlag
            FROM AppSettings a
            LEFT JOIN AttendanceStatusDefinitions s ON s.StatusId=a.SettingValue
            WHERE a.SettingKey=$key;
            """;
        q.Parameters.AddWithValue("$key",DefaultStatusKey);
        using var reader = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        string? id = null, name = null;
        bool active = false;
        if(await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            id=reader.GetString(0);
            name=reader.IsDBNull(1)?null:reader.GetString(1);
            active=!reader.IsDBNull(2)&&reader.GetInt32(2)==1;
        }
        return BridgeHelpers.Ok(new JsonObject
        {
            ["defaultStatusId"]=id,
            ["defaultStatusName"]=name,
            ["configured"]=id is not null && active && !AttendanceDateRangePolicy.RequiresRange(id),
            ["dueTimeLocal"]=await ReadClockAsync(_db,DueTimeKey,DefaultDueTime,ct),
            ["dailyLockTimeLocal"]=await ReadClockAsync(_db,LockTimeKey,DefaultLockTime,ct),
            ["note"]=id is not null && AttendanceDateRangePolicy.RequiresRange(id)
                ? "الحالة الافتراضية المحفوظة هي إجازة أو إذن وتتطلب مدة من وإلى؛ لن تطبق دون مدة. اختر حالة عادية من الإعدادات."
                : "إذا لم توجد حالة لليوم السابق تُستخدم الحالة الافتراضية المعتمدة من الإدارة."
        });
    }

    public async Task<BridgeResponse> SetAsync(JsonObject? payload,BridgePrincipal actor,
        CancellationToken ct)
    {
        if(actor.Role is not (UserRole.GeneralAdministration or UserRole.OrdinaryAdministration))
            return BridgeHelpers.Error(BridgeErrorCodes.Unauthorized,"تغيير الحالة الافتراضية متاح للإدارة فقط.");
        string statusId;
        try {statusId=payload?["statusId"]?.GetValue<string>()?.Trim()??"";}
        catch {statusId="";}
        if(statusId.Length is <1 or >128)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "اختر حالة تحضير نشطة من الإعدادات.", "statusId");
        // A default has no from/to dates. It cannot assign indefinite leave
        // or permission while the daily attendance API requires bounded dates.
        if(AttendanceDateRangePolicy.RequiresRange(statusId))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "الإجازة والإذن تتطلبان مدة من وإلى في التمام اليومي، ولا يمكن اختيارهما حالة افتراضية دون مدة.",
                "statusId");
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,"SetAutoAttendanceDefaultStatus",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return WithCommittedAuditReceipt(replay);
            using var status=c.CreateCommand();status.Transaction=t;
            status.CommandText="SELECT VisibleName FROM AttendanceStatusDefinitions WHERE StatusId=$id AND ActiveFlag=1;";
            status.Parameters.AddWithValue("$id",statusId);
            string? name=await status.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if(name is null)
                return BridgeHelpers.Error(BridgeErrorCodes.CustomStatusInactive,
                    "الحالة المختارة غير موجودة أو غير نشطة؛ اختر حالة نشطة.", "statusId");
            using var before=c.CreateCommand();before.Transaction=t;
            before.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
            before.Parameters.AddWithValue("$key",DefaultStatusKey);
            string? previous=await before.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            string timestamp=_time.GetUtcNow().ToString("O");
            string? auditEventId=null;
            if(previous!=statusId)
            {
                using var save=c.CreateCommand();save.Transaction=t;
                save.CommandText="""
                    INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                    VALUES($key,$id,$now)
                    ON CONFLICT(SettingKey) DO UPDATE SET
                        SettingValue=$id,UpdatedAtUtc=$now;
                    """;
                save.Parameters.AddWithValue("$key",DefaultStatusKey);
                save.Parameters.AddWithValue("$id",statusId);
                save.Parameters.AddWithValue("$now",timestamp);
                await save.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                auditEventId=await BridgeAuditWriter.WriteAsync(c,t,_time,
                    "AUTO_ATTENDANCE_DEFAULT_STATUS_SET","تعيين الحالة الافتراضية للتحضير",
                    actor.UserId,actor.Username,actor.Username,
                    "AppSetting",DefaultStatusKey,"الحالة الافتراضية عند عدم وجود تحضير أمس",
                    new JsonObject{["statusId"]=previous}.ToJsonString(),
                    new JsonObject{["statusId"]=statusId,["name"]=name}.ToJsonString(),
                    "تغيير الحالة الافتراضية للتحضير التلقائي وفق الوقت المحدد",
                    null,_time.GetLocalNow().ToString("yyyy-MM-dd"),Guid.NewGuid().ToString("N"),ct)
                    .ConfigureAwait(false);
            }
            // Reuse the transaction connection: opening another SQLCipher pool connection
            // while a write is active can race test pool resets and acquire a lock.
            using var due=c.CreateCommand();
            due.Transaction=t;
            due.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
            due.Parameters.AddWithValue("$key",DueTimeKey);
            string? storedDue=await due.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            var data=new JsonObject{["defaultStatusId"]=statusId,["defaultStatusName"]=name,
                ["configured"]=true,
                ["dueTimeLocal"]=IsValidClock(storedDue)?storedDue:DefaultDueTime};
            if(auditEventId is not null)data["auditEventId"]=auditEventId;
            await MutationIdempotency.StoreAsync(c,t,receipt,data,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(data) with {AuditEventId=auditEventId};
        },ct).ConfigureAwait(false);
    }
    // Each clock is a distinct idempotent, audited SQLCipher transaction.
    // The settings screen deliberately calls these separately so one failed
    // validation cannot roll back an unrelated successful setting change.
    private async Task<BridgeResponse> SetSingleClockAsync(string key,string value,
        JsonObject? payload,BridgePrincipal actor,CancellationToken ct)
    {
        if(!IsValidClock(value))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "وقت التحضير أو القفل غير صالح؛ استخدم الصيغة HH:mm.",
                key==DueTimeKey?"dueTimeLocal":"dailyLockTimeLocal");
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,"SetAttendanceSchedule",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return WithCommittedAuditReceipt(replay);
            using var prior=c.CreateCommand();
            prior.Transaction=t;
            prior.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
            prior.Parameters.AddWithValue("$key",key);
            string old=(await prior.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString()
                ??(key==DueTimeKey?DefaultDueTime:DefaultLockTime);
            string? auditEventId=null;
            if(old!=value)
            {
                using var change=c.CreateCommand();
                change.Transaction=t;
                change.CommandText="""
                    INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                    VALUES($key,$value,$at)
                    ON CONFLICT(SettingKey) DO UPDATE SET
                      SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                    """;
                change.Parameters.AddWithValue("$key",key);
                change.Parameters.AddWithValue("$value",value);
                change.Parameters.AddWithValue("$at",_time.GetUtcNow().ToString("O"));
                await change.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                auditEventId=await BridgeAuditWriter.WriteAsync(c,t,_time,
                    "ATTENDANCE_CLOCK_SET","تعديل وقت التحضير أو قفل اليوم",
                    actor.UserId,actor.Username,null,"AppSetting",key,
                    key==DueTimeKey?"وقت التحضير التلقائي":"وقت قفل التحضير",
                    new JsonObject{["value"]=old}.ToJsonString(),
                    new JsonObject{["value"]=value}.ToJsonString(),
                    "تعديل مستقل لساعة إعدادات التحضير",null,
                    _time.GetLocalNow().ToString("yyyy-MM-dd"),Guid.NewGuid().ToString("N"),ct)
                    .ConfigureAwait(false);
            }
            var result=new JsonObject{["settingKey"]=key,["value"]=value};
            if(auditEventId is not null)result["auditEventId"]=auditEventId;
            await MutationIdempotency.StoreAsync(c,t,receipt,result,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(result) with {AuditEventId=auditEventId};
        },ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Save EXACTLY one clock per authenticated, audited, idempotent transaction.
    /// The default status belongs to SetAutoAttendanceDefaultStatus, not this action.
    /// Do not revive the retired legacy combined/atomic settings writer.
    /// </summary>
    public async Task<BridgeResponse> SetScheduleAsync(JsonObject? payload,
        BridgePrincipal actor, CancellationToken ct)
    {
        if(actor.Role is not (UserRole.GeneralAdministration or UserRole.OrdinaryAdministration))
            return BridgeHelpers.Error(BridgeErrorCodes.Unauthorized,
                "تغيير أوقات التحضير متاح للإدارة فقط.");

        bool hasDue=payload?.ContainsKey("dueTimeLocal")==true;
        bool hasLock=payload?.ContainsKey("dailyLockTimeLocal")==true;
        bool hasDefault=payload?.ContainsKey("defaultStatusId")==true;
        if(hasDue==hasLock || hasDefault)
        {
            var fields=new JsonObject();
            if(hasDue)fields["dueTimeLocal"]="يجب حفظ وقت التحضير وحده.";
            if(hasLock)fields["dailyLockTimeLocal"]="يجب حفظ وقت القفل وحده.";
            if(hasDefault)fields["defaultStatusId"]="احفظ الحالة الافتراضية في عملية مستقلة.";
            if(fields.Count==0)fields["dueTimeLocal"]="حدد وقتا واحدا لحفظه.";
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "يجب حفظ وقت التحضير ووقت القفل والحالة الافتراضية كلًّا على حدة؛ لن يتم حفظ أي تغيير ضمن طلب موحد.")
                with {ValidationErrors=fields};
        }
        string? selected;
        try {selected=payload?[hasDue?"dueTimeLocal":"dailyLockTimeLocal"]?.GetValue<string>()?.Trim();}
        catch {selected=null;}
        return await SetSingleClockAsync(hasDue?DueTimeKey:LockTimeKey,
            selected??"",payload,actor,ct).ConfigureAwait(false);
    }

}
