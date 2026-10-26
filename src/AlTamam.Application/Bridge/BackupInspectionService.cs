using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Read-only backup verification, callable after a trusted native Windows file
/// selection or a validated explicit local path. No restore action is implied.
/// Other-PC backups require their original key/recovery material; a failed
/// verification NEVER generates a replacement key or modifies the live DB.
/// </summary>
public sealed class BackupInspectionService
{
    private readonly AlTamamDbContext _db;
    public BackupInspectionService(AlTamamDbContext db)=>_db=db;

    public async Task<BridgeResponse> VerifyAsync(string? path,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(path)||path.Length>1024)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "اختر ملف نسخة احتياطية مشفرة من جهازك أولاً.");
        try
        {
            var result=await _db.InspectEncryptedBackupAsync(path,ct).ConfigureAwait(false);
            string? expected=null;
            using(var connection=_db.CreateOpenConnection())
            using(var q=connection.CreateCommand())
            {
                q.CommandText="""
                    SELECT Sha256Checksum FROM BackupHistory
                    WHERE BackupFilePath=$path AND VerificationPassed=1
                    ORDER BY CreatedAtUtc DESC LIMIT 1;
                    """;
                q.Parameters.AddWithValue("$path",result.FullPath);
                expected=await q.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }
            if(expected is not null &&
               !string.Equals(expected,result.Sha256,StringComparison.OrdinalIgnoreCase))
                return BridgeHelpers.Error(BridgeErrorCodes.BackupIntegrityFailed,
                    "تغير محتوى النسخة عن البصمة المسجلة في البرنامج. لم يُعدل أي سجل.");
            return BridgeHelpers.Ok(new JsonObject{
                ["verified"]=true,
                ["fileName"]=Path.GetFileName(result.FullPath),
                ["path"]=result.FullPath,
                ["sha256"]=result.Sha256,
                ["fileSizeBytes"]=result.FileSize,
                ["schemaVersion"]=result.SchemaVersion,
                ["schemaCompatible"]=result.SchemaVersion==AlTamamDbContext.CurrentSchemaVersion,
                ["people"]=result.PeopleCount,
                ["attendance"]=result.AttendanceCount,
                ["auditEvents"]=result.AuditCount,
                ["users"]=result.UserCount,
                ["registeredInCurrentCatalog"]=expected is not null,
                ["restoreEnabled"]=false,
                ["message"]=result.SchemaVersion==AlTamamDbContext.CurrentSchemaVersion
                   ?"النسخة المشفرة سليمة ومطابقة لإصدار قاعدة البيانات الحالي؛ الاستعادة الفعلية ليست مفعلة بعد."
                   :"النسخة سليمة لكن إصدارها يختلف؛ يلزم مسار ترحيل آمن قبل الاستعادة."
            });
        }
        catch(OperationCanceledException){throw;}
        catch(ArgumentException)
        {
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "هذا المسار غير صالح أو غير متاح، يُرجى تحديد مسار سليم وصحيح.");
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException)
        {
            return BridgeHelpers.Error(BridgeErrorCodes.BackupIntegrityFailed,
                "هذا المسار غير صالح أو غير متاح، يُرجى تحديد مسار سليم وصحيح. تحقّق من اتصال القرص وصلاحيات القراءة.");
        }
        catch(Exception e) when(e is SqliteException or InvalidDataException or FormatException)
        {
            return BridgeHelpers.Error(BridgeErrorCodes.BackupIntegrityFailed,
                "تعذر التحقق من النسخة: قد تكون تالفة أو لا يمكن فك تشفيرها بالمفتاح الحالي. لم تتغير بيانات البرنامج.");
        }
    }
}
