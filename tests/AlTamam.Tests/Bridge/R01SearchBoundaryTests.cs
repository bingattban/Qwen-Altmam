using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

// Real login, session resolution, native dispatcher and encrypted persistence.
// These acceptance tests extend existing lookup coverage without changing semantics.
public sealed class R01SearchBoundaryTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _services = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;
    private string _nonce = "";

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex,
            useConnectionPooling: false);
        await _db.InitializeAsync();
        _services = new BridgeSharedServices(_db);
        var provision = await _services.ProvisionFirstAdministratorAsync(
            "r01-search-admin", "Synthetic123", default);
        Assert.True(provision.Success, provision.UserMessage);
        var (principal, failure) = await _services.AuthenticateAsync(
            "r01-search-admin", "Synthetic123", default);
        Assert.Null(failure);
        Assert.NotNull(principal);
        _nonce = principal!.SessionNonce;
        _dispatcher = BridgeHandlerCatalog.Create(_services);

        using var connection = _db.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();
        // Insert in reverse order with identical names, so PersonId tie-breaking
        // must be provided by the query rather than insertion order.
        for (int i = 204; i >= 0; i--)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                    FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($id,'بحث اختباري','بحث اختباري',$number,$number,
                    '2026-10-01','2026-10-01T00:00:00Z',1,1,7);
                """;
            command.Parameters.AddWithValue("$id", $"R01-SEARCH-{i:D3}");
            command.Parameters.AddWithValue("$number", (980000 + i).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    [Theory]
    [InlineData("SearchPersons")]
    [InlineData("SearchActivePersonsForAccount")]
    public async Task Search_LimitsAndOrdersResultsWithoutChangingPeopleOrAudit(string action)
    {
        string before = ReadEvidence();
        foreach (var (take, expected) in new (int?, int)[]
            { (null, 25), (0, 25), (-1, 25), (1, 1), (30, 30), (200, 200), (999, 200) })
        {
            var payload = new JsonObject { ["term"] = "بحث اختباري" };
            if (take.HasValue) payload["take"] = take.Value;
            JsonObject data = Data(await Call(action, payload));
            var rows = Assert.IsType<JsonArray>(data["items"]);
            Assert.Equal(expected, data["count"]!.GetValue<int>());
            Assert.Equal(Enumerable.Range(0, expected).Select(i => $"R01-SEARCH-{i:D3}"),
                rows.Select(row => row!["personId"]!.GetValue<string>()));
        }
        Assert.Equal(before, ReadEvidence());
    }

    [Theory]
    [InlineData("SearchPersons", "980000")]
    [InlineData("SearchPersons", "٩٨٠٠٠٠")]
    [InlineData("SearchPersons", "۹۸۰۰۰۰")]
    [InlineData("SearchActivePersonsForAccount", "980000")]
    [InlineData("SearchActivePersonsForAccount", "٩٨٠٠٠٠")]
    [InlineData("SearchActivePersonsForAccount", "۹۸۰۰۰۰")]
    public async Task Search_DigitFormsPreserveExactUiProjectionAndPersonId(string action, string term)
    {
        JsonObject data = Data(await Call(action, new JsonObject { ["term"] = term }));
        Assert.Equal(1, data["count"]!.GetValue<int>());
        var row = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(data["items"])));
        var expected = JsonNode.Parse("""
            {"personId":"R01-SEARCH-000","displayName":"بحث اختباري",
             "serviceNumberA":"980000","organizationalStatus":1,"revision":7,"siteName":"غير محدد"}
            """);
        // Compare the serialized native DTO that JavaScript actually receives.
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(row.ToJsonString())));
    }

    [Theory]
    [InlineData("SearchPersons")]
    [InlineData("SearchActivePersonsForAccount")]
    public async Task Search_ValidEmptyResultAndTermValidationAreDistinct(string action)
    {
        var empty = Data(await Call(action, new JsonObject { ["term"] = "لا يطابق" }));
        Assert.Empty(Assert.IsType<JsonArray>(empty["items"]));
        Assert.Equal(0, empty["count"]!.GetValue<int>());
        foreach (var (term, code) in new[] {
            ("", BridgeErrorCodes.MissingData),
            (new string('ا', 101), BridgeErrorCodes.ValidationError),
            ("بحث\n", BridgeErrorCodes.ValidationError),
            ("ـً", BridgeErrorCodes.ValidationError) })
        {
            var denied = await Call(action, new JsonObject { ["term"] = term });
            Assert.False(denied.Response.Success);
            Assert.Equal(code, denied.Response.ErrorCode);
            Assert.Null(denied.Response.Data);
        }
        var boundary = Data(await Call(action,
            new JsonObject { ["term"] = new string('ا', 100) }));
        Assert.Empty(Assert.IsType<JsonArray>(boundary["items"]));
    }

    [Theory]
    [InlineData("SearchPersons")]
    [InlineData("SearchActivePersonsForAccount")]
    public async Task Search_ExpiredPersistedSessionReturnsNoPersonData(string action)
    {
        using (var connection = _db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE UserSessions SET ExpiresAtUtc='2000-01-01T00:00:00Z' WHERE SessionNonce=$nonce;";
            command.Parameters.AddWithValue("$nonce", _nonce);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var result = await Call(action, new JsonObject { ["term"] = "بحث" });
        Assert.Equal(BridgeOutcomeKind.Unauthorized, result.Kind);
        Assert.False(result.Response.Success);
        Assert.Equal(BridgeErrorCodes.SessionExpired, result.Response.ErrorCode);
        Assert.Null(result.Response.Data);
    }

    private Task<BridgeDispatchResult> Call(string action, JsonObject payload)
    {
        var request = new JsonObject {
            ["requestId"] = Guid.NewGuid().ToString("N"),
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = action, ["sessionNonce"] = _nonce, ["payload"] = payload
        };
        return _dispatcher.DispatchAsync(request.ToJsonString(), LocalWebUiOrigin.BaseUrl,
            true, nonce => _services.ResolveSessionAsync(nonce));
    }

    private static JsonObject Data(BridgeDispatchResult result)
    {
        Assert.True(result.Response.Success, result.Response.UserMessage);
        Assert.Equal(BridgeOutcomeKind.Success, result.Kind);
        return Assert.IsType<JsonObject>(result.Response.Data);
    }

    private string ReadEvidence()
    {
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM Persons) || ':' ||
                   (SELECT SUM(RowVersion) FROM Persons) || ':' ||
                   (SELECT COUNT(*) FROM AuditEvents) || ':' ||
                   (SELECT COUNT(*) FROM MutationReceipts);
            """;
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
