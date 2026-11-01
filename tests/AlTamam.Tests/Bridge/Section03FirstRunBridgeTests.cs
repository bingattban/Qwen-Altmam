using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// 03.02–03.04 and 03.16: run the REAL first-startup WebView2 bridge route
/// against a fresh encrypted database with no roster entries.
/// </summary>
[Collection("SQLCipherGlobalPool")]
public sealed class Section03FirstRunBridgeTests : IAsyncLifetime, IDisposable
{
    private const string GuestNonce = "00112233445566778899aabbccddeeff";
    private const string StrongPassword = "Abc123!secure";
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _services = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        // Separate these short-lived synthetic databases from process-wide pooled handles.
        // Other restore regression tests legitimately clear all SQLCipher connection pools.
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex,
            useConnectionPooling: false);
        await _db.InitializeAsync();
        _services = new BridgeSharedServices(_db);
        _dispatcher = BridgeHandlerCatalog.Create(_services);
    }

    private async Task<BridgeDispatchResult> SendAsync(string action, JsonObject payload,
        string nonce = GuestNonce)
    {
        var request = new JsonObject
        {
            ["requestId"] = Guid.NewGuid().ToString("N"),
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = action,
            ["sessionNonce"] = nonce,
            ["payload"] = payload
        };
        return await _dispatcher.DispatchAsync(
            request.ToJsonString(), LocalWebUiOrigin.BaseUrl, true,
            sessionNonce => _services.ResolveSessionAsync(sessionNonce));
    }

    private object? Scalar(string sql)
    {
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public async Task FirstRunBridge_ExactlySixCharactersWithoutSpecialSymbol_Works()
    {
        const string minimumPassword = "Abc123";
        var setup = await SendAsync("AuthenticateLocalUser", new JsonObject
        {
            ["username"] = "minimum-founder",
            ["password"] = minimumPassword,
            ["passwordConfirmation"] = minimumPassword,
            ["setupIntent"] = "firstTimeSetup"
        });
        Assert.True(setup.Response.Success, setup.Response.UserMessage);
        Assert.Equal(1L, Convert.ToInt64(Scalar("SELECT COUNT(*) FROM Users WHERE Role=1;")));
        var laterLogin = await SendAsync("AuthenticateLocalUser", new JsonObject
        {
            ["username"] = "minimum-founder",
            ["password"] = minimumPassword
        });
        Assert.True(laterLogin.Response.Success, laterLogin.Response.UserMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyRoster_FirstSetupDoesNotRequirePersonLink_AndFounderNameIsOptional(
        bool includeFullName)
    {
        var before = await SendAsync("GetProvisioningState", new JsonObject());
        Assert.True(before.Response.Success, before.Response.UserMessage);
        Assert.False(Assert.IsType<JsonObject>(before.Response.Data)["provisioned"]!.GetValue<bool>());
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT COUNT(*) FROM Persons;")));

        // No personnel number or PersonId is requested or passed to the bridge.
        var setup = new JsonObject
        {
            ["username"] = "first-owner",
            ["password"] = StrongPassword,
            ["passwordConfirmation"] = StrongPassword,
            ["setupIntent"] = "firstTimeSetup"
        };
        if (includeFullName) setup["fullName"] = "صاحب البرنامج";

        var login = await SendAsync("AuthenticateLocalUser", setup);
        Assert.True(login.Response.Success, login.Response.UserMessage);
        var session = Assert.IsType<JsonObject>(login.Response.Data);
        string nonce = session["sessionNonce"]!.GetValue<string>();
        Assert.True(BridgeProtocol.IsValidSessionNonceFormat(nonce));
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT COUNT(*) FROM Persons;")));
        Assert.Equal(1L, Convert.ToInt64(Scalar("SELECT COUNT(*) FROM Users WHERE Role=1;")));
        Assert.Equal(DBNull.Value, Scalar("SELECT LinkedPersonId FROM Users WHERE Role=1;"));
        object expectedFounderName = includeFullName ? "صاحب البرنامج" : DBNull.Value;
        Assert.Equal(expectedFounderName, Scalar("SELECT FoundingFullName FROM Users WHERE Role=1;"));

        var users = await SendAsync("ListUserAccounts", new JsonObject(), nonce);
        Assert.True(users.Response.Success, users.Response.UserMessage);
        var items = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(users.Response.Data)["items"]);
        Assert.Single(items);
        var founder = Assert.IsType<JsonObject>(items[0]);
        Assert.Null(founder["personId"]);
        Assert.Equal("", founder["serviceNumberA"]!.GetValue<string>());
        Assert.Equal(includeFullName
                ? "الإدارة العامة - first-owner (صاحب البرنامج)"
                : "الإدارة العامة - first-owner",
            founder["fullName"]!.GetValue<string>());

        var after = await SendAsync("GetProvisioningState", new JsonObject());
        Assert.True(Assert.IsType<JsonObject>(after.Response.Data)["provisioned"]!.GetValue<bool>());
        var repeat = await SendAsync("AuthenticateLocalUser", new JsonObject
        {
            ["username"] = "second-owner", ["password"] = StrongPassword,
            ["passwordConfirmation"] = StrongPassword, ["setupIntent"] = "firstTimeSetup",
            ["fullName"] = "محاولة إنشاء مؤسس ثانٍ"
        });
        Assert.False(repeat.Response.Success);
        Assert.Equal(1L, Convert.ToInt64(Scalar("SELECT COUNT(*) FROM Users WHERE Role=1;")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyFounderFullName_IsOptionalAndNeverCreatesRosterIdentity(string fullName)
    {
        var setup = new JsonObject
        {
            ["username"] = "first-owner",
            ["password"] = StrongPassword,
            ["passwordConfirmation"] = StrongPassword,
            ["setupIntent"] = "firstTimeSetup",
            ["fullName"] = fullName
        };
        var response = await SendAsync("AuthenticateLocalUser", setup);
        Assert.True(response.Response.Success, response.Response.UserMessage);
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT COUNT(*) FROM Persons;")));
        Assert.Equal(DBNull.Value, Scalar("SELECT LinkedPersonId FROM Users WHERE Role=1;"));
        Assert.Equal(DBNull.Value, Scalar("SELECT FoundingFullName FROM Users WHERE Role=1;"));
        var nonce = Assert.IsType<JsonObject>(response.Response.Data)["sessionNonce"]!.GetValue<string>();
        var listed = await SendAsync("ListUserAccounts", new JsonObject(), nonce);
        Assert.True(listed.Response.Success);
        var items = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(listed.Response.Data)["items"]);
        Assert.Equal("الإدارة العامة - first-owner",
            items[0]!["fullName"]!.GetValue<string>());
    }

    [Fact]
    public async Task FirstSetup_RejectsMissingConfirmation_WithoutCreatingAccount()
    {
        var attempt = await SendAsync("AuthenticateLocalUser", new JsonObject
        {
            ["username"] = "first-owner", ["password"] = StrongPassword,
            ["setupIntent"] = "firstTimeSetup"
        });
        Assert.False(attempt.Response.Success);
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT COUNT(*) FROM Users;")));
        Assert.False(await _services.IsProvisionedAsync());
    }


    [Fact]
    public async Task FirstRunBridge_UnpooledSqlCipherHandleRemainsValidAfterGlobalPoolClear()
    {
        using var connection = _db.CreateOpenConnection();
        Assert.False(new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling);
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Persons;";
        Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));

        // Offline restore intentionally clears SQLite pools process-wide. The isolated
        // first-run fixture must never borrow a handle from those pools.
        SqliteConnection.ClearAllPools();
        Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
        var provision = await SendAsync("GetProvisioningState", new JsonObject());
        Assert.True(provision.Response.Success, provision.Response.UserMessage);
        Assert.False(Assert.IsType<JsonObject>(provision.Response.Data)["provisioned"]!.GetValue<bool>());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
