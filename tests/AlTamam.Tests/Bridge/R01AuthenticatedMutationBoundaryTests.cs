using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

// R01 4.7 vertical slice: real login -> dispatcher -> service -> SQLCipher.
// This is not a claim of per-action coverage for the entire manifest.
public sealed class R01AuthenticatedMutationBoundaryTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _services = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;
    private BridgePrincipal _admin = null!;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex, useConnectionPooling: false);
        await _db.InitializeAsync();
        _services = new BridgeSharedServices(_db);
        var setup = await _services.ProvisionFirstAdministratorAsync("r01-native-admin", "Synthetic123", default);
        Assert.True(setup.Success, setup.UserMessage);
        var (admin, failure) = await _services.AuthenticateAsync("r01-native-admin", "Synthetic123", default);
        Assert.Null(failure);
        _admin = Assert.IsType<BridgePrincipal>(admin);
        using (var connection = _db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                    FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-R01-SUP','مشرف اختباري','مشرف اختباري','987654','987654',
                    '2026-01-01','2026-01-01T00:00:00Z',1,1,1);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var account = await new UserAccountManagementService(_db).CreateAsync(new JsonObject {
            ["username"]="r01-native-supervisor", ["password"]="Synthetic123",
            ["passwordConfirmation"]="Synthetic123", ["personId"]="TST-R01-SUP", ["role"]="Supervisor"
        }, _admin, default);
        Assert.True(account.Success, account.UserMessage);
        _dispatcher = BridgeHandlerCatalog.Create(_services);
    }

    [Fact]
    public async Task CreateSite_RetryUsesOneDurableReceipt_ChangedIntentIsRejected()
    {
        var payload = new JsonObject {
            ["name"]="موقع اختبار حد التسجيل", ["operationId"]=Guid.NewGuid().ToString("N")
        };
        var first = await Call(_admin, payload);
        Assert.True(first.Response.Success, first.Response.UserMessage);
        var original = Assert.IsType<JsonObject>(first.Response.Data);
        Assert.False(string.IsNullOrWhiteSpace(first.Response.AuditEventId));
        string beforeRetry = Evidence();
        var replay = await Call(_admin, (JsonObject)payload.DeepClone());
        Assert.True(replay.Response.Success, replay.Response.UserMessage);
        Assert.Equal(first.Response.AuditEventId, replay.Response.AuditEventId);
        Assert.True(JsonNode.DeepEquals(original, Assert.IsType<JsonObject>(replay.Response.Data)));
        Assert.Equal(beforeRetry, Evidence());
        payload["name"]="نية مختلفة بنفس معرف العملية";
        var conflict = await Call(_admin, payload);
        Assert.False(conflict.Response.Success);
        Assert.Equal(BridgeErrorCodes.ConflictStale, conflict.Response.ErrorCode);
        Assert.Null(conflict.Response.Data);
        Assert.Null(conflict.Response.AuditEventId);
        Assert.Equal(beforeRetry, Evidence());
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT VisibleName FROM Sites WHERE SiteId=$id;";
        command.Parameters.AddWithValue("$id", original["siteId"]!.GetValue<string>());
        Assert.Equal("موقع اختبار حد التسجيل", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT OperationCode FROM AuditEvents WHERE AuditEventId=$audit;";
        command.Parameters.AddWithValue("$audit", first.Response.AuditEventId!);
        Assert.Equal("SITE_CREATE", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CreateSite_InvalidPayloadSupervisorStaleAndRevokedSessionCannotWrite()
    {
        string before = Evidence();
        var invalid = await Call(_admin, new JsonObject {
            ["name"]=new JsonObject(), ["operationId"]=Guid.NewGuid().ToString("N")
        });
        Assert.False(invalid.Response.Success);
        var stalePayload = Intent();
        stalePayload["expectedRevision"]=1L;
        stalePayload["baseRevision"]=2L;
        var stale = await Call(_admin, stalePayload);
        Assert.Equal(BridgeErrorCodes.ConflictStale, stale.Response.ErrorCode);
        Assert.Equal(before, Evidence());
        // Explicit logout is required before switching accounts. Its legitimate
        // audit event belongs outside the rejected-request evidence snapshot.
        await _services.RevokeSessionAsync(_admin.SessionNonce);
        string afterLogout = Evidence();
        var expired = await Call(_admin, Intent());
        Assert.Equal(BridgeErrorCodes.SessionExpired, expired.Response.ErrorCode);
        Assert.Equal(afterLogout, Evidence());
        var (supervisorPrincipal, loginFailure) = await _services.AuthenticateAsync(
            "r01-native-supervisor", "Synthetic123", default);
        Assert.Null(loginFailure);
        var supervisorActor = Assert.IsType<BridgePrincipal>(supervisorPrincipal);
        string afterSupervisorLogin = Evidence();
        var supervisor = await Call(supervisorActor, Intent());
        Assert.Equal(BridgeOutcomeKind.Forbidden, supervisor.Kind);
        foreach (var result in new[] { invalid, supervisor, stale, expired })
        {
            Assert.False(result.Response.Success);
            Assert.Null(result.Response.Data);
            Assert.Null(result.Response.AuditEventId);
            Assert.True(result.Response.InvalidationEvents is null ||
                result.Response.InvalidationEvents.Count == 0);
        }
        Assert.Equal(afterSupervisorLogin, Evidence());
        Assert.True(_db.HasValidIntegrity());
    }

    private static JsonObject Intent() => new() {
        ["name"]="موقع يجب ألا يحفظ", ["operationId"]=Guid.NewGuid().ToString("N")
    };

    private Task<BridgeDispatchResult> Call(BridgePrincipal actor, JsonObject payload)
        => _dispatcher.DispatchAsync(new JsonObject {
            ["requestId"]=Guid.NewGuid().ToString("N"), ["protocolVersion"]=BridgeProtocol.ProtocolVersion,
            ["action"]="CreateSite", ["sessionNonce"]=actor.SessionNonce, ["payload"]=payload
        }.ToJsonString(), LocalWebUiOrigin.BaseUrl, true, nonce => _services.ResolveSessionAsync(nonce));

    private string Evidence()
    {
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM Sites) || ':' ||
                   (SELECT COUNT(*) FROM SiteNameHistory) || ':' ||
                   (SELECT COUNT(*) FROM AuditEvents) || ':' ||
                   (SELECT COUNT(*) FROM MutationReceipts);
            """;
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() { _db?.Dispose(); SyntheticFixtures.DeleteTempDatabase(_path); }
}
