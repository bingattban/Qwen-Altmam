using System.Text.Json;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using Xunit;

namespace AlTamam.Tests;

public sealed class R01BridgeSqlCipherDispatchTests
{
    [Fact]
    public async Task ActiveStatuses_AuthenticatedDispatcher_ReturnsRealSqlCipherProjection()
    {
        string directory = Path.Combine(Path.GetTempPath(), "altamam-r01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var db = new AlTamamDbContext(Path.Combine(directory, "fixture.db"),
                Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                useConnectionPooling: false);
            await db.InitializeAsync();
            var services = new BridgeSharedServices(db);
            var dispatcher = BridgeHandlerCatalog.Create(services);
            const string nonce = "0123456789abcdef0123456789abcdef";
            var principal = new BridgePrincipal("synthetic-admin", "r01-test",
                UserRole.GeneralAdministration, nonce);
            string raw = JsonSerializer.Serialize(new
            {
                requestId = "r01-statuses-1",
                protocolVersion = BridgeProtocol.ProtocolVersion,
                action = "GetActiveAttendanceStatuses",
                sessionNonce = nonce,
                payload = new { }
            });
            var result = await dispatcher.DispatchAsync(raw,
                $"https://{LocalWebUiOrigin.Host}/index.html", true,
                supplied => Task.FromResult<BridgePrincipal?>(supplied == nonce ? principal : null));
            Assert.True(result.Response.Success, result.Response.UserMessage);
            var rows = Assert.IsType<JsonArray>(result.Response.Data);
            foreach (var item in rows)
            {
                var status = Assert.IsType<JsonObject>(item);
                Assert.False(string.IsNullOrWhiteSpace(status["statusId"]?.GetValue<string>()));
                Assert.False(string.IsNullOrWhiteSpace(status["displayName"]?.GetValue<string>()));
                Assert.True(status["active"]?.GetValue<bool>());
                Assert.NotNull(status["color"]);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* Windows may still be releasing SQLite handles. */ }
        }
    }

    [Fact]
    public async Task ActiveStatuses_ExpiredSession_IsRejectedBeforeSqlRead()
    {
        string directory = Path.Combine(Path.GetTempPath(), "altamam-r01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var db = new AlTamamDbContext(Path.Combine(directory, "fixture.db"),
                Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                useConnectionPooling: false);
            await db.InitializeAsync();
            var dispatcher = BridgeHandlerCatalog.Create(new BridgeSharedServices(db));
            string raw = JsonSerializer.Serialize(new
            {
                requestId = "r01-statuses-expired",
                protocolVersion = BridgeProtocol.ProtocolVersion,
                action = "GetActiveAttendanceStatuses",
                sessionNonce = "0123456789abcdef0123456789abcdef",
                payload = new { }
            });
            var result = await dispatcher.DispatchAsync(raw,
                $"https://{LocalWebUiOrigin.Host}/index.html", true,
                _ => Task.FromResult<BridgePrincipal?>(null));
            Assert.Equal(BridgeOutcomeKind.Unauthorized, result.Kind);
            Assert.False(result.Response.Success);
            Assert.Equal(BridgeErrorCodes.SessionExpired, result.Response.ErrorCode);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
