using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Draft saves are per-user, never a way to overwrite another operator's pending work.</summary>
public sealed class AttendanceDraftOwnershipIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    private static JsonObject Draft(string? draftId = null, params string[] ids)
    {
        var payload = new JsonObject
        {
            ["businessDate"] = "2026-10-23",
            ["targetStatusId"] = "STS-PRESENT",
            ["selectedPersonIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
        };
        if (draftId is not null) payload["draftId"] = draftId;
        return payload;
    }

    [Fact]
    public async Task SaveListRestorePayloadAndDelete_AreScopedToTheOwner()
    {
        var svc = new BridgeSharedServices(_db);
        var save = await svc.SaveDraftAsync("TST-DRAFT-OWNER-A", Draft(null, "TST-P1", "TST-P2"), CancellationToken.None);
        Assert.True(save.Success, save.UserMessage);
        var draftId = Assert.IsType<JsonObject>(save.Data)["draftId"]!.GetValue<string>();

        var listA = await svc.ListDraftsAsync("TST-DRAFT-OWNER-A", CancellationToken.None);
        var ownDraft = Assert.Single(Assert.IsType<JsonArray>(listA.Data));
        var ids = Assert.IsType<JsonArray>(ownDraft!["selectedPersonIds"]);
        Assert.Equal(new[] { "TST-P1", "TST-P2" }, ids.Select(n => n!.GetValue<string>()));
        var listB = await svc.ListDraftsAsync("TST-DRAFT-OWNER-B", CancellationToken.None);
        Assert.Empty(Assert.IsType<JsonArray>(listB.Data));

        var forbidden = await svc.SaveDraftAsync("TST-DRAFT-OWNER-B", Draft(draftId, "TST-ALTERED"), CancellationToken.None);
        Assert.False(forbidden.Success);
        Assert.Equal(BridgeErrorCodes.ConflictStale, forbidden.ErrorCode);
        var untouched = Assert.Single(Assert.IsType<JsonArray>((await svc.ListDraftsAsync("TST-DRAFT-OWNER-A", CancellationToken.None)).Data));
        Assert.Equal(2, Assert.IsType<JsonArray>(untouched!["selectedPersonIds"]).Count);

        var deniedDelete = await svc.DeleteDraftAsync("TST-DRAFT-OWNER-B", new JsonObject { ["draftId"] = draftId }, CancellationToken.None);
        Assert.False(deniedDelete.Success);
        var deleted = await svc.DeleteDraftAsync("TST-DRAFT-OWNER-A", new JsonObject { ["draftId"] = draftId }, CancellationToken.None);
        Assert.True(deleted.Success, deleted.UserMessage);
        Assert.Empty(Assert.IsType<JsonArray>((await svc.ListDraftsAsync("TST-DRAFT-OWNER-A", CancellationToken.None)).Data));
    }

    [Fact]
    public async Task InvalidSelection_DoesNotCreateDraft()
    {
        var svc = new BridgeSharedServices(_db);
        foreach (var invalid in new[] { Draft(), Draft(null, "TST-P1", "TST-P1"), Draft(null, "") })
        {
            var result = await svc.SaveDraftAsync("TST-DRAFT-OWNER-A", invalid, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(BridgeErrorCodes.ValidationError, result.ErrorCode);
        }
        var tooMany = Enumerable.Range(0, 101).Select(i => $"TST-P{i}").ToArray();
        Assert.False((await svc.SaveDraftAsync("TST-DRAFT-OWNER-A", Draft(null, tooMany), CancellationToken.None)).Success);
        Assert.Empty(Assert.IsType<JsonArray>((await svc.ListDraftsAsync("TST-DRAFT-OWNER-A", CancellationToken.None)).Data));
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
