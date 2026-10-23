using System.Security.Cryptography;
using System.Text;

namespace AlTamam.Infrastructure.Security;

/// <summary>
/// Windows per-user DPAPI key store for SQLCipher. Fail closed if a previous database
/// (or its recovery material) exists without the original key.
/// Compatible with the original UTF-8-hex DPAPI blobs written by LocalSecretStore.
/// </summary>
public sealed class WindowsDatabaseKeyStore
{
    private const string RecoveryRequired =
        "تعذر استعادة مفتاح تشفير قاعدة البيانات. استعد المفتاح الأصلي والنسخة الاحتياطية؛ لم يتم تغيير البيانات.";
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
            File.Exists(database + "-shm") || ContainsRecoveryArtifacts(database))
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

    private static bool ContainsRecoveryArtifacts(string databasePath)
    {
        // A backup is a recovery candidate, not permission to issue a new key.
        string? dataRoot = Path.GetDirectoryName(databasePath);
        if (dataRoot is null) return false;
        string backupDirectory = Path.Combine(dataRoot, "backups");
        return Directory.Exists(backupDirectory) &&
            Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories).Any();
    }
}
