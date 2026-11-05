using System.Text.Json;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

// R01 4.2: exercise the real dispatcher and encrypted persistence with exact
// synthetic projections, including an initially empty active catalogue.
public sealed class R01SeededStatusContractTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex,
            useConnectionPooling: false);
        await _db.InitializeAsync();
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AttendanceStatusDefinitions SET ActiveFlag=0;
            INSERT INTO AttendanceStatusDefinitions
                (StatusId,VisibleName,NormalizedName,ShortSymbol,ChosenColorHex,
                 ActiveFlag,SortOrder,CreatedAtUtc,RowVersion)
            VALUES
                ('R01-B','ثاني','ثاني','ب','#223344',1,2,'2026-01-01T00:00:00Z',7),
                ('R01-A','أول','اول','أ','#112233',1,1,'2026-01-01T00:00:00Z',3),
                ('R01-C','ثالث','ثالث','ج','#334455',1,2,'2026-01-01T00:00:00Z',9),
                ('R01-HIDDEN','معطل','معطل','م','#445566',0,0,'2026-01-01T00:00:00Z',1);
            """;
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("GetActiveAttendanceStatuses")]
    [InlineData("QueryAttendanceStatuses")]
    public async Task StatusRead_ReturnsExactActiveProjectionInStableOrder(string action)
    {
        JsonArray rows = await ReadAsync(action);
        Assert.Equal(new[] { "R01-A", "R01-B", "R01-C" },
            rows.Select(row => row!["statusId"]!.GetValue<string>()).ToArray());
        Assert.Equal(
            new JsonObject {
                ["statusId"]="R01-A", ["displayName"]="أول", ["shortSymbol"]="أ",
                ["color"]="#112233", ["active"]=true, ["version"]=3L, ["todayCount"]=0L
            }.ToJsonString(), rows[0]!.ToJsonString());
        Assert.Equal("ثاني", rows[1]!["displayName"]!.GetValue<string>());
        Assert.Equal(7L, rows[1]!["version"]!.GetValue<long>());
    }

    [Fact]
    public async Task StatusRead_DeactivatedRowsDisappear_AndEmptyCatalogueIsSuccessful()
    {
        Assert.Equal(3, (await ReadAsync("GetActiveAttendanceStatuses")).Count);
        using (var connection = _db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE AttendanceStatusDefinitions SET ActiveFlag=0 WHERE StatusId='R01-B';";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(new[] { "R01-A", "R01-C" },
            (await ReadAsync("GetActiveAttendanceStatuses"))
                .Select(row => row!["statusId"]!.GetValue<string>()).ToArray());
        using (var connection = _db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE AttendanceStatusDefinitions SET ActiveFlag=0;";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Empty(await ReadAsync("GetActiveAttendanceStatuses"));
    }

    private async Task<JsonArray> ReadAsync(string action)
    {
        var dispatcher = BridgeHandlerCatalog.Create(new BridgeSharedServices(_db));
        var principal = new BridgePrincipal("r01-synthetic", "r01",
            UserRole.GeneralAdministration, Nonce);
        string raw = JsonSerializer.Serialize(new {
            requestId=Guid.NewGuid().ToString("N"),
            protocolVersion=BridgeProtocol.ProtocolVersion, action,
            sessionNonce=Nonce, payload=new { }
        });
        var result = await dispatcher.DispatchAsync(raw,
            $"https://{LocalWebUiOrigin.Host}/index.html", true,
            supplied => Task.FromResult<BridgePrincipal?>(supplied == Nonce ? principal : null));
        Assert.True(result.Response.Success, result.Response.UserMessage);
        return Assert.IsType<JsonArray>(result.Response.Data);
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
