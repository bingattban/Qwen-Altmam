using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Staging step toward safe restore: a genuine preview of the selected encrypted
/// backup with current-versus-candidate counts. Never activates or overwrites it.
/// </summary>
public sealed class RestorePreviewReadOnlyIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;
    private string _backup = "";
    private const string GaNonce = "11111111111111111111111111111111";
    private const string OaNonce = "22222222222222222222222222222222";
    private const string SvNonce = "33333333333333333333333333333333";
    private static readonly BridgePrincipal Ga =
        new("TST-RESTORE-GA", "test-restore-ga", UserRole.GeneralAdministration, GaNonce);
    private static readonly BridgePrincipal Oa =
        new("TST-RESTORE-OA", "test-restore-oa", UserRole.OrdinaryAdministration, OaNonce);
    private static readonly BridgePrincipal Sv =
        new("TST-RESTORE-SV", "test-restore-sv", UserRole.Supervisor, SvNonce);

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _backup = Path.Combine(Path.GetDirectoryName(_path)!, "restore-preview.dbbackup");
        // Snapshot before adding the current-day person, so source and current differ.
        await _db.CreateVerifiedEncryptedBackupAsync(_backup);
        using(var connection=_db.CreateOpenConnection())
        using(var command=connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-RESTORE-LIVE-P','الفرد الحالي','الفرد الحالي',
                    '8642','8642','2026-10-28','2026-10-28T00:00:00Z',1,1,1);
                """;
            await command.ExecuteNonQueryAsync();
        }
        _dispatcher = BridgeHandlerCatalog.Create(new BridgeSharedServices(_db));
    }

    private Task<BridgeDispatchResult> Call(string nonce, JsonObject payload)
    {
        var request = new JsonObject
        {
            ["requestId"] = "TST-RESTORE-" + Guid.NewGuid().ToString("N"),
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = "PreviewRestore",
            ["sessionNonce"] = nonce,
            ["payload"] = payload
        };
        return _dispatcher.DispatchAsync(request.ToJsonString(), LocalWebUiOrigin.BaseUrl,
            isMainFrame:true, nonceValue => Task.FromResult<BridgePrincipal?>(nonceValue switch
            {
                GaNonce => Ga,
                OaNonce => Oa,
                SvNonce => Sv,
                _ => null
            }));
    }

    private long CurrentCount()
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM Persons;";
        return Convert.ToInt64(q.ExecuteScalar());
    }

    [Theory]
    [InlineData(GaNonce)]
    [InlineData(OaNonce)]
    public async Task Preview_ReturnsEncryptedBackupAndLiveCounts_WithoutAlteringDatabase(string nonce)
    {
        var original=await _db.InspectEncryptedBackupAsync(_backup);
        var response=await Call(nonce,new JsonObject{["path"]=_backup});
        Assert.Equal(BridgeOutcomeKind.Success,response.Kind);
        var data=Assert.IsType<JsonObject>(response.Response.Data);
        Assert.True(data["verified"]!.GetValue<bool>());
        Assert.True(data["previewOnly"]!.GetValue<bool>());
        Assert.False(data["restoreEnabled"]!.GetValue<bool>());
        Assert.Equal(original.Sha256,data["sha256"]!.GetValue<string>());
        Assert.Equal(0L,data["incoming"]!["people"]!.GetValue<long>());
        Assert.Equal(1L,data["current"]!["people"]!.GetValue<long>());
        Assert.Equal(1L,CurrentCount());
        Assert.Equal(original.Sha256,(await _db.InspectEncryptedBackupAsync(_backup)).Sha256);
    }

    [Fact]
    public async Task SupervisorCannotPreviewRestore_AndCannotReadCounts()
    {
        var forbidden=await Call(SvNonce,new JsonObject{["path"]=_backup});
        Assert.Equal(BridgeOutcomeKind.Forbidden,forbidden.Kind);
        Assert.False(forbidden.Response.Success);
        Assert.Null(forbidden.Response.Data);
        Assert.Equal(1L,CurrentCount());
    }

    [Fact]
    public async Task MissingOrCorruptBackup_FailsClosedAndPreservesExistingData()
    {
        var missing=await Call(GaNonce,new JsonObject{["path"]=
            Path.Combine(Path.GetDirectoryName(_path)!,"not-here.dbbackup")});
        Assert.False(missing.Response.Success);
        var corrupt=Path.Combine(Path.GetDirectoryName(_path)!,"corrupt-preview.dbbackup");
        await File.WriteAllTextAsync(corrupt,"not an encrypted SQLite database");
        var broken=await Call(GaNonce,new JsonObject{["path"]=corrupt});
        Assert.False(broken.Response.Success);
        Assert.Null(broken.Response.Data);
        Assert.Equal(1L,CurrentCount());
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
