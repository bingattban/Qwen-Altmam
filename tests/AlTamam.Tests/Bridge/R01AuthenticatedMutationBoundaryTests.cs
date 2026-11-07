using System.Text.Json;
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
        // Supervisors may create sites under the current catalog policy.
        // Account creation is the genuinely forbidden write for this role.
        var supervisor = await Call(supervisorActor, Intent(), "CreateUserAccount");
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


    [Fact]
    public async Task NativeReadWire_MatchesStatusArraySearchItemsAndExpiredErrorContract()
    {
        // DesktopBridgeGateway uses JsonSerializerDefaults.Web for responses.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var statuses = await Call(_admin, new JsonObject(), "GetActiveAttendanceStatuses");
        Assert.True(statuses.Response.Success, statuses.Response.UserMessage);
        using var statusDocument = JsonDocument.Parse(JsonSerializer.Serialize(statuses.Response, options));
        var statusRoot = statusDocument.RootElement;
        Assert.Equal(statuses.Response.RequestId, statusRoot.GetProperty("requestId").GetString());
        Assert.Equal("OK", statusRoot.GetProperty("code").GetString());
        var rows = statusRoot.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.NotEmpty(rows.EnumerateArray());
        var present = Assert.Single(rows.EnumerateArray(),
            row => row.GetProperty("statusId").GetString() == "STS-PRESENT");
        Assert.False(string.IsNullOrWhiteSpace(present.GetProperty("displayName").GetString()));
        Assert.Equal(JsonValueKind.String, present.GetProperty("color").ValueKind);
        Assert.True(present.GetProperty("active").GetBoolean());
        Assert.True(present.GetProperty("version").GetInt64() >= 1);
        Assert.Equal(0L, present.GetProperty("todayCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, statusRoot.GetProperty("auditEventId").ValueKind);

        var search = await Call(_admin, new JsonObject {
            ["term"]="۹۸۷۶۵۴", ["take"]=30
        }, "SearchPersons");
        Assert.True(search.Response.Success, search.Response.UserMessage);
        using var searchDocument = JsonDocument.Parse(JsonSerializer.Serialize(search.Response, options));
        var data = searchDocument.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        var person = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("TST-R01-SUP", person.GetProperty("personId").GetString());
        Assert.Equal("مشرف اختباري", person.GetProperty("displayName").GetString());
        Assert.Equal("987654", person.GetProperty("serviceNumberA").GetString());
        Assert.Equal(1, person.GetProperty("organizationalStatus").GetInt32());
        Assert.Equal(1L, person.GetProperty("revision").GetInt64());
        Assert.Equal("غير محدد", person.GetProperty("siteName").GetString());

        await _services.RevokeSessionAsync(_admin.SessionNonce);
        string before = Evidence();
        var denied = await Call(_admin, new JsonObject { ["term"]="987654" }, "SearchPersons");
        using var errorDocument = JsonDocument.Parse(JsonSerializer.Serialize(denied.Response, options));
        var error = errorDocument.RootElement;
        Assert.False(error.GetProperty("success").GetBoolean());
        Assert.Equal("SessionExpired", error.GetProperty("code").GetString());
        Assert.Equal(error.GetProperty("code").GetString(), error.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("messageAr").GetString()));
        Assert.Equal(error.GetProperty("messageAr").GetString(), error.GetProperty("userMessage").GetString());
        Assert.False(error.TryGetProperty("data", out _));
        Assert.Equal(JsonValueKind.Null, error.GetProperty("auditEventId").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("invalidationEvents").ValueKind);
        Assert.Equal(before, Evidence());
    }

    [Fact]
    public async Task RealLogin_ReadContractsPreserveBusinessStateAndServerRolePermissions()
    {
        async Task<JsonObject> ReadObject(BridgePrincipal actor, string action, JsonObject? payload = null)
        {
            var reply = await Call(actor, payload ?? new JsonObject(), action);
            Assert.True(reply.Response.Success, action + ": " + reply.Response.UserMessage);
            Assert.Null(reply.Response.AuditEventId);
            Assert.True(reply.Response.InvalidationEvents is null || reply.Response.InvalidationEvents.Count == 0);
            return Assert.IsType<JsonObject>(reply.Response.Data);
        }
        async Task CheckRole(BridgePrincipal actor)
        {
            string before = Evidence();
            var session = await ReadObject(actor, "GetCurrentSession");
            Assert.Equal(actor.UserId, session["userId"]!.GetValue<string>());
            Assert.Equal(actor.Role.ToString(), session["role"]!.GetValue<string>());
            var navigation = await ReadObject(actor, "GetNavigationPermissions");
            Assert.Equal(actor.Role.ToString(), navigation["role"]!.GetValue<string>());
            Assert.Equal(ActionManifest.All.Where(x => x.AllowedRoles.Contains(actor.Role.ToString(), StringComparer.Ordinal))
                .Select(x => x.Action).OrderBy(x => x, StringComparer.Ordinal),
                Assert.IsType<JsonArray>(navigation["allowedActions"]).Select(x => x!.GetValue<string>())
                    .OrderBy(x => x, StringComparer.Ordinal));
            var settings = await ReadObject(actor, "GetSettingsOverview");
            Assert.Equal(actor.Username, settings["username"]!.GetValue<string>());
            Assert.Equal(actor.Role.ToString(), settings["role"]!.GetValue<string>());
            Assert.Equal("light", settings["theme"]!.GetValue<string>());
            Assert.Equal("16:00", settings["backupTime"]!.GetValue<string>());
            var policy = await ReadObject(actor, "GetAutoAttendancePolicy");
            Assert.Equal("08:00", policy["dueTimeLocal"]!.GetValue<string>());
            Assert.Equal("20:00", policy["dailyLockTimeLocal"]!.GetValue<string>());
            var schedule = await ReadObject(actor, "GetBackupSchedule");
            Assert.Equal("16:00", schedule["backupTime"]!.GetValue<string>());
            var version = await ReadObject(actor, "GetApplicationVersion");
            Assert.Equal("التمام اليومي", version["productNameAr"]!.GetValue<string>());
            Assert.True(version["offline"]!.GetValue<bool>());
            Assert.Equal(BridgeProtocol.ProtocolVersion, version["bridgeProtocol"]!.GetValue<int>());
            var dashboard = await ReadObject(actor, "GetDashboardSummary", new JsonObject {
                ["businessDate"] = TimeProvider.System.GetLocalNow().ToString("yyyy-MM-dd")
            });
            Assert.Equal(1L, dashboard["totalForce"]!.GetValue<long>());
            Assert.Equal(1L, dashboard["unprepared"]!.GetValue<long>());
            Assert.Equal(0L, dashboard["internalTransferDay"]!.GetValue<long>());
            var statuses = await Call(actor, new JsonObject(), "QueryAttendanceStatuses");
            Assert.True(statuses.Response.Success, statuses.Response.UserMessage);
            Assert.Equal(4, Assert.IsType<JsonArray>(statuses.Response.Data).Count);
            var search = await Call(actor, new JsonObject { ["term"] = "۹۸۷۶۵۴", ["take"] = 10 }, "SearchPersons");
            Assert.True(search.Response.Success, search.Response.UserMessage);
            var data = Assert.IsType<JsonObject>(search.Response.Data);
            Assert.Equal(1, data["count"]!.GetValue<int>());
            Assert.Equal("TST-R01-SUP", Assert.Single(Assert.IsType<JsonArray>(data["items"]))!["personId"]!.GetValue<string>());
            Assert.Equal(before, Evidence());
        }
        await CheckRole(_admin);
        var accountSearch = await Call(_admin, new JsonObject { ["term"] = "987654" }, "SearchActivePersonsForAccount");
        Assert.True(accountSearch.Response.Success, accountSearch.Response.UserMessage);
        Assert.Equal(1, Assert.IsType<JsonObject>(accountSearch.Response.Data)["count"]!.GetValue<int>());
        await _services.RevokeSessionAsync(_admin.SessionNonce);
        var (principal, failure) = await _services.AuthenticateAsync("r01-native-supervisor", "Synthetic123", default);
        Assert.Null(failure);
        var supervisor = Assert.IsType<BridgePrincipal>(principal);
        await CheckRole(supervisor);
        string afterLogin = Evidence();
        var forbidden = await Call(supervisor, new JsonObject { ["term"] = "987654" }, "SearchActivePersonsForAccount");
        Assert.Equal(BridgeErrorCodes.Unauthorized, forbidden.Response.ErrorCode);
        Assert.Equal(afterLogin, Evidence());
    }

    [Fact]
    public async Task NativeSchema_GuardRejectsArrayObjectDriftAndInvalidSearchMetadata()
    {
        var statuses = await Call(_admin, new JsonObject(), "GetActiveAttendanceStatuses");
        Assert.True(statuses.Response.Success, statuses.Response.UserMessage);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => R01ResponseSchema.AssertMatches(
            statuses.Response with { Data = new JsonObject() }, "GetActiveAttendanceStatuses"));
        var search = await Call(_admin, new JsonObject { ["term"] = "987654" }, "SearchPersons");
        Assert.True(search.Response.Success, search.Response.UserMessage);
        var malformed = (JsonObject)Assert.IsType<JsonObject>(search.Response.Data).DeepClone();
        malformed["count"] = "1";
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => R01ResponseSchema.AssertMatches(
            search.Response with { Data = malformed }, "SearchPersons"));
        var omitted = (JsonObject)Assert.IsType<JsonObject>(search.Response.Data).DeepClone();
        Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(omitted["items"])[0]).Remove("personId");
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => R01ResponseSchema.AssertMatches(
            search.Response with { Data = omitted }, "SearchPersons"));
    }

    private static JsonObject Intent() => new() {
        ["name"]="موقع يجب ألا يحفظ", ["operationId"]=Guid.NewGuid().ToString("N")
    };

    private async Task<BridgeDispatchResult> Call(BridgePrincipal actor, JsonObject payload,
        string action = "CreateSite")
    {
        var result = await _dispatcher.DispatchAsync(new JsonObject {
            ["requestId"]=Guid.NewGuid().ToString("N"), ["protocolVersion"]=BridgeProtocol.ProtocolVersion,
            ["action"]=action, ["sessionNonce"]=actor.SessionNonce, ["payload"]=payload.DeepClone()
        }.ToJsonString(), LocalWebUiOrigin.BaseUrl, true, nonce => _services.ResolveSessionAsync(nonce));
        R01ResponseSchema.AssertMatches(result.Response, action);
        return result;
    }

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
