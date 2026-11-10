using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Security;
using Xunit;

namespace AlTamam.Tests.Security;

/// <summary>
/// Offline cross-machine recovery using an independent password-protected
/// package; tests never log or commit live user keys, passwords or databases.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PortableRecoveryKeyPackageTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),
        "altamam-portable-recovery-"+Guid.NewGuid().ToString("N"));
    private const string Phrase="Correct horse battery staple 2026 – independently held";

    [Fact]
    public void Package_DecryptsOnAnyMachine_WrongPasswordAndTamperFail()
    {
        string key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] package=PortableRecoveryKeyPackage.Seal(key,Phrase);
        try
        {
            Assert.Equal(PortableRecoveryKeyPackage.FileLength,package.Length);
            Assert.Equal(key,PortableRecoveryKeyPackage.Unseal(package,Phrase));
            Assert.DoesNotContain(key,Convert.ToHexString(package));
            Assert.Throws<CryptographicException>(()=>
                PortableRecoveryKeyPackage.Unseal(package,"An incorrect passphrase for recovery 2026"));
            package[^1]^=0x80;
            Assert.Throws<CryptographicException>(()=>PortableRecoveryKeyPackage.Unseal(package,Phrase));
        }
        finally{CryptographicOperations.ZeroMemory(package);}
    }

    [Fact]
    public void Package_UsesFreshSaltAndNonceAndRefusesWeakPassphrase()
    {
        string key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] a=PortableRecoveryKeyPackage.Seal(key,Phrase);
        byte[] b=PortableRecoveryKeyPackage.Seal(key,Phrase);
        Assert.NotEqual(a,b);
        Assert.Equal(key,PortableRecoveryKeyPackage.Unseal(b,Phrase));
        Assert.Throws<ArgumentException>(()=>PortableRecoveryKeyPackage.Seal(key,"weak"));
        Assert.Throws<ArgumentException>(()=>PortableRecoveryKeyPackage.Seal("garbage",Phrase));
        Assert.Throws<CryptographicException>(()=>
            PortableRecoveryKeyPackage.Unseal(new byte[PortableRecoveryKeyPackage.FileLength],Phrase));
        CryptographicOperations.ZeroMemory(a);
        CryptographicOperations.ZeroMemory(b);
    }

    private static (long PersonTables,long Users) ReadLogicalSnapshot(string database,string key)
    {
        using var context=new AlTamamDbContext(database,key,useConnectionPooling:false);
        Assert.True(context.HasValidIntegrity());
        using var connection=context.CreateOpenConnection();
        using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type='table'),
                   (SELECT COUNT(*) FROM Users);
            """;
        using var reader=command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt64(0),reader.GetInt64(1));
    }

    [Fact]
    public async Task ExportFromClosedEncryptedImage_DoesNotModifySourceOrCreateWalShm()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        string live=Path.Combine(_root,"export-live.db");
        string originalKeyPath=Path.Combine(_root,"export-live.key");
        string backup=Path.Combine(_root,"export-standalone.dbbackup");
        string offline=Path.Combine(_root,"export-offline.db");
        string wrappedPackage=Path.Combine(_root,"export-offline.altrecovery");
        var store=new WindowsDatabaseKeyStore(originalKeyPath);
        string key=store.LoadOrCreate(live);
        using(var db=new AlTamamDbContext(live,key,useConnectionPooling:false))
        {
            await db.InitializeAsync();
            await db.CreateVerifiedEncryptedBackupAsync(backup);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Copy(backup,offline);
        var originalSha=Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(offline)));
        Assert.False(File.Exists(offline+"-wal"));
        Assert.False(File.Exists(offline+"-shm"));

        // A portable-key export is a credential operation only. It must not
        // open an offline customer database with ReadWriteCreate nor turn it
        // into a WAL DB while claiming to "verify integrity".
        store.ExportPortableRecoveryKey(offline,wrappedPackage,Phrase);

        Assert.Equal(key,PortableRecoveryKeyPackage.Unseal(
            await File.ReadAllBytesAsync(wrappedPackage),Phrase));
        Assert.Equal(originalSha,Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(offline))));
        Assert.False(File.Exists(offline+"-wal"));
        Assert.False(File.Exists(offline+"-shm"));
        Assert.False(File.Exists(offline+"-journal"));
        Assert.Empty(Directory.GetDirectories(_root,".altamam-key-preflight-*"));
    }

    [Fact]
    public async Task ExportWithLiveWal_PreservesNewestCommittedSqlcipherStateAndKey()
    {
        if(!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        string live=Path.Combine(_root,"live-wal.db");
        string protectedKey=Path.Combine(_root,"live-wal.key");
        string wrapped=Path.Combine(_root,"live-wal.altrecovery");
        var store=new WindowsDatabaseKeyStore(protectedKey);
        string key=store.LoadOrCreate(live);
        using(var db=new AlTamamDbContext(live,key,useConnectionPooling:false))
        {
            await db.InitializeAsync();
            using(var writer=db.CreateOpenConnection())
            {
                using(var change=writer.CreateCommand())
                {
                    change.CommandText="PRAGMA user_version = 467;";
                    change.ExecuteNonQuery();
                }
                Assert.True(File.Exists(live+"-wal"));
                store.ExportPortableRecoveryKey(live,wrapped,Phrase);
                using var check=writer.CreateCommand();
                check.CommandText="PRAGMA user_version;";
                Assert.Equal(467,Convert.ToInt32(check.ExecuteScalar()));
            }
        }
        Assert.Equal(key,PortableRecoveryKeyPackage.Unseal(
            await File.ReadAllBytesAsync(wrapped),Phrase));
        Assert.Empty(Directory.GetDirectories(_root,".altamam-key-preflight-*"));
    }

    [Fact]
    public async Task ExportAndRecoverOnIndependentDpapiLocation_ExactOriginalDatabaseAndIdentity()
    {
        if(!OperatingSystem.IsWindows())return;
        Directory.CreateDirectory(_root);
        string sourcePath=Path.Combine(_root,"source.db");
        string sourceKey=Path.Combine(_root,"source.protected");
        string recoveryPath=Path.Combine(_root,"separate.altrecovery");
        string backup=Path.Combine(_root,"source.dbbackup");
        string otherFolder=Path.Combine(_root,"independent-device");
        Directory.CreateDirectory(otherFolder);
        string targetDb=Path.Combine(otherFolder,"altamam.db");
        string targetKey=Path.Combine(otherFolder,"db.key.protected");
        var sourceStore=new WindowsDatabaseKeyStore(sourceKey);
        string secret=sourceStore.LoadOrCreate(sourcePath);
        using(var db=new AlTamamDbContext(sourcePath,secret))
        {
            await db.InitializeAsync();
            await db.CreateVerifiedEncryptedBackupAsync(backup);
        }
        sourceStore.ExportPortableRecoveryKey(sourcePath,recoveryPath,Phrase);
        Assert.True(File.Exists(recoveryPath));
        Assert.DoesNotContain(secret,Encoding.ASCII.GetString(File.ReadAllBytes(recoveryPath)));
        Assert.Throws<IOException>(()=>
            sourceStore.ExportPortableRecoveryKey(sourcePath,recoveryPath,Phrase));
        File.Copy(backup,targetDb);
        // Snapshot counts are read from the backup, NOT from the destination
        // before key import. A writable target read would enable SQLite WAL
        // and invalidate a clean pre-login recovery fixture.
        var before=ReadLogicalSnapshot(backup,secret);
        var untouchedTargetSha=Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(targetDb)));
        var targetStore=new WindowsDatabaseKeyStore(targetKey);
        Assert.Throws<CryptographicException>(()=>
            targetStore.RecoverExistingDatabaseKey(targetDb,recoveryPath,
                "Incorrect password long enough to be accepted"));
        Assert.False(File.Exists(targetKey));
        Assert.Equal(untouchedTargetSha,Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(targetDb))));
        Assert.False(File.Exists(targetDb+"-wal"));
        Assert.False(File.Exists(targetDb+"-shm"));
        targetStore.RecoverExistingDatabaseKey(targetDb,recoveryPath,Phrase);
        Assert.Equal(secret,targetStore.LoadOrCreate(targetDb));
        using(var db=new AlTamamDbContext(targetDb,targetStore.LoadOrCreate(targetDb)))
            Assert.True(db.HasValidIntegrity());
        Assert.Throws<InvalidOperationException>(()=>
            targetStore.RecoverExistingDatabaseKey(targetDb,recoveryPath,Phrase));
        Assert.Equal(before,ReadLogicalSnapshot(targetDb,secret));
    }

    [Fact]
    public async Task RecoverExistingKey_ReadOnlyPreflight_LeavesTargetEncryptedDatabaseByteIdentical()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        string source=Path.Combine(_root,"readonly-source.db");
        string sourceKeyPath=Path.Combine(_root,"readonly-source.key");
        string candidate=Path.Combine(_root,"readonly-copy.dbbackup");
        string recovery=Path.Combine(_root,"readonly-key.altrecovery");
        string targetRoot=Path.Combine(_root,"readonly-target");
        Directory.CreateDirectory(targetRoot);
        string target=Path.Combine(targetRoot,"altamam.db");
        string targetKey=Path.Combine(targetRoot,"db.key.protected");
        var sourceStore=new WindowsDatabaseKeyStore(sourceKeyPath);
        string originalKey=sourceStore.LoadOrCreate(source);
        using(var db=new AlTamamDbContext(source,originalKey,useConnectionPooling:false))
        {
            await db.InitializeAsync();
            await db.CreateVerifiedEncryptedBackupAsync(candidate);
        }
        sourceStore.ExportPortableRecoveryKey(source,recovery,Phrase);
        File.Copy(candidate,target);
        string before=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target)));
        Assert.False(File.Exists(target+"-wal"));
        Assert.False(File.Exists(target+"-shm"));

        new WindowsDatabaseKeyStore(targetKey)
            .RecoverExistingDatabaseKey(target,recovery,Phrase);

        Assert.True(File.Exists(targetKey));
        // A verification-only credential import is not allowed to turn an
        // offline SQLCipher backup from DELETE into WAL or alter ANY DB bytes.
        Assert.Equal(before,Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(target))));
        Assert.False(File.Exists(target+"-wal"));
        Assert.False(File.Exists(target+"-shm"));
        Assert.Equal(originalKey,new WindowsDatabaseKeyStore(targetKey).LoadOrCreate(target));
    }

    [Fact]
    public async Task RecoverExistingKey_RejectsFutureSchemaWithoutWritingDpapiKeyOrDatabase()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        string source=Path.Combine(_root,"future-source.db");
        string sourceKeyPath=Path.Combine(_root,"future-source.key");
        string backup=Path.Combine(_root,"future.dbbackup");
        string recovery=Path.Combine(_root,"future-key.altrecovery");
        string targetRoot=Path.Combine(_root,"future-target");
        Directory.CreateDirectory(targetRoot);
        string target=Path.Combine(targetRoot,"altamam.db");
        string targetKey=Path.Combine(targetRoot,"db.key.protected");
        var sourceStore=new WindowsDatabaseKeyStore(sourceKeyPath);
        string originalKey=sourceStore.LoadOrCreate(source);
        using(var db=new AlTamamDbContext(source,originalKey,useConnectionPooling:false))
        {
            await db.InitializeAsync();
            await db.CreateVerifiedEncryptedBackupAsync(backup);
        }
        sourceStore.ExportPortableRecoveryKey(source,recovery,Phrase);
        File.Copy(backup,target);

        // A synthetically future-dated schema is still an INTEGRITY-CHECK-OK
        // SQLCipher DB, but is not a compatible application database.
        using(var db=new AlTamamDbContext(target,originalKey,useConnectionPooling:false))
        using(var connection=db.CreateOpenConnection())
        {
            using(var mutate=connection.CreateCommand())
            {
                mutate.CommandText=
                    "UPDATE SchemaVersions SET SchemaVersion=999 "+
                    "WHERE SchemaVersion=(SELECT MAX(SchemaVersion) FROM SchemaVersions);";
                Assert.Equal(1,mutate.ExecuteNonQuery());
            }
            using(var mode=connection.CreateCommand())
            {
                mode.CommandText="PRAGMA journal_mode=DELETE;";
                Assert.Equal("delete",mode.ExecuteScalar()?.ToString(),
                    ignoreCase:true);
            }
        }
        string before=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target)));

        Assert.Throws<CryptographicException>(()=>
            new WindowsDatabaseKeyStore(targetKey)
                .RecoverExistingDatabaseKey(target,recovery,Phrase));
        Assert.False(File.Exists(targetKey));
        Assert.Equal(before,Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(target))));
        Assert.False(File.Exists(target+"-wal"));
        Assert.False(File.Exists(target+"-shm"));
    }

    [Fact]
    public async Task WrongDatabaseEvenWithCorrectPackage_CannotInstallAnyKey()
    {
        if(!OperatingSystem.IsWindows())return;
        Directory.CreateDirectory(_root);
        string first=Path.Combine(_root,"original.db"),encrypted=Path.Combine(_root,"rescued.db");
        string protectedKey=Path.Combine(_root,"original.key"),otherProtected=Path.Combine(_root,"other.key");
        string package=Path.Combine(_root,"offline.altrecovery");
        var store=new WindowsDatabaseKeyStore(protectedKey);
        string key=store.LoadOrCreate(first);
        using(var db=new AlTamamDbContext(first,key))await db.InitializeAsync();
        store.ExportPortableRecoveryKey(first,package,Phrase);
        string wrongKey=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // The original test left pooled SQLite/WAL handles open on the
        // candidate, so it failed at a Windows sharing violation instead of
        // actually exercising the wrong-key verification boundary.
        string wrongBackup=Path.Combine(_root,"wrong-database.dbbackup");
        using(var other=new AlTamamDbContext(encrypted,wrongKey,useConnectionPooling:false))
        {
            await other.InitializeAsync();
            await other.CreateVerifiedEncryptedBackupAsync(wrongBackup);
        }
        string offlineCandidate=Path.Combine(_root,"wrong-offline-target.db");
        File.Copy(wrongBackup,offlineCandidate);
        string originalSha=Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(offlineCandidate)));
        Assert.Throws<CryptographicException>(()=>
            new WindowsDatabaseKeyStore(otherProtected)
                .RecoverExistingDatabaseKey(offlineCandidate,package,Phrase));
        Assert.False(File.Exists(otherProtected));
        Assert.Equal(originalSha,Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(offlineCandidate))));
        Assert.False(File.Exists(offlineCandidate+"-wal"));
        Assert.False(File.Exists(offlineCandidate+"-shm"));
    }

    public void Dispose()
    {
        // The test-only materialized databases use Microsoft.Data.Sqlite pooling.
        // Release idle handles after semantic DB assertions and before cleanup.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try{if(Directory.Exists(_root))Directory.Delete(_root,true);}
        catch(IOException)
        {
            // Windows native SQLCipher may transiently retain a handle after
            // pooled connections close. Synthetic fixture cleanup is best-effort;
            // the actual recovery, failure and nonmutation assertions run above.
        }
        catch(UnauthorizedAccessException)
        {
            // Identical test-only cleanup limitation on locked temp directories.
        }
    }
}
