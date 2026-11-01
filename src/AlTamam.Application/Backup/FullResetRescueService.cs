using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Backup;

/// <summary>
/// Mandatory, non-destructive pre-reset rescue. The verified encrypted snapshot
/// is under the EXISTING per-user database root, never beside the executable or
/// in a user-selected external export directory. A failure forbids full reset.
/// </summary>
public sealed class FullResetRescueService
{
    private readonly AlTamamDbContext _db;
    public FullResetRescueService(AlTamamDbContext db)=>_db=db;

    public async Task<string> CreateAsync(CancellationToken ct=default)
    {
        string folder=Path.Combine(Path.GetDirectoryName(
            Path.GetFullPath(_db.DatabasePath))!,"backups");
        // Never accept the live database as a rescue destination. CreateNew
        // inside CreateVerifiedEncryptedBackupAsync prevents overwriting an
        // earlier verified rescue; keep old snapshots after any new failure.
        string path=Path.Combine(folder,
            "rescue-before-full-reset-"+Guid.NewGuid().ToString("N")+".dbbackup");
        await _db.CreateVerifiedEncryptedBackupAsync(path,ct).ConfigureAwait(false);
        var checkedCopy=await _db.InspectEncryptedBackupAsync(path,ct).ConfigureAwait(false);
        if(checkedCopy.SchemaVersion!=AlTamamDbContext.CurrentSchemaVersion ||
           checkedCopy.FileSize==0 || checkedCopy.Sha256.Length!=64)
            throw new InvalidDataException(
                "نسخة الإنقاذ المشفرة غير مكتملة، ولن تُصفَّر قاعدة البيانات.");
        return checkedCopy.FullPath;
    }
}
