using System.Runtime.Versioning;
using System.Security.Cryptography;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Windows DPAPI/SQLCipher integration at the real native GeneralAdmin session
/// boundary. Only disposable encrypted data and separately held test secrets.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection("SQLCipherGlobalPool")]
public sealed class NativePortableRecoveryExportTests : IDisposable
{
    private const string AdminPassword = "NativeExport1234";
    private const string RecoveryPassphrase = "Separate recovery password for Windows 2026";
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "altamam-native-key-export-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "live.db");
    private string ProtectedKey => Path.Combine(_root, "db.key.protected");
    private string Package => Path.Combine(_root, "independent.altrecovery");

    private async Task<(AlTamamDbContext db, BridgeSharedServices sessions,
        BridgePrincipal founder, WindowsDatabaseKeyStore keyStore, string key)> SetupAsync()
    {
        Directory.CreateDirectory(_root);
        var store = new WindowsDatabaseKeyStore(ProtectedKey);
        string key = store.LoadOrCreate(Database);
        var db = new AlTamamDbContext(Database, key, useConnectionPooling: false);
        await db.InitializeAsync();
        var sessions = new BridgeSharedServices(db);
        var setup = await sessions.ProvisionFirstAdministratorAsync(
            "native-export-owner", AdminPassword, CancellationToken.None);
        Assert.True(setup.Success, setup.UserMessage);
        var (principal, error) = await sessions.AuthenticateAsync(
            "native-export-owner", AdminPassword, CancellationToken.None);
        Assert.Null(error);
        return (db, sessions, Assert.IsType<BridgePrincipal>(principal), store, key);
    }

    [Fact]
    public async Task FreshFounderPassword_ExportsOnlyAnEncryptedKeyAndNeverMutatesLiveDatabase()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (db, sessions, founder, store, key) = await SetupAsync();
        using (db)
        {
            string before = await Sha256Async(Database);
            var export = new NativePortableRecoveryExportService(db, sessions, store);
            Assert.True(await export.TryExportAsync(
                founder, AdminPassword, RecoveryPassphrase, Package));
            Assert.True(File.Exists(Package));
            Assert.Equal(PortableRecoveryKeyPackage.FileLength,
                new FileInfo(Package).Length);
            Assert.Equal(key, PortableRecoveryKeyPackage.Unseal(
                await File.ReadAllBytesAsync(Package), RecoveryPassphrase));
            Assert.Equal(before, await Sha256Async(Database));

            // Existing recovery material must never be overwritten.
            await Assert.ThrowsAsync<IOException>(() => export.TryExportAsync(
                founder, AdminPassword, RecoveryPassphrase, Package));
            Assert.Equal(before, await Sha256Async(Database));
            Assert.True(File.Exists(ProtectedKey));
        }
    }

    [Fact]
    public async Task WrongPasswordRevokedOrForgedSession_CannotCreateRecoveryMaterial()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (db, sessions, founder, store, _) = await SetupAsync();
        using (db)
        {
            var export = new NativePortableRecoveryExportService(db, sessions, store);
            Assert.False(await export.TryExportAsync(
                founder, "WrongNativePassword777", RecoveryPassphrase, Package));
            Assert.False(File.Exists(Package));
            var forged = new BridgePrincipal(
                founder.UserId, founder.Username, UserRole.Supervisor, founder.SessionNonce);
            Assert.False(await export.TryExportAsync(
                forged, AdminPassword, RecoveryPassphrase, Package));
            Assert.False(File.Exists(Package));
            await sessions.RevokeSessionAsync(founder.SessionNonce);
            Assert.False(await export.TryExportAsync(
                founder, AdminPassword, RecoveryPassphrase, Package));
            Assert.False(File.Exists(Package));
        }
    }

    [Fact]
    public async Task ReusingAdministratorPasswordOrWeakRecoveryPassphrase_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (db, sessions, founder, store, _) = await SetupAsync();
        using (db)
        {
            var export = new NativePortableRecoveryExportService(db, sessions, store);
            Assert.False(await export.TryExportAsync(
                founder, AdminPassword, AdminPassword, Package));
            Assert.False(await export.TryExportAsync(
                founder, AdminPassword, "short", Package));
            Assert.False(File.Exists(Package));
        }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive:true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
