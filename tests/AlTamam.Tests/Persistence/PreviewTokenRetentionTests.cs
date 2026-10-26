using AlTamam.Application.Scheduling;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Persistence;

public sealed class PreviewTokenRetentionTests
{
    private sealed class FixedUtcTime(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    [Fact]
    public async Task OnlyUnreferencedTokensExpiredForMoreThanThirtyDaysAreDeleted()
    {
        string path = SyntheticFixtures.CreateTempDatabasePath();
        try
        {
            using var db = new AlTamamDbContext(path, SyntheticFixtures.TestKeyHex);
            await db.InitializeAsync();
            string unused = new('a', 64);
            string usedByAnyOtherTable = new('b', 64);
            string consumedUnused = new('c', 64);
            string recentlyExpired = new('d', 64);
            using (var c = db.CreateOpenConnection())
            {
                Insert(c, unused, "2026-09-01T00:00:00.0000000+00:00", "2026-09-01T00:15:00.0000000+00:00", 0);
                Insert(c, usedByAnyOtherTable, "2026-08-01T00:00:00.0000000+00:00", "2026-08-01T00:15:00.0000000+00:00", 0);
                Insert(c, consumedUnused, "2026-09-01T00:00:00.0000000+00:00", "2026-09-01T00:15:00.0000000+00:00", 1);
                Insert(c, recentlyExpired, "2026-10-25T00:00:00.0000000+00:00", "2026-10-25T00:15:00.0000000+00:00", 0);
                using var evidence = c.CreateCommand();
                evidence.CommandText = """
                    INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                    VALUES('test-saved-reference',$ref,'2026-09-01T00:00:00Z');
                    """;
                evidence.Parameters.AddWithValue("$ref", "important-evidence/" + usedByAnyOtherTable);
                evidence.ExecuteNonQuery();
            }

            var service = new PreviewTokenRetentionService(db,
                new FixedUtcTime(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));
            Assert.Equal(2, await service.RunIfDueAsync());
            Assert.Equal(0, await service.RunIfDueAsync()); // daily checkpoint, no repeat
            using (var c = db.CreateOpenConnection())
            using (var read = c.CreateCommand())
            {
                read.CommandText = "SELECT Token FROM PreviewTokens ORDER BY Token;";
                using var reader = read.ExecuteReader();
                var retained = new List<string>();
                while (reader.Read()) retained.Add(reader.GetString(0));
                Assert.Equal(new[] { usedByAnyOtherTable, recentlyExpired }, retained);
            }
        }
        finally { SyntheticFixtures.DeleteTempDatabase(path); }
    }

    private static void Insert(SqliteConnection c, string token, string issued, string expiry, int consumed)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PreviewTokens(Token,Operation,ActorUserId,PayloadCanonicalJson,BaseRevision,
                SummaryJson,IssuedAtUtc,ExpiresAtUtc,ConsumedFlag)
            VALUES($token,'test','actor','{}',1,'{}',$issued,$expiry,$consumed);
            """;
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$issued", issued);
        cmd.Parameters.AddWithValue("$expiry", expiry);
        cmd.Parameters.AddWithValue("$consumed", consumed);
        cmd.ExecuteNonQuery();
    }
}
