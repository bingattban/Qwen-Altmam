using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Persistence.Migrations;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>
/// Phase 02.09: an existing encrypted database must have a verified, independently
/// reopenable pre-migration rescue image BEFORE any schema migration executes.
/// These run on the same Windows runner as the full release test suite.
/// </summary>
public sealed class PreMigrationEncryptedRescueTests : IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private string RescueFolder => _path + ".migration-backups";

    private async Task CreateLegacyV001Async()
    {
        using var db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        using var connection = db.CreateOpenConnection();
        await new SchemaMigrationRunner([AlTamamDbContext.CanonicalMigrations[0]])
            .UpgradeAsync(connection);
        Assert.Equal(1, SchemaMigrationRunner.GetCurrentVersion(connection));
    }

    [Fact]
    public async Task ExistingDatabase_UpgradePublishesEncryptedRescueOfOriginalSchema()
    {
        await CreateLegacyV001Async();
        using (var db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex))
        {
            await db.InitializeAsync();
            using var current = db.CreateOpenConnection();
            Assert.Equal(AlTamamDbContext.CurrentSchemaVersion,
                SchemaMigrationRunner.GetCurrentVersion(current));
        }

        var backups = Directory.GetFiles(RescueFolder, "pre-migration-v001-to-v009-*.db");
        Assert.Single(backups);
        using var backup = new AlTamamDbContext(backups[0], SyntheticFixtures.TestKeyHex);
        using var older = backup.CreateOpenConnection();
        Assert.Equal(1, SchemaMigrationRunner.GetCurrentVersion(older));
        Assert.True(backup.HasValidIntegrity());

        byte[] original = File.ReadAllBytes(backups[0]);
        Assert.False(original.AsSpan(0, 16).SequenceEqual("SQLite format 3\\0"u8),
            "The rescue image must be encrypted, never plaintext SQLite.");
        using var wrongKey = new AlTamamDbContext(backups[0], new string('A', 64));
        Assert.ThrowsAny<Exception>(() =>
        {
            using var denied = wrongKey.CreateOpenConnection();
            using var command = denied.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM SchemaVersions;";
            command.ExecuteScalar();
        });
    }

    [Fact]
    public async Task UnwritableRescueDestination_BlocksUpgradeWithoutSchemaMutation()
    {
        await CreateLegacyV001Async();
        // An existing ordinary file makes Directory.CreateDirectory fail on
        // any OS. No ACL / admin privilege assumptions in this regression.
        File.WriteAllText(RescueFolder, "not-a-directory");
        try
        {
            using var upgrade = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
            await Assert.ThrowsAnyAsync<IOException>(() => upgrade.InitializeAsync());
        }
        finally { File.Delete(RescueFolder); }

        using var original = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        using var connection = original.CreateOpenConnection();
        Assert.Equal(1, SchemaMigrationRunner.GetCurrentVersion(connection));
        Assert.True(original.HasValidIntegrity());
    }

    [Fact]
    public async Task FreshDatabase_DoesNotCreateRedundantMigrationRescue()
    {
        using var db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await db.InitializeAsync();
        Assert.Equal(AlTamamDbContext.CurrentSchemaVersion,
            SchemaMigrationRunner.GetCurrentVersion(db.CreateOpenConnection()));
        Assert.False(Directory.Exists(RescueFolder));
    }

    public void Dispose()
    {
        if (Directory.Exists(RescueFolder)) Directory.Delete(RescueFolder, recursive: true);
        else if (File.Exists(RescueFolder)) File.Delete(RescueFolder);
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
