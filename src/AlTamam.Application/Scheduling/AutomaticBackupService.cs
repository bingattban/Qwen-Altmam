using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Scheduling;

/// <summary>
/// Local-only, opt-in daily SQLCipher snapshots, never a cloud service. One
/// durable BackupHistory row per local business date prevents repeat runs after
/// restart, clock rewinds or duplicate scheduler callbacks. No old files are
/// deleted by this scheduler, and no successful log is written before file
/// integrity and SHA-256 verification.
/// </summary>
public sealed class AutomaticBackupService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _runGate=new(1,1);
    public AutomaticBackupService(AlTamamDbContext db,TimeProvider? time=null)
    {
        _db=db;_time=time??TimeProvider.System;
    }

    public async Task<(bool Enabled,string Time)> ReadSettingsAsync(CancellationToken ct=default)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="""
            SELECT SettingKey,SettingValue FROM AppSettings
            WHERE SettingKey IN ('AutoBackupEnabled','AutoBackupTimeLocal');
            """;
        bool enabled=false;
        string time="16:00";
        using var reader=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while(await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if(reader.GetString(0)=="AutoBackupEnabled")
                enabled=reader.GetString(1)=="true";
            else if(reader.GetString(0)=="AutoBackupTimeLocal")
                time=reader.GetString(1);
        }
        return (enabled,time);
    }

    public async Task<string?> LastSuccessfulDateAsync(CancellationToken ct=default)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="""
            SELECT substr(BackupId,6,4)||'-'||substr(BackupId,10,2)||'-'||substr(BackupId,12,2)
            FROM BackupHistory WHERE IsAutomatic=1 AND VerificationPassed=1
              AND BackupId LIKE 'AUTO-%'
            ORDER BY BackupId DESC LIMIT 1;
            """;
        return (await q.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
    }

    public async Task<BridgeResponse> ConfigureAsync(JsonObject? payload,
        BridgePrincipal actor,CancellationToken ct)
    {
        bool enabled;
        string time;
        try
        {
            enabled=payload?["enabled"]?.GetValue<bool>()
                ?? throw new ArgumentException("Missing enabled");
            time=payload?["backupTime"]?.GetValue<string>()??"";
        }
        catch{return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
            "أدخل وقت النسخ وحدد تفعيل الجدولة أو إيقافها.");}
        if(!TimeOnly.TryParseExact(time,"HH:mm",CultureInfo.InvariantCulture,
                DateTimeStyles.None,out _))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "وقت النسخ التلقائي يجب أن يكون بصيغة 24 ساعة، مثل 16:00.");
        if(enabled)
        {
            string folder=await new OutputDirectorySettings(_db).GetAsync(ct).ConfigureAwait(false);
            if(!OutputDirectorySettings.IsUsableDirectory(folder,_db.DatabasePath))
                return BridgeHelpers.Error(BridgeErrorCodes.ScheduleUnavailable,
                    "لا يمكن تفعيل النسخ التلقائي قبل اختيار مجلد محلي متاح وقابل للكتابة.");
        }
        await _db.ExecuteInWriteTransactionAsync(async(c,tx)=>
        {
            using var cmd=c.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES ('AutoBackupEnabled',$enabled,$now)
                ON CONFLICT(SettingKey) DO UPDATE SET
                  SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES ('AutoBackupTimeLocal',$time,$now)
                ON CONFLICT(SettingKey) DO UPDATE SET
                  SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$enabled",enabled?"true":"false");
            cmd.Parameters.AddWithValue("$time",time);
            cmd.Parameters.AddWithValue("$now",_time.GetUtcNow().ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await BridgeAuditWriter.WriteAsync(c,tx,_time,
                "AUTO_BACKUP_SCHEDULE_CHANGED","تعديل جدولة النسخ الاحتياطي",
                actor.UserId,actor.Username,null,"AppSettings","AutoBackupTimeLocal",
                "النسخ التلقائي",null,
                new JsonObject{["enabled"]=enabled,["time"]=time}.ToJsonString(),
                "حفظ وقت النسخ التلقائي وتفعيل الجدولة أو إيقافها",
                null,_time.GetLocalNow().ToString("yyyy-MM-dd"),Guid.NewGuid().ToString("N"),ct)
                .ConfigureAwait(false);
            return 0;
        },ct).ConfigureAwait(false);
        return BridgeHelpers.Ok(new JsonObject{["enabled"]=enabled,["backupTime"]=time});
    }

    /// <returns>True only after the encrypted file and durable database audit
    /// record both committed. Runs once per date, including a same-day startup
    /// catch-up after the scheduled time; never invents backups for past dates.</returns>
    public async Task<bool> RunIfDueAsync(CancellationToken ct=default)
    {
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (enabled,clock)=await ReadSettingsAsync(ct).ConfigureAwait(false);
            if(!enabled)return false;
            if(!TimeOnly.TryParseExact(clock,"HH:mm",CultureInfo.InvariantCulture,
                    DateTimeStyles.None,out TimeOnly due))
                throw new InvalidOperationException("وقت النسخ التلقائي غير صالح.");
            DateTimeOffset now=_time.GetLocalNow();
            if(TimeOnly.FromDateTime(now.DateTime)<due)return false;
            string day=now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            string id="AUTO-"+now.ToString("yyyyMMdd",CultureInfo.InvariantCulture);
            using(var c=_db.CreateOpenConnection())
            using(var q=c.CreateCommand())
            {
                q.CommandText="SELECT EXISTS(SELECT 1 FROM BackupHistory WHERE BackupId=$id AND VerificationPassed=1);";
                q.Parameters.AddWithValue("$id",id);
                if(Convert.ToInt32(await q.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                    return false;
            }
            var output=new OutputDirectorySettings(_db);
            string folder=await output.GetAsync(ct).ConfigureAwait(false);
            if(!OutputDirectorySettings.IsUsableDirectory(folder,_db.DatabasePath))
                throw new IOException("مجلد النسخ التلقائي غير متاح؛ لم تُسجَّل نسخة ناجحة.");
            string filename="AlTamam-Auto-"+now.ToString("yyyyMMdd",CultureInfo.InvariantCulture)
                +"-"+Guid.NewGuid().ToString("N")+".dbbackup";
            string path=Path.Combine(folder,filename);
            try
            {
                await _db.CreateVerifiedEncryptedBackupAsync(path,ct).ConfigureAwait(false);
                await using var stream=File.OpenRead(path);
                string hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)
                    .ConfigureAwait(false)).ToLowerInvariant();
                await _db.ExecuteInWriteTransactionAsync(async(c,tx)=>
                {
                    using var insert=c.CreateCommand();
                    insert.Transaction=tx;
                    insert.CommandText="""
                        INSERT INTO BackupHistory(BackupId,BackupFilePath,Sha256Checksum,
                          SchemaVersion,IsAutomatic,VerificationPassed,KeyProtectionMode,
                          CreatedByUserId,CreatedAtUtc,NotesArabic)
                        VALUES($id,$path,$hash,$schema,1,1,'SQLCipher',
                          'USR-SYSTEM',$now,'نسخة يومية آلية مشفرة ومتحقق منها');
                        """;
                    insert.Parameters.AddWithValue("$id",id);
                    insert.Parameters.AddWithValue("$path",path);
                    insert.Parameters.AddWithValue("$hash",hash);
                    insert.Parameters.AddWithValue("$schema",AlTamamDbContext.CurrentSchemaVersion);
                    insert.Parameters.AddWithValue("$now",_time.GetUtcNow().ToString("O"));
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    await BridgeAuditWriter.WriteAsync(c,tx,_time,
                        "AUTO_BACKUP_CREATE","نسخ احتياطي تلقائي",
                        "USR-SYSTEM","النظام",null,"Backup",id,filename,
                        null,new JsonObject{["sha256"]=hash,["path"]=path}.ToJsonString(),
                        "نسخة آلية مشفرة تم التحقق من سلامتها",null,day,
                        Guid.NewGuid().ToString("N"),ct).ConfigureAwait(false);
                    return 0;
                },ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                // The registry and audit transaction either BOTH committed or
                // rolled back. Never retain an unregistered output as success.
                try{if(File.Exists(path))File.Delete(path);}
                catch(IOException){ /* orphan file is not in BackupHistory */ }
                throw;
            }
        }
        finally{_runGate.Release();}
    }
}
