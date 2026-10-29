using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Bridge;

/// <summary>One persisted, user-selected directory for encrypted backups and Excel exports.
/// Only local, existing, writable absolute folders are accepted; the WebView never
/// controls an output filename. The database's own path is not moved.</summary>
public sealed class OutputDirectorySettings
{
    public const string DefaultDirectory = @"E:\AlTamamAlYawmi\Backups";
    private readonly AlTamamDbContext _db;
    public OutputDirectorySettings(AlTamamDbContext db) => _db=db;

    public async Task<string> GetAsync(CancellationToken ct=default)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey='PreferredBackupDirectory';";
        string? configured=(await q.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
        if(!string.IsNullOrWhiteSpace(configured))return configured;
        // The default is a visible preference, not permission to create folders.
        // An unavailable or unwritable path is reported by Overview; the native
        // Windows folder picker lets the operator explicitly select a valid one.
        return DefaultDirectory;
    }

    public static bool IsUsableDirectory(string? input, string databasePath)
    {
        if(!IsSafeDirectory(input,databasePath,out string folder))return false;
        try
        {
            string probe=Path.Combine(folder,".altamam-probe-"+Guid.NewGuid().ToString("N"));
            using var file=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None,
                4096,FileOptions.DeleteOnClose);
            file.WriteByte(1);
            return true;
        }
        catch (Exception ex) when(ex is IOException or UnauthorizedAccessException)
        { return false; }
    }

    public static bool IsSafeDirectory(string? input, string databasePath, out string folder)
    {
        folder="";
        if(string.IsNullOrWhiteSpace(input) || input.Length>600 ||
           input.Any(char.IsControl) || !Path.IsPathFullyQualified(input))
            return false;
        try
        {
            folder=Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            if(folder.StartsWith(@"\\",StringComparison.Ordinal) ||
               folder.StartsWith("//",StringComparison.Ordinal) ||
               !Directory.Exists(folder))return false;
            // A mapped drive letter can still point to LAN storage. Restrict
            // user-selected backup/export paths to local, available media.
            string root=Path.GetPathRoot(folder) ?? "";
            if(string.IsNullOrWhiteSpace(root))return false;
            DriveType driveType=new DriveInfo(root).DriveType;
            if(driveType is DriveType.Network or DriveType.NoRootDirectory
                or DriveType.CDRom or DriveType.Unknown)return false;
            var dbFolder=Path.TrimEndingDirectorySeparator(
                Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? "");
            // Keep exports out of the live SQLCipher database directory and its descendants.
            if(folder.Equals(dbFolder,StringComparison.OrdinalIgnoreCase) ||
               folder.StartsWith(dbFolder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch(ArgumentException){return false;}
        catch(NotSupportedException){return false;}
        catch(IOException){return false;}
        catch(UnauthorizedAccessException){return false;}
        catch(System.Security.SecurityException){return false;}
    }

    public async Task<BridgeResponse> SaveAsync(string? requested,BridgePrincipal actor,CancellationToken ct)
    {
        if(!IsSafeDirectory(requested,_db.DatabasePath,out string folder))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "هذا المسار غير صالح أو غير متاح، يُرجى تحديد مسار سليم وصحيح. يجب اختيار مجلد محلي موجود خارج مجلد قاعدة البيانات، وليس مسار شبكة.");
        try
        {
            // A real create-and-delete probe detects a disconnected, read-only or full drive.
            string probe=Path.Combine(folder,".altamam-write-check-"+Guid.NewGuid().ToString("N"));
            using(var file=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None,
                4096,FileOptions.DeleteOnClose))
                await file.WriteAsync(new byte[]{65},ct).ConfigureAwait(false);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "هذا المسار غير صالح أو غير متاح، يُرجى تحديد مسار سليم وصحيح. المجلد غير قابل للكتابة؛ تحقق من أذونات القرص والمساحة.");
        }
        await _db.ExecuteInWriteTransactionAsync(async(c,t)=>{
            using var previous=c.CreateCommand();
            previous.Transaction=t;
            previous.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey='PreferredBackupDirectory';";
            string? oldFolder=(await previous.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
            if(string.Equals(oldFolder,folder,StringComparison.OrdinalIgnoreCase))return 0;
            using var cmd=c.CreateCommand();cmd.Transaction=t;
            cmd.CommandText="""
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES('PreferredBackupDirectory',$folder,$now)
                ON CONFLICT(SettingKey) DO UPDATE SET
                   SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$folder",folder);
            cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await BridgeAuditWriter.WriteAsync(c,t,TimeProvider.System,
                "OUTPUT_FOLDER_CHANGE","تغيير مجلد النسخ والتصدير",
                actor.UserId,actor.Username,actor.Username,"AppSettings",
                "PreferredBackupDirectory",folder,
                new JsonObject{["directory"]=oldFolder??DefaultDirectory}.ToJsonString(),
                new JsonObject{["directory"]=folder}.ToJsonString(),
                "تغيير المجلد الموحد للنسخ الاحتياطي وتقارير Excel",
                null,DateTime.Now.ToString("yyyy-MM-dd"),ct.CorrelationId(),ct)
                .ConfigureAwait(false);
            return 0;
        },ct).ConfigureAwait(false);
        return BridgeHelpers.Ok(new JsonObject{["directory"]=folder});
    }
}
