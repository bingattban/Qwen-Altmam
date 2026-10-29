using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Theme is the pre-existing singleton AppSettings.Theme; no duplicate client preference.</summary>
public sealed class ThemeSettingsTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal Actor=new(
        "TST-THEME-ACTOR","test-theme-user",UserRole.Supervisor,
        "cccccccccccccccccccccccccccccccc");

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    [Fact]
    public async Task DefaultAndStoredTheme_AreReadFromTheSameCanonicalSetting()
    {
        var service=new SettingsReadBackupService(_db);
        var original=await service.OverviewAsync(Actor,CancellationToken.None);
        Assert.Equal("light",Assert.IsType<JsonObject>(original.Data)["theme"]!.GetValue<string>());
        var set=await service.SetThemeAsync(new JsonObject{["theme"]="dark"},CancellationToken.None);
        Assert.True(set.Success,set.UserMessage);
        Assert.Equal("dark",((JsonObject)set.Data!)["theme"]!.GetValue<string>());
        var reopened=new SettingsReadBackupService(_db);
        var saved=await reopened.OverviewAsync(Actor,CancellationToken.None);
        Assert.Equal("dark",((JsonObject)saved.Data!)["theme"]!.GetValue<string>());
        using var conn=_db.CreateOpenConnection();
        using var q=conn.CreateCommand();
        q.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey='Theme';";
        Assert.Equal("dark",(await q.ExecuteScalarAsync())?.ToString());
        var light=await reopened.SetThemeAsync(new JsonObject{["theme"]="light"},CancellationToken.None);
        Assert.True(light.Success,light.UserMessage);
        var after=await service.OverviewAsync(Actor,CancellationToken.None);
        Assert.Equal("light",((JsonObject)after.Data!)["theme"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("automatic")]
    [InlineData("DARK")]
    [InlineData("day")]
    [InlineData("<script>")]
    public async Task RejectsUnapprovedThemeValuesWithoutTouchingOtherSettings(string? value)
    {
        var service=new SettingsReadBackupService(_db);
        var payload=value is null?new JsonObject():new JsonObject{["theme"]=value};
        var r=await service.SetThemeAsync(payload,CancellationToken.None);
        Assert.False(r.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError,r.ErrorCode);
        var current=await service.OverviewAsync(Actor,CancellationToken.None);
        Assert.Equal("light",((JsonObject)current.Data!)["theme"]!.GetValue<string>());
    }

    [Fact]
    public async Task RejectsWrongJsonTypeAndPreservesDatabaseSchema()
    {
        var result=await new SettingsReadBackupService(_db).SetThemeAsync(
            new JsonObject{["theme"]=17},CancellationToken.None);
        Assert.False(result.Success);
        using var connection=_db.CreateOpenConnection();
        using var query=connection.CreateCommand();
        query.CommandText="SELECT COUNT(*) FROM AppSettings WHERE SettingKey='Theme';";
        Assert.Equal(1L,Convert.ToInt64(await query.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CanonicalBridge_RequiresAuthenticatedRoleAndSavesExactValue()
    {
        var dispatcher=BridgeHandlerCatalog.Create(new BridgeSharedServices(_db));
        async Task<BridgeDispatchResult> Run(string nonce,JsonObject payload)
        {
            var request=new JsonObject {
                ["requestId"]=Guid.NewGuid().ToString("N"),
                ["protocolVersion"]=BridgeProtocol.ProtocolVersion,
                ["action"]="SetTheme",
                ["sessionNonce"]=nonce,["payload"]=payload
            };
            return await dispatcher.DispatchAsync(request.ToJsonString(),
                LocalWebUiOrigin.BaseUrl,true,
                n=>Task.FromResult<BridgePrincipal?>(n==Actor.SessionNonce?Actor:null));
        }
        var denied=await Run("ffffffffffffffffffffffffffffffff",
            new JsonObject{["theme"]="dark",["role"]="GeneralAdministration"});
        Assert.Equal(BridgeOutcomeKind.Unauthorized,denied.Kind);
        var missingId=await Run(Actor.SessionNonce,new JsonObject{["theme"]="dark"});
        Assert.False(missingId.Response.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError,missingId.Response.ErrorCode);
        // This is an authenticated data write, so the caller must supply its
        // independently generated, reusable operation ID.
        var allowed=await Run(Actor.SessionNonce,new JsonObject{
            ["theme"]="dark",["operationId"]="section02-theme-intent-0123456789"});
        Assert.Equal(BridgeOutcomeKind.Success,allowed.Kind);
        Assert.Equal("dark",((JsonObject)allowed.Response.Data!)["theme"]!.GetValue<string>());
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose(){_db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);}
}
