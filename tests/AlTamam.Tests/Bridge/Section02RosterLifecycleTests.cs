using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class Section02RosterLifecycleTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private const string Day = "2026-10-24";
    private const string Actor = "TST-LIFECYCLE-ADMIN";

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        Execute("""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
              NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
              OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-LIFE-P1','الأول','الأول','1111','1111','2026-01-01',
                '2026-01-01T00:00:00Z',1,1,1),
              ('TST-LIFE-P2','الثاني','الثاني','2222','2222','2026-01-01',
                '2026-01-01T00:00:00Z',1,1,1);
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
              ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-LIFE-A1','TST-LIFE-P1','SITE-UNSPECIFIED','2026-01-01',
                NULL,NULL,'2026-01-01T00:00:00Z'),
              ('TST-LIFE-A2','TST-LIFE-P2','SITE-UNSPECIFIED','2026-01-01',
                NULL,NULL,'2026-01-01T00:00:00Z');
            """);
    }
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
    private void Execute(string sql)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
    private long Number(string sql)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private string Text(string sql)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }
    private static JsonObject Selected() => new()
    {
        ["personIds"] = new JsonArray(JsonValue.Create("TST-LIFE-P1"),JsonValue.Create("TST-LIFE-P2")),
        ["effectiveLocalDate"] = Day
    };
    private static JsonObject Commit(JsonObject preview,JsonArray ids) => new()
    {
        ["personIds"] = ids.DeepClone(),
        ["effectiveLocalDate"] = Day,
        ["baseRevision"] = preview["baseRevision"]!.GetValue<long>(),
        ["expectedRevision"] = preview["baseRevision"]!.GetValue<long>(),
        ["previewToken"] = preview["previewToken"]!.GetValue<string>(),
        ["reason"] = "إخراج من القوة الفعالة لأغراض اختبار سلامة الأرشيف"
    };

    [Fact]
    public async Task ArchiveBatch_ClosesButNeverErasesHistory_RejectsReplay()
    {
        var service = new PersonRosterService(_db);
        var preview = await service.PreviewArchivePersonsAsync(Selected(), Actor, CancellationToken.None);
        Assert.True(preview.Success, preview.UserMessage);
        var approved = Commit(Assert.IsType<JsonObject>(preview.Data), (JsonArray)Selected()["personIds"]!);
        var saved = await service.ArchivePersonsAsync(Actor, "test-admin", approved, CancellationToken.None);
        Assert.True(saved.Success, saved.UserMessage);
        Assert.Equal(2,Number("SELECT COUNT(*) FROM Persons WHERE OrganizationalStatus=4 AND ActiveFlag=0;"));
        Assert.Equal(2,Number("SELECT COUNT(*) FROM PersonAssignments WHERE ValidToLocalDate='2026-10-24';"));
        Assert.Equal(2,Number("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_ARCHIVE';"));
        var replay = await service.ArchivePersonsAsync(Actor, "test-admin", approved, CancellationToken.None);
        Assert.False(replay.Success);
        Assert.Equal(2,Number("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_ARCHIVE';"));
    }

    [Fact]
    public async Task StaleBatchRejectsAllMembersWithoutPartialArchiving()
    {
        var service = new PersonRosterService(_db);
        var preview = await service.PreviewArchivePersonsAsync(Selected(), Actor, CancellationToken.None);
        Assert.True(preview.Success, preview.UserMessage);
        var data = Assert.IsType<JsonObject>(preview.Data);
        Execute("UPDATE Persons SET RowVersion=RowVersion+1 WHERE PersonId='TST-LIFE-P2';");
        var result = await service.ArchivePersonsAsync(Actor, "test-admin",
            Commit(data, (JsonArray)Selected()["personIds"]!), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(2,Number("SELECT COUNT(*) FROM Persons WHERE ActiveFlag=1;"));
        Assert.Equal(2,Number("SELECT COUNT(*) FROM PersonAssignments WHERE ValidToLocalDate IS NULL;"));
        Assert.Equal(0,Number("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_ARCHIVE';"));
    }

    [Fact]
    public async Task Reactivation_CreatesNewWindowAndPreservesArchiveGap()
    {
        Execute("""
            UPDATE Persons SET OrganizationalStatus=4,ActiveFlag=0,RowVersion=2
              WHERE PersonId='TST-LIFE-P1';
            UPDATE PersonAssignments SET ValidToLocalDate='2026-02-01'
              WHERE PersonId='TST-LIFE-P1';
            """);
        var service = new PersonRosterService(_db);
        var payload = new JsonObject
        {
            ["personId"]="TST-LIFE-P1",["effectiveLocalDate"]="2026-03-01",
            ["expectedRevision"]=2,["reason"]="عودة مثبتة"
        };
        var result = await service.RestoreArchivedPersonAsync(Actor, "test-admin",
            payload, CancellationToken.None);
        Assert.True(result.Success, result.UserMessage);
        Assert.Equal(2,Number("SELECT COUNT(*) FROM PersonAssignments WHERE PersonId='TST-LIFE-P1';"));
        Assert.Equal("2026-02-01",Text("SELECT ValidToLocalDate FROM PersonAssignments WHERE AssignmentId='TST-LIFE-A1';"));
        Assert.Equal("2026-03-01",Text("SELECT ValidFromLocalDate FROM PersonAssignments WHERE PersonId='TST-LIFE-P1' AND ValidToLocalDate IS NULL;"));
        Assert.Equal(0,Number("SELECT COUNT(*) FROM PersonAssignments WHERE PersonId='TST-LIFE-P1' AND ValidFromLocalDate<='2026-02-15' AND (ValidToLocalDate IS NULL OR ValidToLocalDate>'2026-02-15');"));
        Assert.Equal(3,Number("SELECT RowVersion FROM Persons WHERE PersonId='TST-LIFE-P1';"));
        var again = await service.RestoreArchivedPersonAsync(Actor,"test-admin",payload,CancellationToken.None);
        Assert.False(again.Success);
        Assert.Equal(1,Number("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_RESTORE';"));
    }
}
