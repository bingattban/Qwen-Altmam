using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AlTamam.Infrastructure.Security;
using Xunit;

namespace AlTamam.Tests.Security;

/// <summary>
/// Verify that existing SQLCipher files are never orphaned by a replacement DPAPI key.
/// Windows DPAPI cases run on the native Windows CI; no secret or protected blob is committed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDatabaseKeyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "altamam-keystore-" + Guid.NewGuid().ToString("N"));
    private string KeyPath => Path.Combine(_root, "db.key.protected");
    private string DatabasePath => Path.Combine(_root, "altamam.db");

    [Fact]
    public void FreshKey_IsDpapiProtected_AndReopenPreservesExistingIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDatabaseKeyStore(KeyPath);
        string first = store.LoadOrCreate(DatabasePath);
        Assert.Matches("^[0-9A-F]{64}$", first);
        byte[] protectedBytes = File.ReadAllBytes(KeyPath);
        Assert.NotEqual(Encoding.ASCII.GetBytes(first), protectedBytes);
        File.WriteAllBytes(DatabasePath, new byte[] { 0x5A, 0x00, 0x12 });
        string second = store.LoadOrCreate(DatabasePath);
        Assert.Equal(first, second);
        Assert.Equal(protectedBytes, File.ReadAllBytes(KeyPath));
        using var unprotected = new MemoryStream(ProtectedData.Unprotect(protectedBytes, null,
            DataProtectionScope.CurrentUser));
        Assert.Equal(first, Encoding.ASCII.GetString(unprotected.ToArray()));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("wal")]
    [InlineData("shm")]
    [InlineData("backup")]
    public void MissingKey_WhenOldDataExists_RefusesToGenerateAReplacement(string artifact)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        string filename = artifact switch
        {
            "wal" => DatabasePath + "-wal",
            "shm" => DatabasePath + "-shm",
            "backup" => Path.Combine(_root, "backups", "altamam-2026-10-23.db"),
            _ => DatabasePath
        };
        Directory.CreateDirectory(Path.GetDirectoryName(filename)!);
        File.WriteAllBytes(filename, new byte[] { 0x03, 0x18 });
        var error=Assert.Throws<CryptographicException>(() =>
            new WindowsDatabaseKeyStore(KeyPath).LoadOrCreate(DatabasePath));
        Assert.Equal(DatabaseStartupFailureClassifier.MissingEncryptionKey,error.Message);
        Assert.False(File.Exists(KeyPath));
        Assert.Equal(new byte[] { 0x03, 0x18 }, File.ReadAllBytes(filename));
    }

    [Theory]
    [InlineData("pending-intent")]
    [InlineData("journal-temp")]
    [InlineData("original-rollback")]
    [InlineData("staged-backup")]
    [InlineData("replay-copy")]
    [InlineData("failed-live-image")]
    [InlineData("sqlite-journal")]
    [InlineData("portable-recovery")]
    public void MissingKeyWithOnlyRootRecoveryEvidence_FailsClosedBeforeNewKeyCreation(
        string kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        string artifact = kind switch
        {
            "pending-intent" => Path.Combine(_root,
                ".altamam-restore-activation.pending.json"),
            "journal-temp" => Path.Combine(_root,
                ".altamam-restore-activation.pending.json.123.tmp"),
            "original-rollback" => Path.Combine(_root,
                ".altamam-restore-rollback-" + Guid.NewGuid().ToString("N") + ".dbbackup"),
            "staged-backup" => Path.Combine(_root,
                ".altamam-restore-stage-" + Guid.NewGuid().ToString("N") + ".dbbackup"),
            "replay-copy" => Path.Combine(_root,
                ".altamam-restore-reconcile-" + Guid.NewGuid().ToString("N") + ".dbbackup"),
            "failed-live-image" => DatabasePath + ".failed-restore-" +
                Guid.NewGuid().ToString("N"),
            "sqlite-journal" => DatabasePath + "-journal",
            "portable-recovery" => Path.Combine(_root, "portable.altrecovery"),
            _ => throw new InvalidOperationException("Unknown test recovery kind.")
        };
        // No live database, no key, and no backup-directory files. Historically
        // that combination incorrectly allowed a fresh DPAPI SQLCipher key.
        byte[] original = new byte[] { 0x09, 0x18, 0x27, 0x36 };
        File.WriteAllBytes(artifact, original);
        var store = new WindowsDatabaseKeyStore(KeyPath);

        var rejected = Assert.Throws<CryptographicException>(() =>
            store.LoadOrCreate(DatabasePath));
        Assert.Equal(DatabaseStartupFailureClassifier.MissingEncryptionKey,
            rejected.Message);
        Assert.False(File.Exists(KeyPath));
        Assert.False(File.Exists(DatabasePath));
        Assert.Equal(original, File.ReadAllBytes(artifact));
    }

    [Fact]
    public void EmptyBackupDirectoryAndUnrelatedSibling_DoNotBlockFirstUseKeyCreation()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        string unrelated = Path.Combine(_root, "notes.txt");
        File.WriteAllText(unrelated, "first-use setup note");
        string created = new WindowsDatabaseKeyStore(KeyPath).LoadOrCreate(DatabasePath);
        Assert.Matches("^[0-9A-F]{64}$", created);
        Assert.Equal(created, new WindowsDatabaseKeyStore(KeyPath).LoadOrCreate(DatabasePath));
        Assert.Equal("first-use setup note", File.ReadAllText(unrelated));
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public void CorruptProtectedBlob_FailsClosedAndNeverChangesTheKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        byte[] damaged = RandomNumberGenerator.GetBytes(128);
        File.WriteAllBytes(KeyPath, damaged);
        Assert.Throws<CryptographicException>(() =>
            new WindowsDatabaseKeyStore(KeyPath).LoadOrCreate(DatabasePath));
        Assert.Equal(damaged, File.ReadAllBytes(KeyPath));
        Assert.False(File.Exists(DatabasePath));
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    public void DpapiBlobWithInvalidPlaintext_IsRejectedWithoutRotation(string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, encrypted);
        Assert.Throws<CryptographicException>(() =>
            new WindowsDatabaseKeyStore(KeyPath).LoadOrCreate(DatabasePath));
        Assert.Equal(encrypted, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void LegacyUtf8HexDpapiKey_RemainsReadableByteForByte()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        string legacy = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] oldProtected = ProtectedData.Protect(Encoding.UTF8.GetBytes(legacy), null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, oldProtected);
        string loaded = new WindowsDatabaseKeyStore(KeyPath).LoadOrCreate(DatabasePath);
        Assert.Equal(legacy, loaded);
        Assert.Equal(oldProtected, File.ReadAllBytes(KeyPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
