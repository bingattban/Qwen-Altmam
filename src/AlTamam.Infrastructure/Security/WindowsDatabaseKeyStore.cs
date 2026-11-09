using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AlTamam.Infrastructure.Security;

/// <summary>
/// Windows per-user DPAPI key store for SQLCipher. Fail closed if a previous database
/// (or its recovery material) exists without the original key.
/// Compatible with the original UTF-8-hex DPAPI blobs written by LocalSecretStore.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDatabaseKeyStore
{
    private const string RecoveryRequired =
        DatabaseStartupFailureClassifier.MissingEncryptionKey;
    private readonly string _keyPath;

    public WindowsDatabaseKeyStore(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        _keyPath = Path.GetFullPath(keyPath);
    }

    public string LoadOrCreate(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("مخزن المفاتيح هذا يتطلب Windows DPAPI.");

        string database = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);

        // Inter-process guard: exactly one owner can create/read the key at a time.
        // A contention failure is safer than silently generating a second key.
        using var lease = new FileStream(_keyPath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        if (File.Exists(_keyPath))
            return ReadExisting();

        // A missing key is NOT a fresh installation if any encrypted data may remain.
        if (File.Exists(database) || File.Exists(database + "-wal") ||
            File.Exists(database + "-shm") || File.Exists(database + "-journal") ||
            ContainsRecoveryArtifacts(database))
            throw new CryptographicException(RecoveryRequired);

        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        string temporary = _keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string hex = Convert.ToHexString(key);
            // Match the existing on-disk representation: DPAPI(ASCII 64-digit hex).
            plaintext = Encoding.ASCII.GetBytes(hex);
            ciphertext = ProtectedData.Protect(plaintext, optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            using (var file = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                file.Write(ciphertext);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _keyPath, overwrite: false);
            return hex;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Creates a portable recovery-key file with a DIFFERENT independently held
    /// passphrase. Does not export the DPAPI blob: those bytes cannot be opened
    /// on another machine. Intended for native GeneralAdmin UI only.
    /// </summary>
    public void ExportPortableRecoveryKey(string databasePath,string destinationPath,string passphrase)
    {
        if(!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI is required.");
        string database=Path.GetFullPath(databasePath);
        string output=Path.GetFullPath(destinationPath);
        if(!File.Exists(database) ||
           string.Equals(output,database,StringComparison.OrdinalIgnoreCase) ||
           string.Equals(output,_keyPath,StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(Path.GetExtension(output),".altrecovery",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a separate .altrecovery file for the recovery package.");
        // No new key may ever be generated as a side effect of export.
        using var lease=new FileStream(_keyPath+".lock",FileMode.OpenOrCreate,
            FileAccess.ReadWrite,FileShare.None);
        if(!File.Exists(_keyPath))throw new CryptographicException(RecoveryRequired);
        string key=ReadExisting();
        using(var databaseContext=new AlTamam.Infrastructure.Persistence.AlTamamDbContext(database,key))
            if(!databaseContext.HasValidIntegrity())
                throw new CryptographicException(RecoveryRequired);
        byte[] body=PortableRecoveryKeyPackage.Seal(key,passphrase);
        string temp=output+"."+Guid.NewGuid().ToString("N")+".pending";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,
                FileAccess.Write,FileShare.None))
            {
                stream.Write(body);
                stream.Flush(flushToDisk:true);
            }
            // Never overwrite a previously protected recovery package.
            File.Move(temp,output,overwrite:false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            if(File.Exists(temp))File.Delete(temp);
        }
    }

    /// <summary>
    /// Install the ORIGINAL key on another Windows user profile, only after
    /// decrypting an independently password-protected package AND proving the
    /// existing encrypted DB can be opened with it. Never replaces a key file.
    /// The administrator must explicitly place the correct encrypted database
    /// at the destination first; this API does not restore or move databases.
    /// </summary>
    public void RecoverExistingDatabaseKey(string databasePath,string packagePath,string passphrase)
    {
        if(!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI is required.");
        string database=Path.GetFullPath(databasePath);
        if(!File.Exists(database))throw new FileNotFoundException(
            "ضع نسخة قاعدة البيانات المشفرة في المسار المحدد أولاً.");
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        using var lease=new FileStream(_keyPath+".lock",FileMode.OpenOrCreate,
            FileAccess.ReadWrite,FileShare.None);
        if(File.Exists(_keyPath))
            throw new InvalidOperationException("يوجد مفتاح قائم؛ لن يُستبدل أو يُدار تلقائياً.");
        string package=Path.GetFullPath(packagePath);
        if(!string.Equals(Path.GetExtension(package),".altrecovery",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unsupported recovery package type.");
        byte[] wrapped=File.ReadAllBytes(package);
        string key;
        try{key=PortableRecoveryKeyPackage.Unseal(wrapped,passphrase);}
        finally{CryptographicOperations.ZeroMemory(wrapped);}
        // The verification MUST NOT open the customer's database with SQLite:
        // even ReadOnly mode can create -wal/-shm sidecars on a WAL-format DB.
        // A separate encrypted, isolated copy is used for every SQLCipher query.
        // Do not accept an active WAL/journal: a standalone copy would omit
        // uncheckpointed transactions and could validate incomplete history.
        if(File.Exists(database+"-wal") || File.Exists(database+"-shm") ||
           File.Exists(database+"-journal"))
            throw new CryptographicException(RecoveryRequired);
        string inspectionDirectory=Path.Combine(
            Path.GetDirectoryName(database)!,
            ".altamam-key-preflight-"+Guid.NewGuid().ToString("N"));
        string inspectionCopy=Path.Combine(inspectionDirectory,"candidate.dbbackup");
        Directory.CreateDirectory(inspectionDirectory);
        try
        {
            // Read sharing denies an active database writer and prevents new
            // writers while the byte-exact copy is inspected and key saved.
            using var original=new FileStream(database,FileMode.Open,FileAccess.Read,
                FileShare.Read,81920,FileOptions.SequentialScan);
            using(var staged=new FileStream(inspectionCopy,FileMode.CreateNew,
                FileAccess.Write,FileShare.None,81920,FileOptions.WriteThrough))
            {
                original.CopyTo(staged);
                staged.Flush(flushToDisk:true);
            }
            // A brand-new Windows process has not initialized AlTamamDbContext.
            SQLitePCL.Batteries_V2.Init();
            AlTamam.Infrastructure.Persistence.EncryptedBackupInspection inspected;
            try
            {
                inspected=AlTamam.Infrastructure.Persistence.EncryptedBackupInspector
                    .VerifyAsync(inspectionCopy,database,key,CancellationToken.None)
                    .GetAwaiter().GetResult();
                if(inspected.SchemaVersion!=
                   AlTamam.Infrastructure.Persistence.AlTamamDbContext.CurrentSchemaVersion)
                    throw new CryptographicException(RecoveryRequired);
            }
            catch(Exception ex) when(ex is Microsoft.Data.Sqlite.SqliteException ||
                                     ex is InvalidOperationException ||
                                     ex is InvalidDataException ||
                                     ex is FormatException ||
                                     ex is OverflowException)
            {
                throw new CryptographicException(RecoveryRequired);
            }
            // The original has been locked read-only since BEFORE the copy.
            // It cannot be replaced between verification and DPAPI key write.
            original.Position=0;
            string liveSha=Convert.ToHexString(SHA256.HashData(original));
            if(!string.Equals(liveSha,inspected.Sha256,
                StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException(RecoveryRequired);
            if(File.Exists(database+"-wal") || File.Exists(database+"-shm") ||
               File.Exists(database+"-journal"))
                throw new CryptographicException(RecoveryRequired);

            byte[] plaintext=Encoding.ASCII.GetBytes(key);
            byte[] protectedBlob=Array.Empty<byte>();
            string temp=_keyPath+"."+Guid.NewGuid().ToString("N")+".pending";
            try
            {
                protectedBlob=ProtectedData.Protect(plaintext,null,DataProtectionScope.CurrentUser);
                using(var stream=new FileStream(temp,FileMode.CreateNew,
                    FileAccess.Write,FileShare.None))
                {
                    stream.Write(protectedBlob);
                    stream.Flush(flushToDisk:true);
                }
                File.Move(temp,_keyPath,overwrite:false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if(protectedBlob.Length>0)CryptographicOperations.ZeroMemory(protectedBlob);
                if(File.Exists(temp))File.Delete(temp);
            }
        }
        finally
        {
            // This isolated directory contains ONLY the disposable encrypted
            // inspection copy and its SQLCipher sidecars. It is never a customer
            // backup. An interrupted cleanup must never delete the original DB.
            try { Directory.Delete(inspectionDirectory,recursive:true); }
            catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }

    }

    private string ReadExisting()
    {
        var length = new FileInfo(_keyPath).Length;
        if (length is < 32 or > 16384)
            throw new CryptographicException(RecoveryRequired);

        byte[]? plaintext = null;
        try
        {
            byte[] protectedBlob = File.ReadAllBytes(_keyPath);
            try
            {
                plaintext = ProtectedData.Unprotect(protectedBlob,
                    optionalEntropy: null, DataProtectionScope.CurrentUser);
            }
            finally { CryptographicOperations.ZeroMemory(protectedBlob); }

            if (plaintext.Length != 64 ||
                plaintext.Any(c => !((c >= (byte)'0' && c <= (byte)'9') ||
                    (c >= (byte)'A' && c <= (byte)'F') ||
                    (c >= (byte)'a' && c <= (byte)'f'))))
                throw new CryptographicException(RecoveryRequired);

            return Encoding.ASCII.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            // No key replacement, no silent reset. Do not include the secret in errors.
            throw new CryptographicException(RecoveryRequired);
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private bool ContainsRecoveryArtifacts(string databasePath)
    {
        // Missing live DB + missing DPAPI key is NOT sufficient evidence of a
        // clean installation. Power loss may leave only a durable restore
        // intent, pre-restore rollback, replay image, stage or failed live DB
        // beside the missing database. Never mint an unrelated SQLCipher key.
        string? dataRoot = Path.GetDirectoryName(databasePath);
        if (dataRoot is null) return false;
        string databaseFile = Path.GetFileName(databasePath);

        // R14 copies the original encrypted DB into a uniquely GUID-named
        // scratch DIRECTORY during pre-login recovery verification. Abrupt
        // process termination can leave this directory as the only surviving
        // encrypted evidence. R13's file-only scan did not detect it.
        const string preflightPrefix=".altamam-key-preflight-";
        if (Directory.Exists(dataRoot) &&
            Directory.EnumerateDirectories(dataRoot,"*",SearchOption.TopDirectoryOnly)
                .Any(dir =>
                {
                    string name=Path.GetFileName(dir);
                    return name.StartsWith(preflightPrefix,StringComparison.OrdinalIgnoreCase) &&
                           name.Length==preflightPrefix.Length+32 &&
                           name.AsSpan(preflightPrefix.Length).ToString().All(Uri.IsHexDigit);
                }))
            return true;

        // DPAPI write-through is also two-phase (.tmp for new keys, .pending
        // for explicitly recovered originals). If power fails before rename,
        // the original protected key may exist only as an orphan temp file.
        // Inspect the ACTUAL key folder, which need not be the DB folder.
        string? keyRoot=Path.GetDirectoryName(_keyPath);
        string keyPrefix=Path.GetFileName(_keyPath)+".";
        if(keyRoot is not null && Directory.Exists(keyRoot) &&
           Directory.EnumerateFiles(keyRoot,"*",SearchOption.TopDirectoryOnly)
               .Any(file =>
               {
                   string name=Path.GetFileName(file);
                   if(!name.StartsWith(keyPrefix,StringComparison.OrdinalIgnoreCase))
                       return false;
                   string tail=name[keyPrefix.Length..];
                   string guid=tail.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase)
                       ?tail[..^4]
                       :tail.EndsWith(".pending",StringComparison.OrdinalIgnoreCase)
                           ?tail[..^8] : "";
                   return guid.Length==32 && guid.All(Uri.IsHexDigit);
               }))
            return true;

        if (Directory.Exists(dataRoot) &&
            Directory.EnumerateFiles(dataRoot, "*", SearchOption.TopDirectoryOnly)
                .Any(file =>
                {
                    string name = Path.GetFileName(file);
                    return name.StartsWith(".altamam-restore-activation.pending.json",
                               StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith(".altamam-restore-rollback-",
                               StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith(".altamam-restore-stage-",
                               StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith(".altamam-restore-reconcile-",
                               StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith(databaseFile + ".failed-restore-",
                               StringComparison.OrdinalIgnoreCase) ||
                           name.EndsWith(".altrecovery",
                               StringComparison.OrdinalIgnoreCase);
                }))
            return true;

        // Backups remain recovery candidates even if the primary database
        // vanished. Access/permission errors must fail closed, not silently
        // become permission to create a new key.
        string backupDirectory = Path.Combine(dataRoot, "backups");
        return Directory.Exists(backupDirectory) &&
            Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories).Any();
    }
}
