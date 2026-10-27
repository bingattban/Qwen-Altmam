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
            ["dueTimeLocal"]="08:00",
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
                    "تغيير الحالة الافتراضية للتحضير التلقائي الساعة الثامنة صباحاً",
                    null,_time.GetLocalNow().ToString("yyyy-MM-dd"),Guid.NewGuid().ToString("N"),ct)
                    .ConfigureAwait(false);
            }
            var data=new JsonObject{["defaultStatusId"]=statusId,["defaultStatusName"]=name,
                ["configured"]=true,["dueTimeLocal"]="08:00"};
            await MutationIdempotency.StoreAsync(c,t,receipt,data,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(data);
        },ct).ConfigureAwait(false);
    }
}
