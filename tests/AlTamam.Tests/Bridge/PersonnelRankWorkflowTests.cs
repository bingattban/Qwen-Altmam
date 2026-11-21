using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Regression for the mandatory rank between service number and person name.
/// Uses a real encrypted database; values remain keyed to immutable PersonId.
/// </summary>
public sealed class PersonnelRankWorkflowTests : IAsyncLifetime, IDisposable
{
    private const string RankFieldId = "FLD-SYS-RANK";
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new(
        "TST-RANK-ACTOR", "rank-editor", UserRole.GeneralAdministration,
        "d1e229dfab294590b451b2f1c4430f8e");

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    [Fact]
    public async Task Rank_IsProtectedSystemColumn_AndWritesAreAuditedAndReplaySafe()
    {
        using (var connection = _db.CreateOpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT FieldDefinitionId, DisplayOrder, IsSystemProtected, ActiveFlag
                FROM CustomFieldDefinitions WHERE TechnicalKey='sys_rank';
                """;
            using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "V017 must create the protected rank field.");
            Assert.Equal(RankFieldId, reader.GetString(0));
            Assert.Equal(3, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal(1, reader.GetInt32(3));
        }

        var roster = new PersonRosterService(_db);
        var created = await roster.CreatePersonAsync(Actor.UserId, Actor.Username,
            new JsonObject {
                ["visibleName"] = "فرد رتبة تجريبي",
                ["serviceNumberA"] = "08994567",
                ["siteId"] = "SITE-UNSPECIFIED"
            }, default);
        Assert.True(created.Success, created.UserMessage);
        string personId = ((JsonObject)created.Data!)["personId"]!.GetValue<string>();

        var before = await roster.QueryPersonsAsync(new JsonObject {
            ["page"] = 1, ["pageSize"] = 30
        }, default);
        Assert.True(before.Success, before.UserMessage);
        var beforeEntry = Assert.Single(((JsonArray)((JsonObject)before.Data!)["items"]!)
            .OfType<JsonObject>(), p => p["personId"]!.GetValue<string>() == personId);
        Assert.Equal("", beforeEntry["rank"]!.GetValue<string>());

        var editing = new RosterCustomFieldValueService(_db);
        var request = new JsonObject {
            ["personId"] = personId,
            ["expectedRevision"] = 1L,
            ["values"] = new JsonObject { [RankFieldId] = "ملازم أول" },
            ["operationId"] = "TST-RANK-UPDATE-" + Guid.NewGuid().ToString("N")
        };
        var update = await editing.UpdateAsync(request, Actor, default);
        Assert.True(update.Success, update.UserMessage);
        var replay = await editing.UpdateAsync(request, Actor, default);
        Assert.True(replay.Success, replay.UserMessage);
        var after = await roster.QueryPersonsAsync(new JsonObject {
            ["page"] = 1, ["pageSize"] = 30
        }, default);
        var entry = Assert.Single(((JsonArray)((JsonObject)after.Data!)["items"]!)
            .OfType<JsonObject>(), p => p["personId"]!.GetValue<string>() == personId);
        Assert.Equal("ملازم أول", entry["rank"]!.GetValue<string>());
        Assert.Equal(2L, entry["revision"]!.GetValue<long>());

        using var auditDb = _db.CreateOpenConnection();
        using var audit = auditDb.CreateCommand();
        audit.CommandText = """
            SELECT COUNT(*) FROM AuditEvents
            WHERE OperationCode='CUSTOM_FIELD_VALUE_UPDATE' AND TargetEntityId=$person;
            """;
        audit.Parameters.AddWithValue("$person", personId);
        Assert.Equal(1L, Convert.ToInt64(await audit.ExecuteScalarAsync()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
