using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Contract-level tests of the actual UI bridge aliases, not a mock dispatcher.
/// Historical correction may find departed identities, but account pickers must
/// never offer inactive/external-transfer identities. Synthetic SQLCipher data only.
/// </summary>
public sealed class SharedRosterLookupIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static readonly BridgePrincipal Actor =
        new("TST-LOOKUP-GA", "test-lookup-ga", UserRole.GeneralAdministration, Nonce);

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Persons (PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES
                ('TST-LOOKUP-A','أحمد النُور','احمد النور','1234','1234',
                 '2026-10-01','2026-10-01T01:00:00Z',1,1,1),
                ('TST-LOOKUP-X','إبراهيم الخارج','ابراهيم الخارج','5678','5678',
                 '2026-10-01','2026-10-01T01:00:00Z',2,0,1),
                ('TST-LOOKUP-P','اسم %_ تجريبي','اسم %_ تجريبي','9012','9012',
                 '2026-10-01','2026-10-01T01:00:00Z',1,1,1);
            INSERT INTO PersonAssignments (AssignmentId,PersonId,SiteId,
                ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES
                ('TST-LOOKUP-ASG-A','TST-LOOKUP-A','SITE-UNSPECIFIED',
                 '2026-10-01',NULL,NULL,'2026-10-01T01:00:00Z'),
                ('TST-LOOKUP-ASG-P','TST-LOOKUP-P','SITE-UNSPECIFIED',
                 '2026-10-01',NULL,NULL,'2026-10-01T01:00:00Z');
            """;
        await cmd.ExecuteNonQueryAsync();
        _dispatcher = BridgeHandlerCatalog.Create(new BridgeSharedServices(_db));
    }

    private async Task<BridgeDispatchResult> Call(string action, JsonObject? payload = null,
        bool authenticated = true)
    {
        var envelope = new JsonObject
        {
            ["requestId"] = "TST-LOOKUP-" + Guid.NewGuid().ToString("N"),
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = action,
            ["sessionNonce"] = Nonce,
            ["payload"] = payload ?? new JsonObject()
        };
        return await _dispatcher.DispatchAsync(envelope.ToJsonString(), LocalWebUiOrigin.BaseUrl,
            isMainFrame: true,
            nonce => Task.FromResult<BridgePrincipal?>(
                authenticated && nonce == Nonce ? Actor : null));
    }

    private static JsonArray Items(BridgeDispatchResult answer)
    {
        Assert.Equal(BridgeOutcomeKind.Success, answer.Kind);
        Assert.True(answer.Response.Success, answer.Response.UserMessage);
        return Assert.IsType<JsonArray>(
            Assert.IsType<JsonObject>(answer.Response.Data)["items"]);
    }

    [Fact]
    public async Task StatusAliases_ReturnSameActiveStatusRowsForAttendanceAndSettings()
    {
        var direct = await Call("GetActiveAttendanceStatuses");
        var config = await Call("QueryAttendanceStatuses");
        Assert.Equal(BridgeOutcomeKind.Success, direct.Kind);
        Assert.Equal(BridgeOutcomeKind.Success, config.Kind);
        var statuses = Assert.IsType<JsonArray>(direct.Response.Data);
        Assert.NotEmpty(statuses);
        Assert.Equal(statuses.ToJsonString(), Assert.IsType<JsonArray>(config.Response.Data).ToJsonString());
        Assert.All(statuses, node =>
        {
            Assert.False(string.IsNullOrWhiteSpace(node?["statusId"]?.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(node?["displayName"]?.GetValue<string>()));
            Assert.True(node?["active"]?.GetValue<bool>());
        });
    }

    [Fact]
    public async Task AccountPicker_FiltersExternalTransfersAtSqlBoundary_WhileHistoricalLookupRetainsIdentity()
    {
        var accountMatches = Items(await Call("SearchActivePersonsForAccount", new JsonObject
        {
            ["term"] = "إبراهيم", ["take"] = 50
        }));
        Assert.Empty(accountMatches);

        var historicalMatches = Items(await Call("SearchPersons", new JsonObject
        {
            ["term"] = "إبراهيم", ["take"] = 50
        }));
        Assert.Single(historicalMatches);
        Assert.Equal("TST-LOOKUP-X", historicalMatches[0]!["personId"]!.GetValue<string>());
    }

    [Fact]
    public async Task SharedSearch_NormalizesArabicNameAndArabicIndicDigits()
    {
        var byName = Items(await Call("SearchActivePersonsForAccount", new JsonObject
        {
            ["term"] = "احمد", ["take"] = 30
        }));
        Assert.Single(byName);
        Assert.Equal("TST-LOOKUP-A", byName[0]!["personId"]!.GetValue<string>());

        var byNumber = Items(await Call("SearchPersons", new JsonObject
        {
            ["term"] = "١٢٣٤", ["take"] = 30
        }));
        Assert.Single(byNumber);
        Assert.Equal("TST-LOOKUP-A", byNumber[0]!["personId"]!.GetValue<string>());
    }

    [Fact]
    public async Task SearchTreatsSqlWildcardsAsLiteralCharactersAndRejectsEmptyNormalizedTerm()
    {
        var literal = Items(await Call("SearchPersons", new JsonObject
        {
            ["term"] = "%_", ["take"] = 50
        }));
        Assert.Single(literal);
        Assert.Equal("TST-LOOKUP-P", literal[0]!["personId"]!.GetValue<string>());

        var untrusted = await Call("SearchPersons", new JsonObject { ["term"] = "ـً" });
        Assert.False(untrusted.Response.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError, untrusted.Response.ErrorCode);
    }

    [Fact]
    public async Task QueryActivePersonsBySite_CannotBeCoercedIntoListingDepartedIdentities()
    {
        var answer = await Call("QueryActivePersonsBySite", new JsonObject
        {
            ["siteId"] = "SITE-UNSPECIFIED",
            ["includeArchived"] = true,
            ["organizationalStatus"] = 2,
            ["page"] = 1,
            ["pageSize"] = 10
        });
        var rows = Items(answer);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, answer.Response.Data!["total"]!.GetValue<long>());
        Assert.DoesNotContain(rows, x => x!["personId"]!.GetValue<string>() == "TST-LOOKUP-X");
        Assert.All(rows, x =>
        {
            Assert.Equal(1, x!["organizationalStatus"]!.GetValue<int>());
            Assert.True(x["active"]!.GetValue<bool>());
        });

        var foreign = Items(await Call("QueryActivePersonsBySite", new JsonObject
        {
            ["siteId"] = "SITE-NOT-EXIST", ["includeArchived"] = true
        }));
        Assert.Empty(foreign);
    }

    [Fact]
    public async Task QueryUnprepared_IgnoresForgedStatusFilterAndRequiresValidBusinessDate()
    {
        var answer = await Call("QueryUnprepared", new JsonObject
        {
            ["businessDate"] = "2026-10-02",
            ["statusId"] = "__TRANSFER_DAY__",
            ["siteId"] = "SITE-UNSPECIFIED",
            ["pageSize"] = 20
        });
        var rows = Items(answer);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, answer.Response.Data!["total"]!.GetValue<long>());
        Assert.DoesNotContain(rows, x => x!["personId"]!.GetValue<string>() == "TST-LOOKUP-X");
        Assert.All(rows, x => Assert.Equal("غير محضر", x!["statusName"]!.GetValue<string>()));

        var invalid = await Call("QueryUnprepared", new JsonObject
        {
            ["businessDate"] = "2026-02-30", ["statusId"] = ""
        });
        Assert.False(invalid.Response.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError, invalid.Response.ErrorCode);
    }

    [Fact]
    public async Task SearchAndStatusCalls_RequireAuthenticatedSession()
    {
        foreach (var action in new[] { "GetActiveAttendanceStatuses", "SearchPersons",
            "SearchActivePersonsForAccount", "QueryActivePersonsBySite", "QueryUnprepared" })
        {
            var answer = await Call(action, new JsonObject { ["term"] = "احمد" },
                authenticated: false);
            Assert.Equal(BridgeOutcomeKind.Unauthorized, answer.Kind);
            Assert.Equal(BridgeErrorCodes.SessionExpired, answer.Response.ErrorCode);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
