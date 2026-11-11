using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

// Deliberately disabled restore must remain fail-closed through the real
// authenticated dispatcher. No actual restore is enabled by these tests.
public sealed class R01DisabledRestoreDispatchTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledRestore_RejectsLiveOrExpiredAdministratorWithoutChangingEvidence(
        bool liveSession)
    {
        string path = SyntheticFixtures.CreateTempDatabasePath();
        try
        {
            using var db = new AlTamamDbContext(path, SyntheticFixtures.TestKeyHex,
                useConnectionPooling: false);
            await db.InitializeAsync();
            var services = new BridgeSharedServices(db);
            var setup = await services.ProvisionFirstAdministratorAsync(
                "r01-restore-admin", "Synthetic123", default);
            Assert.True(setup.Success, setup.UserMessage);
            var (principal, failure) = await services.AuthenticateAsync(
                "r01-restore-admin", "Synthetic123", default);
            Assert.Null(failure);
            Assert.NotNull(principal);
            string nonce = principal!.SessionNonce;
            string backup = Path.Combine(Path.GetDirectoryName(path)!, "disabled-restore.dbbackup");
            await db.CreateVerifiedEncryptedBackupAsync(backup);
            byte[] backupHash = SHA256.HashData(await File.ReadAllBytesAsync(backup));
            string before = Evidence(db);
            if (!liveSession)
            {
                using var connection = db.CreateOpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE UserSessions SET ExpiresAtUtc='2000-01-01T00:00:00Z' WHERE SessionNonce=$nonce;";
                command.Parameters.AddWithValue("$nonce", nonce);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            var request = new JsonObject
            {
                ["requestId"] = Guid.NewGuid().ToString("N"),
                ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
                ["action"] = "RestoreDatabaseAtomically",
                ["sessionNonce"] = nonce,
                ["payload"] = new JsonObject
                {
                    ["operationId"] = Guid.NewGuid().ToString("N"),
                    ["path"] = backup,
                    ["confirm"] = true
                }
            };
            var dispatcher = BridgeHandlerCatalog.Create(services);
            var result = await dispatcher.DispatchAsync(request.ToJsonString(),
                LocalWebUiOrigin.BaseUrl, true, value => services.ResolveSessionAsync(value));
            R01ResponseSchema.AssertMatches(result.Response, "RestoreDatabaseAtomically");
            Assert.False(result.Response.Success);
            Assert.Equal(liveSession ? BridgeErrorCodes.MissingData : BridgeErrorCodes.SessionExpired,
                result.Response.ErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Response.UserMessage));
            if (liveSession)
                Assert.Equal("استخدم قائمة «استعادة نسخة احتياطية بأمان» بحساب الإدارة العامة؛ لم يتم تعديل البيانات.",
                    result.Response.UserMessage);
            Assert.Null(result.Response.Data);
            Assert.Null(result.Response.AuditEventId);
            Assert.True(result.Response.InvalidationEvents is null ||
                result.Response.InvalidationEvents.Count == 0);
            Assert.Equal(before, Evidence(db));
            Assert.Equal(backupHash, SHA256.HashData(await File.ReadAllBytesAsync(backup)));
            Assert.True(db.HasValidIntegrity());
        }
        finally
        {
            SyntheticFixtures.DeleteTempDatabase(path);
        }
    }

    private static string Evidence(AlTamamDbContext db)
    {
        using var connection = db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM Persons) || ':' ||
                   (SELECT COUNT(*) FROM AttendanceRecords) || ':' ||
                   (SELECT COUNT(*) FROM AuditEvents) || ':' ||
                   (SELECT COUNT(*) FROM MutationReceipts) || ':' ||
                   (SELECT COUNT(*) FROM Users);
            """;
        return Assert.IsType<string>(command.ExecuteScalar());
    }
}
