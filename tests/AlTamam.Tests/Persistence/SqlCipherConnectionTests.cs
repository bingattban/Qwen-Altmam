using System.Text;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>
/// Phase 01 step 01.03 — SQLCipher connection behavior, native provider presence,
/// WAL, foreign keys, integrity check, and the negative plaintext-open test that
/// proves encryption-at-rest. Synthetic fixtures only; no real personnel data.
/// </summary>
public sealed class SqlCipherConnectionTests : IDisposable
{
    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();

    [Fact]
    public void CreateOpenConnection_ProvidesNativeSqlCipherProvider()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA cipher_version;";
        var version = cmd.ExecuteScalar()?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(version),
            "SQLCipher native provider must be present (bundle_e_sqlcipher).");
    }

    [Fact]
    public void CreateOpenConnection_EnablesWalAndForeignKeys()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        using var connection = context.CreateOpenConnection();

        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", journal.ExecuteScalar()?.ToString(), ignoreCase: true);
        }
        using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, Convert.ToInt64(fk.ExecuteScalar()));
        }
        using (var timeout = connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout;";
            Assert.True(Convert.ToInt64(timeout.ExecuteScalar()) >= 15000);
        }
    }

    [Fact]
    public async Task InitializeAsync_CreatesSchema_AndIntegrityCheckPasses()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        await context.InitializeAsync();
        Assert.True(context.HasValidIntegrity());

        using var connection = context.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table';";
        Assert.True(Convert.ToInt64(cmd.ExecuteScalar()) >= 26,
            "V001 baseline tables must exist after initialization.");
    }

    [Fact]
    public async Task DatabaseFile_IsNotPlainSqlite_EncryptionAtRestProven()
    {
        // Arrange: fully initialized encrypted database.
        using (var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex))
            await context.InitializeAsync();

        // Negative test: open WITHOUT the key using plain SQLite semantics.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = false
            // deliberately no Password
        };
        using (var unencrypted = new SqliteConnection(builder.ConnectionString))
        {
            await Assert.ThrowsAnyAsync<SqliteException>(async () =>
            {
                await unencrypted.OpenAsync();
                // SQLCipher may defer key validation until the first schema read.
                using var check = unencrypted.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
                await check.ExecuteScalarAsync();
            });
        }

        // And even if a connection object were constructed, the header must not read "SQLite format 3".
        byte[] header = new byte[16];
        await using (var fs = new FileStream(_dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await fs.ReadExactlyAsync(header.AsMemory());
        Assert.NotEqual("SQLite format 3\0"u8.ToArray(), header);
        Assert.DoesNotContain("SQLite format 3", Encoding.ASCII.GetString(header));
    }

    [Fact]
    public async Task WrongKey_CannotReadPlaintextTables()
    {
        using (var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex))
            await context.InitializeAsync();

        string wrongKey = Convert.ToHexString(Enumerable.Repeat((byte)0xA5, 32).ToArray()).ToLowerInvariant();
        using var attacker = new AlTamamDbContext(_dbPath, wrongKey);
        await Assert.ThrowsAnyAsync<SqliteException>(() => Task.Run(() =>
        {
            using var c = attacker.CreateOpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Persons;";
            cmd.ExecuteScalar();
        }));
    }

    [Theory]
    [InlineData("", typeof(ArgumentException))]                       // empty key rejected
    [InlineData("not-hex!!", typeof(ArgumentException))]              // malformed hex rejected
    [InlineData("0011223344556677", typeof(ArgumentException))]       // short (8-byte) key rejected
    public void Constructor_RejectsInvalidKeyMaterial(string keyHex, Type expectedException)
    {
        Assert.Throws(expectedException, () => new AlTamamDbContext(_dbPath, keyHex));
    }

    [Fact]
    public void Constructor_RejectsNullDatabasePath()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new AlTamamDbContext(null!, SyntheticFixtures.TestKeyHex));
        Assert.Equal("databasePath", ex.ParamName);
    }

    public void Dispose() => SyntheticFixtures.DeleteTempDatabase(_dbPath);
}
