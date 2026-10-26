using System.Security.Cryptography;

namespace AlTamam.Infrastructure.Persistence;

/// <summary>
/// Verified PREPARATION for a future restore. It cannot activate or overwrite
/// the live database. The encrypted rescue is retained on every successful
/// preparation, even if a subsequent staging step fails. Neither keys nor
/// plaintext personnel records are exported.
/// </summary>
public sealed record StagedRestorePreparation(
    string StageFilePath,string StageSha256,
    string RescueFilePath,string RescueSha256,
    int SchemaVersion,long SourcePeople,long CurrentPeople);

public sealed class EncryptedRestoreStagingService
{
    private readonly AlTamamDbContext _db;
    public EncryptedRestoreStagingService(AlTamamDbContext db)=>_db=db;

    public async Task<StagedRestorePreparation> PrepareAsync(
        string selectedEncryptedBackup,CancellationToken ct=default)
    {
        // Read-only validation BEFORE creating rescue files or touching the
        // current encrypted database's physical files.
        var selected=await _db.InspectEncryptedBackupAsync(
            selectedEncryptedBackup,ct).ConfigureAwait(false);
        if(selected.SchemaVersion!=AlTamamDbContext.CurrentSchemaVersion)
            throw new InvalidDataException(
                "نسخة الاستعادة من إصدار قاعدة مختلف؛ يلزم ترحيل منفصل قبل الاستعادة.");

        string dbFolder=Path.GetDirectoryName(_db.DatabasePath)!;
        string attempt=Guid.NewGuid().ToString("N");
        string rescueDir=Path.Combine(dbFolder,"backups");
        string rescuePath=Path.Combine(rescueDir,
            "rescue-before-restore-"+attempt+".dbbackup");
        string stagePath=Path.Combine(dbFolder,
            ".altamam-restore-stage-"+attempt+".dbbackup");
        var finishedStage=false;
        try
        {
            // WAL-consistent online encrypted snapshot of CURRENT state.
            // Failure here aborts before any stage or activation is possible.
            await _db.CreateVerifiedEncryptedBackupAsync(rescuePath,ct)
                .ConfigureAwait(false);
            var rescue=await _db.InspectEncryptedBackupAsync(rescuePath,ct)
                .ConfigureAwait(false);
            if(rescue.SchemaVersion!=AlTamamDbContext.CurrentSchemaVersion)
                throw new InvalidDataException("نسخة الإنقاذ غير متوافقة.");

            // Stage on the same filesystem as the current DB to permit a future
            // native atomic replace only AFTER all runtime connections close.
            // No commit/activation marker or user-facing restore action exists yet.
            await using(var input=new FileStream(selected.FullPath,FileMode.Open,
                FileAccess.Read,FileShare.Read,81920,FileOptions.SequentialScan))
            await using(var output=new FileStream(stagePath,FileMode.CreateNew,
                FileAccess.Write,FileShare.None,81920,FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output,ct).ConfigureAwait(false);
                output.Flush(flushToDisk:true);
            }
            var staged=await _db.InspectEncryptedBackupAsync(stagePath,ct)
                .ConfigureAwait(false);
            if(!string.Equals(staged.Sha256,selected.Sha256,StringComparison.OrdinalIgnoreCase) ||
               staged.SchemaVersion!=selected.SchemaVersion ||
               staged.PeopleCount!=selected.PeopleCount ||
               staged.AttendanceCount!=selected.AttendanceCount ||
               staged.AuditCount!=selected.AuditCount)
                throw new InvalidDataException(
                    "تغيرت النسخة أثناء تجهيز الاستعادة؛ بقيت قاعدة البيانات الحالية كما هي.");

            finishedStage=true;
            return new StagedRestorePreparation(stagePath,staged.Sha256,
                rescuePath,rescue.Sha256,selected.SchemaVersion,
                staged.PeopleCount,rescue.PeopleCount);
        }
        finally
        {
            // Never delete the rescue snapshot: even a staging failure may
            // precede an unrelated disk failure requiring emergency recovery.
            if(!finishedStage)
            {
                try{if(File.Exists(stagePath))File.Delete(stagePath);}
                catch(IOException){ /* incomplete stage has no activation marker */ }
            }
        }
    }
}
