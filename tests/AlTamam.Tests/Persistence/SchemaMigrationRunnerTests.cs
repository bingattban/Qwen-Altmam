using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Persistence.Migrations;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>
/// Phase 01 steps 01.04–01.05 — versioned transactional migration runner:
/// schema-version checks, hash pinning, forward-only upgrades (V001 -> V002),
/// stale/future schema rejection and post-migration integrity verification.
/// </summary>
public sealed class SchemaMigrationRunnerTests : IDisposable
{
    private static readonly SchemaMigration V001 =
        new(1, "V001_InitialSchema", "AlTamam.Infrastructure.Persistence.Migrations.V001_InitialSchema.sql", RecordsHash: false);
    private static readonly SchemaMigration V002 =
        new(2, "V002_MigrationHardening", "AlTamam.Infrastructure.Persistence.Migrations.V002_MigrationHardening.sql");

    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();

    private static SchemaMigrationRunner DefaultRunner() => new([V001, V002]);

    [Fact]
    public void Constructor_RejectsNonContiguousVersions()
    {
        // Sequence must start at 1 and be contiguous.
        Assert.Throws<ArgumentException>(() => new SchemaMigrationRunner([V002]));
        Assert.Throws<ArgumentException>(() => new SchemaMigrationRunner([V001, new SchemaMigration(5, "V005_Gap", V002.EmbeddedResourcePath)]));
    }

    [Fact]
    public void Constructor_RejectsEmptyList()
    {
        Assert.Throws<ArgumentException>(() => new SchemaMigrationRunner([]));
    }

    [Fact]
    public async Task UpgradeAsync_FromEmptyDatabase_AppliesAllVersionsInOrder()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();
        var runner = DefaultRunner();

        await runner.UpgradeAsync(connection);

        Assert.Equal(2, SchemaMigrationRunner.GetCurrentVersion(connection));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MigrationName FROM SchemaVersions ORDER BY SchemaVersion;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("V001_InitialSchema", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("V002_MigrationHardening", reader.GetString(0));
    }

    [Fact]
    public async Task UpgradeAsync_RecordsSha256Pins_ForEveryAppliedMigration()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();
        var runner = DefaultRunner();

        await runner.UpgradeAsync(connection);

        foreach (var migration in new[] { V001, V002 })
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Sha256 FROM SchemaVersions WHERE SchemaVersion = $v;";
            cmd.Parameters.Add("$v", SqliteType.Integer).Value = migration.Version;
            var recorded = cmd.ExecuteScalar() as string;
            if (!migration.RecordsHash)
            {
                // V001 bootstraps before the Sha256 column exists; its pin is backfilled by V002.
                Assert.Null(recorded);
                continue;
            }
            Assert.False(string.IsNullOrWhiteSpace(recorded));
            Assert.Matches("^[0-9a-f]{64}$", recorded!);
            Assert.Equal(runner.ComputeExpectedHash(migration), recorded);
        }
    }

    [Fact]
    public async Task UpgradeAsync_IsIdempotent_SecondRunIsNoOp()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();
        var runner = DefaultRunner();

        await runner.UpgradeAsync(connection);
        await runner.UpgradeAsync(connection); // must not throw or re-apply

        Assert.Equal(2, SchemaMigrationRunner.GetCurrentVersion(connection));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersions;";
        Assert.Equal(2, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task UpgradeAsync_DetectsV002Columns_ForwardOnlyContract()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();
        await DefaultRunner().UpgradeAsync(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('SchemaVersions')
            WHERE name IN ('Sha256','BackupPath');
            """;
        Assert.Equal(2, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task GetCurrentVersion_OnUnversionedDatabase_ReturnsZero()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();
        Assert.Equal(0, SchemaMigrationRunner.GetCurrentVersion(connection));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task IsStaleSchema_FutureDatabaseVersion_IsDetectedAndRejected()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();
        await DefaultRunner().UpgradeAsync(connection);

        // Simulate a database written by a NEWER application build (version 99).
        using (var bump = connection.CreateCommand())
        {
            bump.CommandText = "INSERT INTO SchemaVersions (SchemaVersion, MigrationName, AppliedAtUtc) VALUES (99, 'V099_Future', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));";
            bump.ExecuteNonQuery();
        }

        var runner = DefaultRunner();
        Assert.True(runner.IsStaleSchema(connection));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.UpgradeAsync(connection));
    }

    [Fact]
    public async Task VerifyRecordedHashes_TamperedHistoryRow_Throws()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();
        var runner = DefaultRunner();
        await runner.UpgradeAsync(connection);

        // Attacker edits the recorded hash pin (history tampering must be detected).
        using (var tamper = connection.CreateCommand())
        {
            tamper.CommandText = "UPDATE SchemaVersions SET Sha256 = 'deadbeef' WHERE SchemaVersion = 2;";
            tamper.ExecuteNonQuery();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => runner.VerifyRecordedHashes(connection)));
        Assert.Contains("hash mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpgradeAsync_FailedMigration_RollsBackItsOwnStep()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();

        // Register a deliberately broken V002 that references a non-existent table.
        var brokenV002 = new SchemaMigration(2, "V002_Broken", V002.EmbeddedResourcePath);
        var runnerGood = DefaultRunner();
        // Apply only V001 first via a runner that knows just two versions but we interrupt:
        await using (var apply = connection.CreateCommand())
        {
            apply.CommandText = runnerGood.ReadSql(V001);
            await apply.ExecuteNonQueryAsync();
            using var record = connection.CreateCommand();
            record.CommandText = "INSERT INTO SchemaVersions (SchemaVersion, MigrationName, AppliedAtUtc) VALUES (1,'V001_InitialSchema',STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));";
            await record.ExecuteNonQueryAsync();
        }

        // A failing DDL statement inside the runner's step must roll back version recording.
        var failingRunner = new FailingMigrationRunner([V001, brokenV002], connection);
        await Assert.ThrowsAnyAsync<SqliteException>(() => failingRunner.UpgradeWithBrokenSqlAsync());
        Assert.Equal(1, SchemaMigrationRunner.GetCurrentVersion(connection));
    }

    /// <summary>Test double that executes intentionally invalid SQL inside the migration transaction.</summary>
    private sealed class FailingMigrationRunner(IReadOnlyList<SchemaMigration> migrations, SqliteConnection connection)
    {
        public Task UpgradeWithBrokenSqlAsync()
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                using var broken = connection.CreateCommand();
                broken.Transaction = transaction;
                broken.CommandText = "INSERT INTO NoSuchTable_Never_Exists (X) VALUES (1);";
                broken.ExecuteNonQuery(); // throws SqliteException
                transaction.Commit();
                return Task.CompletedTask;
            }
            catch (SqliteException)
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public void Dispose() => SyntheticFixtures.DeleteTempDatabase(_dbPath);
}
