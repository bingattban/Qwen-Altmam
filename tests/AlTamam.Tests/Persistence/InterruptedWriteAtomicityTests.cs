using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>
/// Section 05.06, owning Phase 02 persistence boundary. Synthetic SQLCipher
/// failures before Commit must roll back everything; a later valid retry works.
/// These are real Windows Release/Core database tests, NOT physical power-loss UAT.
/// </summary>
public sealed class InterruptedWriteAtomicityTests : IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private const string Key = "TST-SECTION05-WRITE-FAILURE";

    private static async Task InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, string value)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
            VALUES($key,$value,'2026-10-30T00:00:00Z');
            """;
        insert.Parameters.AddWithValue("$key", Key);
        insert.Parameters.AddWithValue("$value", value);
        await insert.ExecuteNonQueryAsync();
    }

    private static long Count(AlTamamDbContext db)
    {
        using var c = db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AppSettings WHERE SettingKey=$key;";
        cmd.Parameters.AddWithValue("$key", Key);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task InterruptedWrite_RollsBackAndAValidRetryCommitsExactlyOnce()
    {
        using var db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await db.InitializeAsync();

        await Assert.ThrowsAsync<IOException>(() =>
            db.ExecuteInWriteTransactionAsync<int>(async (connection, transaction) =>
            {
                await InsertAsync(connection, transaction, "unfinished");
                throw new IOException("Simulated interrupted write before commit");
            }));
        Assert.Equal(0L, Count(db));
        Assert.True(db.HasValidIntegrity());

        int result = await db.ExecuteInWriteTransactionAsync(async (connection, transaction) =>
        {
            await InsertAsync(connection, transaction, "retried-once");
            return 1;
        });
        Assert.Equal(1, result);
        Assert.Equal(1L, Count(db));
        Assert.True(db.HasValidIntegrity());
    }

    [Fact]
    public async Task ConstraintFailureAfterPartialInsert_RollsBackEntireTransaction()
    {
        using var db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await db.InitializeAsync();
        await Assert.ThrowsAsync<SqliteException>(() =>
            db.ExecuteInWriteTransactionAsync<int>(async (connection, transaction) =>
            {
                await InsertAsync(connection, transaction, "first");
                await InsertAsync(connection, transaction, "duplicate");
                return 1;
            }));
        Assert.Equal(0L, Count(db));
        Assert.True(db.HasValidIntegrity());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
