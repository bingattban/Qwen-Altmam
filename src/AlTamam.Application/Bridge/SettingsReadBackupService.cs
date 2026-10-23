using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Settings overview and verified manual encrypted backups. Restore and destructive
/// reset remain unavailable until separate preview/confirm + clean-machine tests.
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
        string schedule=await Setting("AutoBackupTimeLocal","16:00",ct);
        string theme=await Setting("Theme","light",ct);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["username"]=person.Username,["role"]=person.Role.ToString(),
            ["roleLabelAr"]=person.Role.ToArabicLabel(),
            ["backupDirectory"]=backupPath,["defaultDriveAvailable"]=Directory.Exists(backupPath),
            ["backupTime"]=schedule,
            ["automaticBackupOperational"]=false,
            ["automaticBackupNote"]="الجدولة اليومية ليست نشطة بعد؛ لا تعتمد عليها قبل إكمال اختبار تشغيلها.",
            ["theme"]=theme
        });
    }
    public async Task<BridgeResponse> ScheduleAsync(CancellationToken ct)
    {
        return BridgeHelpers.Ok(new JsonObject
        {
            ["backupTime"]=await Setting("AutoBackupTimeLocal","16:00",ct),
            ["operational"]=false,
            ["message"]="وقت النسخ محفوظ، لكن الجدولة الآلية لم تُربط حتى الآن بخدمة Windows."
        });
    }
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
        if(!OutputDirectorySettings.IsSafeDirectory(folder,_db.DatabasePath,out folder))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "مجلد النسخ الاحتياطي المختار غير متاح. غيّره من الإعدادات.");
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
