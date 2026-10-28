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
    /// <summary>
    /// Strictly read-only preflight for a proposed restore. Compares the verified
    /// encrypted candidate against the live store; does not stage, issue an
    /// approval token, stop services or activate a database switch.
    /// </summary>
    public async Task<BridgeResponse> PreviewRestoreAsync(
        string? path,string? expectedSha256,CancellationToken ct)
    {
        // Bind the preview to the exact bytes previously inspected through the
        // trusted native picker. A direct path alone is not an approval.
        if(string.IsNullOrWhiteSpace(expectedSha256)||
           expectedSha256.Length!=64||expectedSha256.Any(ch=>!Uri.IsHexDigit(ch)))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "افحص النسخة أولاً للحصول على بصمتها الصحيحة قبل المعاينة.");
        BridgeResponse verified=await VerifyAsync(path,ct).ConfigureAwait(false);
        if(!verified.Success)return verified;
        if(verified.Data is not JsonObject candidate ||
           candidate["verified"]?.GetValue<bool>()!=true)
            return BridgeHelpers.Error(BridgeErrorCodes.BackupIntegrityFailed,
                "لم تكتمل معاينة النسخة المشفرة؛ لم تُغيَّر البيانات.");
        if(!string.Equals(candidate["sha256"]?.GetValue<string>(),
            expectedSha256,StringComparison.OrdinalIgnoreCase))
            return BridgeHelpers.Error(BridgeErrorCodes.BackupIntegrityFailed,
                "تغيرت بصمة النسخة منذ الفحص. أعد اختيار الملف والتحقق منه.");
        if(candidate["schemaCompatible"]?.GetValue<bool>()!=true)
            return BridgeHelpers.Error(BridgeErrorCodes.BackupIntegrityFailed,
                "إصدار النسخة غير متوافق مع قاعدة البيانات الحالية؛ لا يمكن تجهيز الاستعادة.");

        using var connection=_db.CreateOpenConnection();
        using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT (SELECT COUNT(*) FROM Persons),
                   (SELECT COUNT(*) FROM AttendanceRecords),
                   (SELECT COUNT(*) FROM AuditEvents),
                   (SELECT COUNT(*) FROM Users);
            """;
        using var reader=await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(!await reader.ReadAsync(ct).ConfigureAwait(false))
            return BridgeHelpers.Error(BridgeErrorCodes.BackupIntegrityFailed,
                "تعذرت قراءة ملخص قاعدة البيانات الحالية.");

        var incoming=new JsonObject
        {
            ["people"]=candidate["people"]?.DeepClone(),
            ["attendance"]=candidate["attendance"]?.DeepClone(),
            ["auditEvents"]=candidate["auditEvents"]?.DeepClone(),
            ["users"]=candidate["users"]?.DeepClone()
        };
        var current=new JsonObject
        {
            ["people"]=reader.GetInt64(0),
            ["attendance"]=reader.GetInt64(1),
            ["auditEvents"]=reader.GetInt64(2),
            ["users"]=reader.GetInt64(3)
        };
        return BridgeHelpers.Ok(new JsonObject
        {
            ["verified"]=true,["previewOnly"]=true,["restoreEnabled"]=false,
            ["fileName"]=candidate["fileName"]?.DeepClone(),
            ["path"]=candidate["path"]?.DeepClone(),
            ["sha256"]=candidate["sha256"]?.DeepClone(),
            ["schemaVersion"]=candidate["schemaVersion"]?.DeepClone(),
            ["incoming"]=incoming,["current"]=current,
            ["message"]="هذه معاينة قراءة فقط للمقارنة. لم تُستبدل قاعدة البيانات أو تُعدَّل السجلات. الاستعادة الفعلية ما زالت معطلة حتى اعتماد فحوص الأمان."
        });
    }

}
