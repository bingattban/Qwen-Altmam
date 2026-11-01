using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Phase 12: actual encrypted backup files and disconnected-folder recovery,
/// without changing the user's machine, E: drive, or production database.</summary>
public sealed class Phase12BackupDiskRecoveryTests : IAsyncLifetime, IDisposable
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 24, 15, 59, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }

    private readonly string _database = SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _folder = Path.Combine(Path.GetTempPath(),
        "altamam-phase12-disk-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Admin = new(
        "TST-PHASE12-GA", "phase12-admin", UserRole.GeneralAdministration,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _db = new AlTamamDbContext(_database, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    [Fact]
    public async Task RemovedSelectedDisk_FailsVisibly_PreservesBackup_AndCatchesUpOnceWhenRestored()
    {
        var folderSettings = new OutputDirectorySettings(_db);
        var selected = await folderSettings.SaveAsync(_folder, Admin, CancellationToken.None);
        Assert.True(selected.Success, selected.UserMessage);
        var scheduler = new AutomaticBackupService(_db, _clock);
        Assert.True((await scheduler.ConfigureAsync(new JsonObject
        {
            ["enabled"] = true, ["backupTime"] = "16:00"
        }, Admin, CancellationToken.None)).Success);

        Assert.False(await scheduler.RunIfDueAsync());
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await scheduler.RunIfDueAsync());
        Assert.False(await scheduler.RunIfDueAsync());

        string firstPath;
        using (var connection = _db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT BackupFilePath FROM BackupHistory WHERE BackupId='AUTO-20261024';";
            firstPath = Assert.IsType<string>(await command.ExecuteScalarAsync());
        }
        Assert.True(File.Exists(firstPath));
        byte[] firstDigest = SHA256.HashData(await File.ReadAllBytesAsync(firstPath));

        _clock.Advance(TimeSpan.FromDays(1));
        // Simulate an ejected removable drive without depending on runner hardware.
        string temporarilyRemoved = _folder + "-disconnected";
        Directory.Move(_folder, temporarilyRemoved);
        try
        {
            var failed = await Assert.ThrowsAsync<IOException>(
                () => scheduler.RunIfDueAsync());
            Assert.Contains("مجلد النسخ", failed.Message);
            var rejectedFolder = await folderSettings.SaveAsync(_folder, Admin, CancellationToken.None);
            Assert.False(rejectedFolder.Success);
            var manual = await new SettingsReadBackupService(_db, _clock)
                .BackupAsync(Admin, CancellationToken.None);
            Assert.False(manual.Success);
            Assert.Contains("هذا المسار غير صالح أو غير متاح", manual.UserMessage);
            Assert.Equal(_folder, await folderSettings.GetAsync());
            using var connection = _db.CreateOpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM BackupHistory WHERE BackupId='AUTO-20261025';";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Move(temporarilyRemoved, _folder);
        }

        Assert.True(await scheduler.RunIfDueAsync());
        Assert.False(await scheduler.RunIfDueAsync());
        Assert.Equal("2026-10-25", await scheduler.LastSuccessfulDateAsync());
        Assert.Equal(firstDigest, SHA256.HashData(await File.ReadAllBytesAsync(firstPath)));
        using var copy = new AlTamamDbContext(firstPath, SyntheticFixtures.TestKeyHex);
        Assert.True(copy.HasValidIntegrity());
        using (var connection = _db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM BackupHistory
                WHERE IsAutomatic=1 AND VerificationPassed=1;
                """;
            Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task InvalidPathCannotReplacePersistedPreferenceOrTriggerHiddenFallback()
    {
        var settings = new OutputDirectorySettings(_db);
        Assert.True((await settings.SaveAsync(_folder, Admin, CancellationToken.None)).Success);
        var missing = Path.Combine(_folder, "missing");
        var denied = await settings.SaveAsync(missing, Admin, CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Contains("هذا المسار غير صالح أو غير متاح", denied.UserMessage);
        Assert.Equal(Path.GetFullPath(_folder), await settings.GetAsync());
        Assert.False(Directory.Exists(missing));
        Assert.True(_db.HasValidIntegrity());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_database);
        try { Directory.Delete(_folder, true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
