using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Backup;

/// <summary>
/// A forced full reset may destroy all logical records, including the current
/// audit ledger. Keep a verified, encrypted, WAL-consistent rescue independently
/// under the per-user application data root BEFORE the reset transaction begins.
/// No automatically selected external drive, plaintext copy or key export.
/// </summary>
public sealed class ResetRescueService
{
    private readonly AlTamamDbContext _db;
    public ResetRescueService(AlTamamDbContext db)=>_db=db;

    public async Task<string> CreateAsync(CancellationToken ct=default)
    {
        string root=Path.GetDirectoryName(_db.DatabasePath)
            ??throw new InvalidOperationException("Database directory unavailable.");
        string path=Path.Combine(root,"backups",
            "rescue-before-reset-"+Guid.NewGuid().ToString("N")+".dbbackup");
        await _db.CreateVerifiedEncryptedBackupAsync(path,ct).ConfigureAwait(false);
        var result=await _db.InspectEncryptedBackupAsync(path,ct).ConfigureAwait(false);
        if(result.SchemaVersion!=AlTamamDbContext.CurrentSchemaVersion ||
           string.IsNullOrWhiteSpace(result.Sha256) || result.FileSize<1024)
            throw new InvalidDataException("Verified pre-reset rescue could not be established.");
        return result.FullPath;
    }
}
