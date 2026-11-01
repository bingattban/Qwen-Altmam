using System.Security.Cryptography;
using AlTamam.Application.Scheduling;
using AlTamam.Application.Backup;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Settings overview, verified encrypted manual backups, and the opt-in local daily
/// backup scheduler. Restore stays unavailable until atomic recovery is proven.
/// </summary>
public sealed class SettingsReadBackupService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    public SettingsReadBackupService(AlTamamDbContext db, TimeProvider? time=null)
    {
        _db=db;_time=time??TimeProvider.System;
    }
    private async Task<string> Setting(string key,string fallback,CancellationToken ct)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
        q.Parameters.AddWithValue("$key",key);
        return (await q.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString()??fallback;
    }
    public async Task<BridgeResponse> OverviewAsync(BridgePrincipal person,CancellationToken ct)
    {
        string backupPath=await new OutputDirectorySettings(_db).GetAsync(ct);
        var backup=new AutomaticBackupService(_db,_time);
        var (enabled,schedule)=await backup.ReadSettingsAsync(ct).ConfigureAwait(false);
        int retentionCount=await new BackupRetentionService(_db,_time)
            .ReadCountAsync(ct).ConfigureAwait(false);
        string? lastDate=await backup.LastSuccessfulDateAsync(ct).ConfigureAwait(false);
        string theme=await Setting("Theme","light",ct);
        string lastBackupFailure=await Setting("AutoBackupLastFailure","",ct);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["username"]=person.Username,["role"]=person.Role.ToString(),
            ["roleLabelAr"]=person.Role.ToArabicLabel(),
            ["backupDirectory"]=backupPath,["defaultDriveAvailable"]=OutputDirectorySettings.IsUsableDirectory(backupPath,_db.DatabasePath),
            ["backupTime"]=schedule,
            ["automaticBackupRetentionCount"]=retentionCount,
            ["automaticBackupOperational"]=enabled,
            ["automaticBackupEnabled"]=enabled,
            ["lastAutoBackupDate"]=lastDate,
            ["lastAutoBackupFailure"]=lastBackupFailure,
            ["automaticBackupNote"]=lastBackupFailure.Length>0
                ? "فشل النسخ التلقائي: "+lastBackupFailure+
                    ". تحقق من القرص والمجلد، ثم اختر مساراً سليماً عند الحاجة."
                : enabled
                    ? "النسخ اليومي مفعل أثناء تشغيل البرنامج. آخر نسخة ناجحة: "+(lastDate??"لم يتم إنشاء نسخة بعد")
                    : "النسخ التلقائي متوقف؛ فعّله من الإعدادات بعد اختيار مجلد صالح.",
            ["theme"]=theme
        });
    }
    /// <summary>
    /// Writes the existing, program-wide AppSettings.Theme setting. No new
    /// storage location, user roster mutation, or client-only preference.
    /// </summary>
    public async Task<BridgeResponse> SetThemeAsync(JsonObject? input,CancellationToken ct,
        BridgePrincipal? actor=null)
    {
        string? theme;
        try { theme=input?["theme"]?.GetValue<string>(); }
        catch { theme=null; }
        if(theme is not ("light" or "dark"))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "اختر الوضع النهاري أو الليلي فقط.");
        await _db.ExecuteInWriteTransactionAsync(async(connection,tx)=>{
            using var before=connection.CreateCommand();
            before.Transaction=tx;
            before.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey='Theme';";
            string oldTheme=(await before.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString()??"light";
            if(oldTheme==theme)return 0;
            using var command=connection.CreateCommand();
            command.Transaction=tx;
            command.CommandText="""
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES('Theme',$theme,$now)
                ON CONFLICT(SettingKey) DO UPDATE SET
                   SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                """;
            command.Parameters.AddWithValue("$theme",theme);
            command.Parameters.AddWithValue("$now",_time.GetUtcNow().ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await BridgeAuditWriter.WriteAsync(connection,tx,_time,
                "THEME_CHANGE","تغيير مظهر البرنامج",
                actor?.UserId??"USR-SYSTEM",actor?.Username??"النظام",null,
                "AppSettings","Theme","المظهر",
                new JsonObject{["theme"]=oldTheme}.ToJsonString(),
                new JsonObject{["theme"]=theme}.ToJsonString(),
                "تغيير المظهر المحفوظ للبرنامج",null,
                _time.GetLocalNow().ToString("yyyy-MM-dd"),
                ct.CorrelationId(),ct).ConfigureAwait(false);
            return 0;
        },ct).ConfigureAwait(false);
        return BridgeHelpers.Ok(new JsonObject{["theme"]=theme});
    }

    public async Task<BridgeResponse> ScheduleAsync(CancellationToken ct)
    {
        var scheduler=new AutomaticBackupService(_db,_time);
        var (enabled,time)=await scheduler.ReadSettingsAsync(ct).ConfigureAwait(false);
        int retention=await new BackupRetentionService(_db,_time).ReadCountAsync(ct)
            .ConfigureAwait(false);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["backupTime"]=time,["enabled"]=enabled,
            ["retentionCount"]=retention,
            ["lastAutoBackupDate"]=await scheduler.LastSuccessfulDateAsync(ct).ConfigureAwait(false),
            ["operational"]=enabled,
            ["message"]=enabled?"الجدولة تعمل محلياً أثناء تشغيل البرنامج.":"الجدولة متوقفة."
        });
    }
    public Task<BridgeResponse> ConfigureBackupScheduleAsync(JsonObject? input,
        BridgePrincipal actor,CancellationToken ct)
        => new AutomaticBackupService(_db,_time).ConfigureAsync(input,actor,ct);

    public BridgeResponse Integrity()
    {
        bool healthy=_db.HasValidIntegrity();
        return healthy
            ?BridgeHelpers.Ok(new JsonObject{["integrity"]="ok",["encrypted"]=true})
            :BridgeHelpers.Error(BridgeErrorCodes.BackupIntegrityFailed,"فشل فحص سلامة قاعدة البيانات.");
    }
    public async Task<BridgeResponse> BackupAsync(BridgePrincipal person,CancellationToken ct)
    {
        string folder=await new OutputDirectorySettings(_db).GetAsync(ct);
        // Fail visibly before creating any file: an existing folder may be
        // disconnected, read-only, full, or no longer be local.
        if(!OutputDirectorySettings.IsUsableDirectory(folder,_db.DatabasePath))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "هذا المسار غير صالح أو غير متاح، يُرجى تحديد مسار سليم وصحيح. تحقق من وجود مجلد النسخ وصلاحيات الكتابة عليه، ثم اختر مساراً آخر من الإعدادات.");
        string backupId="BKP-"+Guid.NewGuid().ToString("N");
        string filename="AlTamam-"+_time.GetLocalNow().ToString("yyyyMMdd-HHmmss")+"-"+backupId+".dbbackup";
        string destination=Path.Combine(folder,filename);
        try
        {
            await _db.CreateVerifiedEncryptedBackupAsync(destination,ct).ConfigureAwait(false);
            using var stream=File.OpenRead(destination);
            byte[] hash=await SHA256.HashDataAsync(stream,ct).ConfigureAwait(false);
            string checksum=Convert.ToHexString(hash).ToLowerInvariant();
            await _db.ExecuteInWriteTransactionAsync(async(c,t)=>{
                using var insert=c.CreateCommand();
                insert.Transaction=t;
                insert.CommandText="""
                    INSERT INTO BackupHistory(BackupId,BackupFilePath,Sha256Checksum,SchemaVersion,
                        IsAutomatic,VerificationPassed,KeyProtectionMode,CreatedByUserId,CreatedAtUtc,NotesArabic)
                    VALUES($id,$file,$hash,$schema,0,1,'SQLCipher', $user,$now,'نسخة يدوية مشفرة بعد التحقق');
                    """;
                insert.Parameters.AddWithValue("$id",backupId);
                insert.Parameters.AddWithValue("$file",destination);
                insert.Parameters.AddWithValue("$hash",checksum);
                insert.Parameters.AddWithValue("$schema",AlTamamDbContext.CurrentSchemaVersion);
                insert.Parameters.AddWithValue("$user",person.UserId);
                insert.Parameters.AddWithValue("$now",_time.GetUtcNow().ToString("O"));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await BridgeAuditWriter.WriteAsync(c,t,_time,"BACKUP_CREATE","إنشاء نسخة احتياطية",
                    person.UserId,person.Username,person.Username,"Backup",backupId,filename,
                    null,new JsonObject{["sha256"]=checksum,["path"]=destination}.ToJsonString(),
                    "إنشاء نسخة احتياطية مشفرة تم التحقق من سلامتها",null,
                    _time.GetLocalNow().ToString("yyyy-MM-dd"),ct.CorrelationId(),ct).ConfigureAwait(false);
                return 0;
            },ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(new JsonObject
            {
                ["backupId"]=backupId,["path"]=destination,["sha256"]=checksum,
                ["verificationPassed"]=true
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return BridgeHelpers.Error(BridgeErrorCodes.BackupIntegrityFailed,
                "تعذر إنشاء نسخة احتياطية موثوقة؛ تحقّق من مجلد الحفظ ومساحته والأذونات.");
        }
    }
}
