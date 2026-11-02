using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Real SQLCipher audit provenance for catalog changes; request retries
/// are the SAME immutable event, never an extra audit or created entity.</summary>
public sealed class Section08CatalogAuditReceiptsTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Admin = new(
        "TST-CATALOG-RECEIPT-ACTOR", "catalog-admin",
        UserRole.GeneralAdministration, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    private long Count(string table)
    {
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(q.ExecuteScalar());
    }

    private void AssertStoredReceipt(BridgeResponse response, string operationCode)
    {
        Assert.True(response.Success, response.UserMessage);
        var data = Assert.IsType<JsonObject>(response.Data);
        string auditId = data["auditEventId"]!.GetValue<string>();
        Assert.StartsWith("AUD-", auditId);
        Assert.Equal(auditId, response.AuditEventId);
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT OperationCode FROM AuditEvents WHERE AuditEventId=$id;";
        q.Parameters.AddWithValue("$id", auditId);
        Assert.Equal(operationCode, q.ExecuteScalar()?.ToString());
    }

    [Fact]
    public async Task SiteCreate_RealAuditIdSurvivesSqlCipherIdempotentReplay()
    {
        var svc = new AdminConfigurationService(_db);
        var request = new JsonObject {
            ["name"] = "موقع اختبار إيصال إنشاء موقع",
            ["operationId"] = "SEC08-CATALOG-SITE-CREATE-0001"
        };
        var first = await svc.CreateSiteAsync(request, Admin, default);
        AssertStoredReceipt(first, "SITE_CREATE");
        long before = Count("AuditEvents");
        var replay = await svc.CreateSiteAsync((JsonObject)request.DeepClone(), Admin, default);
        Assert.True(replay.Success, replay.UserMessage);
        Assert.Equal(first.AuditEventId, replay.AuditEventId);
        Assert.Equal(Assert.IsType<JsonObject>(first.Data)["siteId"]!.GetValue<string>(),
            Assert.IsType<JsonObject>(replay.Data)["siteId"]!.GetValue<string>());
        Assert.Equal(before, Count("AuditEvents"));
        Assert.Equal(1L, Count("MutationReceipts"));
    }

    [Fact]
    public async Task StatusCreateAndDeactivate_EachHasOwnRealImmutableAuditId()
    {
        var svc = new AdminConfigurationService(_db);
        var create = new JsonObject {
            ["name"] = "حالة إيصال تدقيق جديدة", ["symbol"] = "QYX",
            ["color"] = "#6699AA",
            ["operationId"] = "SEC08-CATALOG-STATUS-CREATE-0001"
        };
        var first = await svc.CreateStatusAsync(create, Admin, default);
        AssertStoredReceipt(first, "STATUS_CREATE");
        string statusId = Assert.IsType<JsonObject>(first.Data)["statusId"]!.GetValue<string>();
        var repeated = await svc.CreateStatusAsync((JsonObject)create.DeepClone(), Admin, default);
        Assert.Equal(first.AuditEventId, repeated.AuditEventId);
        var deactivate = new JsonObject {
            ["statusId"] = statusId, ["expectedRevision"] = 1L,
            ["operationId"] = "SEC08-CATALOG-STATUS-DEACTIVATE-0001"
        };
        var second = await svc.DeactivateStatusAsync(deactivate, Admin, default);
        AssertStoredReceipt(second, "STATUS_DEACTIVATE");
        Assert.NotEqual(first.AuditEventId, second.AuditEventId);
        var repeatDeactivate = await svc.DeactivateStatusAsync(
            (JsonObject)deactivate.DeepClone(), Admin, default);
        Assert.True(repeatDeactivate.Success, repeatDeactivate.UserMessage);
        Assert.Equal(second.AuditEventId, repeatDeactivate.AuditEventId);
        Assert.Equal(2L, Count("AuditEvents"));
        Assert.Equal(2L, Count("MutationReceipts"));
    }

    [Fact]
    public async Task BridgeSiteCreate_ExposesActualAuditIdNotAUiGeneratedValue()
    {
        var dispatch = BridgeHandlerCatalog.Create(new BridgeSharedServices(_db));
        var request = new JsonObject {
            ["requestId"] = "SEC08-CATALOG-SITE-BRIDGE-1",
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = "CreateSite",
            ["sessionNonce"] = Admin.SessionNonce,
            ["payload"] = new JsonObject {
                ["name"] = "موقع تحقق جسر السجل",
                ["operationId"] = "SEC08-CATALOG-SITE-BRIDGE-0001"
            }
        };
        var actual = await dispatch.DispatchAsync(request.ToJsonString(),
            LocalWebUiOrigin.BaseUrl, true, nonce =>
                Task.FromResult<BridgePrincipal?>(nonce == Admin.SessionNonce ? Admin : null));
        Assert.Equal(BridgeOutcomeKind.Success, actual.Kind);
        AssertStoredReceipt(actual.Response, "SITE_CREATE");
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
