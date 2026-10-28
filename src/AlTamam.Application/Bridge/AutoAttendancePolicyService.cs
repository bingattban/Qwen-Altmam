using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
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
            ["configured"]=id is not null && active,
            ["dueTimeLocal"]=await ReadClockAsync(_db,DueTimeKey,DefaultDueTime,ct),
            ["dailyLockTimeLocal"]=await ReadClockAsync(_db,LockTimeKey,DefaultLockTime,ct),
            ["note"]="إذا لم توجد حالة لليوم السابق تُستخدم الحالة الافتراضية المعتمدة من الإدارة."
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
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,"اختر حالة تحضير نشطة من الإعدادات.");
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,"SetAutoAttendanceDefaultStatus",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            using var status=c.CreateCommand();status.Transaction=t;
            status.CommandText="SELECT VisibleName FROM AttendanceStatusDefinitions WHERE StatusId=$id AND ActiveFlag=1;";
            status.Parameters.AddWithValue("$id",statusId);
            string? name=await status.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if(name is null)
                return BridgeHelpers.Error(BridgeErrorCodes.CustomStatusInactive,
                    "الحالة المختارة غير موجودة أو غير نشطة؛ اختر حالة نشطة.");
            using var before=c.CreateCommand();before.Transaction=t;
            before.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
            before.Parameters.AddWithValue("$key",DefaultStatusKey);
            string? previous=await before.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            string timestamp=_time.GetUtcNow().ToString("O");
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
                await BridgeAuditWriter.WriteAsync(c,t,_time,
                    "AUTO_ATTENDANCE_DEFAULT_STATUS_SET","تعيين الحالة الافتراضية للتحضير",
                    actor.UserId,actor.Username,actor.Username,
                    "AppSetting",DefaultStatusKey,"الحالة الافتراضية عند عدم وجود تحضير أمس",
                    new JsonObject{["statusId"]=previous}.ToJsonString(),
                    new JsonObject{["statusId"]=statusId,["name"]=name}.ToJsonString(),
                    "تغيير الحالة الافتراضية للتحضير التلقائي وفق الوقت المحدد",
                    null,_time.GetLocalNow().ToString("yyyy-MM-dd"),Guid.NewGuid().ToString("N"),ct)
                    .ConfigureAwait(false);
            }
            var data=new JsonObject{["defaultStatusId"]=statusId,["defaultStatusName"]=name,
                ["configured"]=true,
                ["dueTimeLocal"]=await ReadClockAsync(_db,DueTimeKey,DefaultDueTime,ct)};
            await MutationIdempotency.StoreAsync(c,t,receipt,data,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(data);
        },ct).ConfigureAwait(false);
    }
    /// <summary>Persist both independently selected local times with audit and idempotency.</summary>
    public async Task<BridgeResponse> SetScheduleAsync(JsonObject? payload,
        BridgePrincipal actor, CancellationToken ct)
    {
        if(actor.Role is not (UserRole.GeneralAdministration or UserRole.OrdinaryAdministration))
            return BridgeHelpers.Error(BridgeErrorCodes.Unauthorized,
                "تغيير أوقات التحضير متاح للإدارة فقط.");
        string due,locked;
        try
        {
            due=payload?["dueTimeLocal"]?.GetValue<string>()?.Trim()??"";
            locked=payload?["dailyLockTimeLocal"]?.GetValue<string>()?.Trim()??"";
        }
        catch { due=""; locked=""; }
        if(!IsValidClock(due) || !IsValidClock(locked))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "حدد وقتين صحيحين بصيغة 24 ساعة (HH:mm).");
        // Backward compatible: existing time-only callers may omit this field.
        // The unified settings form supplies it so all three values commit atomically.
        bool updateDefault=payload?.ContainsKey("defaultStatusId")==true;
        string? newStatus=null;
        if(updateDefault)
        {
            try {newStatus=payload?["defaultStatusId"]?.GetValue<string>()?.Trim();}
            catch {return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,"الحالة الافتراضية غير صالحة.");}
            if(string.IsNullOrWhiteSpace(newStatus)||newStatus.Length>128)
                return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                    "اختر حالة افتراضية نشطة قبل حفظ إعدادات التحضير.");
        }
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,"SetAttendanceSchedule",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            async Task<string?> Prior(string key)
            {
                using var q=c.CreateCommand();q.Transaction=t;
                q.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
                q.Parameters.AddWithValue("$key",key);
                return await q.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }
            string oldDue=await Prior(DueTimeKey)??DefaultDueTime;
            string oldLock=await Prior(LockTimeKey)??DefaultLockTime;
            string? oldStatus=await Prior(DefaultStatusKey);
            string? newStatusName=null;
            if(updateDefault)
            {
                using var status=c.CreateCommand();
                status.Transaction=t;
                status.CommandText="SELECT VisibleName FROM AttendanceStatusDefinitions WHERE StatusId=$id AND ActiveFlag=1;";
                status.Parameters.AddWithValue("$id",newStatus!);
                newStatusName=await status.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if(newStatusName is null)
                    return BridgeHelpers.Error(BridgeErrorCodes.CustomStatusInactive,
                        "الحالة الافتراضية المختارة غير نشطة أو غير موجودة؛ لم تُحفظ أي تغييرات.");
            }
            string timestamp=_time.GetUtcNow().ToString("O");
            if(oldDue!=due || oldLock!=locked || (updateDefault && oldStatus!=newStatus))
            {
                var changes=new List<(string Key,string Value)>{
                    (DueTimeKey,due),(LockTimeKey,locked)};
                if(updateDefault)changes.Add((DefaultStatusKey,newStatus!));
                foreach(var setting in changes)
                {
                    using var save=c.CreateCommand();save.Transaction=t;
                    save.CommandText="""
                        INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                        VALUES($key,$value,$now)
                        ON CONFLICT(SettingKey) DO UPDATE SET
                          SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                        """;
                    save.Parameters.AddWithValue("$key",setting.Key);
                    save.Parameters.AddWithValue("$value",setting.Value);
                    save.Parameters.AddWithValue("$now",timestamp);
                    await save.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await BridgeAuditWriter.WriteAsync(c,t,_time,
                    "ATTENDANCE_SCHEDULE_SET","تغيير أوقات التحضير والإغلاق",
                    actor.UserId,actor.Username,actor.Username,
                    "AppSetting",DueTimeKey,"أوقات التحضير التلقائي وقفل التمام اليومي",
                    new JsonObject{["dueTimeLocal"]=oldDue,["dailyLockTimeLocal"]=oldLock,
                        ["defaultStatusId"]=oldStatus}.ToJsonString(),
                    new JsonObject{["dueTimeLocal"]=due,["dailyLockTimeLocal"]=locked,
                        ["defaultStatusId"]=updateDefault?newStatus:oldStatus}.ToJsonString(),
                    "تعديل وقت التحضير التلقائي وقفل التحضير اليومي",
                    null,_time.GetLocalNow().ToString("yyyy-MM-dd"),Guid.NewGuid().ToString("N"),ct)
                    .ConfigureAwait(false);
            }
            var response=new JsonObject{["dueTimeLocal"]=due,["dailyLockTimeLocal"]=locked,
                ["defaultStatusId"]=updateDefault?newStatus:oldStatus,
                ["defaultStatusName"]=newStatusName};
            await MutationIdempotency.StoreAsync(c,t,receipt,response,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(response);
        },ct).ConfigureAwait(false);
    }

}
