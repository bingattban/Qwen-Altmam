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
    private static readonly string Day = DateTime.Today.ToString("yyyy-MM-dd");
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
    public async Task HistoricalDateCannotAlterCurrentRosterMembership()
    {
        var service = new PersonRosterService(_db);
        var backdated = Selected();
        backdated["effectiveLocalDate"] = "2026-01-02";
        var preview = await service.PreviewArchivePersonsAsync(backdated,Actor,CancellationToken.None);
        Assert.False(preview.Success);
        Assert.Equal(AlTamam.Contracts.Bridge.BridgeErrorCodes.HistoricDayLocked,preview.ErrorCode);
        var restore = await service.RestoreArchivedPersonAsync(Actor,"test-admin",new JsonObject {
            ["personId"]="TST-LIFE-P1", ["effectiveLocalDate"]="2026-01-02",
            ["expectedRevision"]=1
        },CancellationToken.None);
        Assert.False(restore.Success);
        Assert.Equal(AlTamam.Contracts.Bridge.BridgeErrorCodes.HistoricDayLocked,restore.ErrorCode);
        Assert.Equal(2,Number("SELECT COUNT(*) FROM PersonAssignments WHERE ValidToLocalDate IS NULL;"));
    }

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
        Assert.Equal(2,Number($"SELECT COUNT(*) FROM PersonAssignments WHERE ValidToLocalDate='{Day}';"));
        Assert.Equal(2,Number("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_ARCHIVE';"));
        var replay = await service.ArchivePersonsAsync(Actor, "test-admin", approved, CancellationToken.None);
        Assert.False(replay.Success);
        Assert.Equal(2,Number("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_ARCHIVE';"));
    }

    [Fact]
    public async Task ArchiveLinkedSupervisor_RevokesSessionsWithoutRestoringAccessWhenPersonReturns()
    {
        var (hash,salt)=AlTamam.Infrastructure.Security.PasswordSecurity.HashPassword("Abc123!secure");
        Execute($"""
            INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
              CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,
              FailedLoginCount,CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,RowVersion)
            VALUES('TST-ARCHIVE-USER','TST-LIFE-P1','archive-user','archive-user',3,
              '{hash}','{salt}',0,1,0,'TST-ADMIN','admin','2026-01-01T00:00:00Z',1);
            INSERT INTO UserSessions(SessionNonce,UserId,Role,IssuedAtUtc,ExpiresAtUtc,RevokedFlag)
            VALUES('f1aabbccddeeff001122334455667788','TST-ARCHIVE-USER',3,
              '2026-10-01T00:00:00Z','2028-10-01T00:00:00Z',0);
            """);
        var svc=new PersonRosterService(_db);
        var preview=await svc.PreviewArchivePersonsAsync(Selected(),Actor,default);
        Assert.True(preview.Success,preview.UserMessage);
        var payload=Commit(Assert.IsType<JsonObject>(preview.Data),
            (JsonArray)Selected()["personIds"]!);
        var archive=await svc.ArchivePersonsAsync(Actor,"test-admin",payload,default);
        Assert.True(archive.Success,archive.UserMessage);
        Assert.Equal(0,Number("SELECT ActiveFlag FROM Users WHERE UserId='TST-ARCHIVE-USER';"));
        Assert.Equal(1,Number("SELECT RevokedFlag FROM UserSessions WHERE UserId='TST-ARCHIVE-USER';"));
        Assert.Equal(1,Number("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='USER_LIFECYCLE_SUSPEND';"));
        var returned=await svc.RestoreArchivedPersonAsync(Actor,"test-admin",new JsonObject{
            ["personId"]="TST-LIFE-P1",["effectiveLocalDate"]=Day,
            ["expectedRevision"]=2L,["reason"]="عودة نظامية موثقة"
        },default);
        Assert.True(returned.Success,returned.UserMessage);
        Assert.Equal(0,Number("SELECT ActiveFlag FROM Users WHERE UserId='TST-ARCHIVE-USER';"));
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
    public async Task EqualAggregateRevisionCannotHideOneStaleMember()
    {
        Execute("UPDATE Persons SET RowVersion=2 WHERE PersonId IN ('TST-LIFE-P1','TST-LIFE-P2');");
        var svc = new PersonRosterService(_db);
        var preview = await svc.PreviewArchivePersonsAsync(Selected(),Actor,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        Execute("""
            UPDATE Persons SET RowVersion=3 WHERE PersonId='TST-LIFE-P1';
            UPDATE Persons SET RowVersion=1 WHERE PersonId='TST-LIFE-P2';
            """);
        // Total revision stays 4; each individual version MUST still match.
        Assert.Equal(4,Number("SELECT SUM(RowVersion) FROM Persons;"));
        var result = await svc.ArchivePersonsAsync(Actor,"test-admin",
            Commit(Assert.IsType<JsonObject>(preview.Data),(JsonArray)Selected()["personIds"]!),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(2,Number("SELECT COUNT(*) FROM Persons WHERE ActiveFlag=1;"));
        Assert.Equal(2,Number("SELECT COUNT(*) FROM PersonAssignments WHERE ValidToLocalDate IS NULL;"));
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
            ["personId"]="TST-LIFE-P1",["effectiveLocalDate"]=Day,
            ["expectedRevision"]=2,["reason"]="عودة مثبتة"
        };
        var result = await service.RestoreArchivedPersonAsync(Actor, "test-admin",
            payload, CancellationToken.None);
        Assert.True(result.Success, result.UserMessage);
        Assert.Equal(2,Number("SELECT COUNT(*) FROM PersonAssignments WHERE PersonId='TST-LIFE-P1';"));
        Assert.Equal("2026-02-01",Text("SELECT ValidToLocalDate FROM PersonAssignments WHERE AssignmentId='TST-LIFE-A1';"));
        Assert.Equal(Day,Text("SELECT ValidFromLocalDate FROM PersonAssignments WHERE PersonId='TST-LIFE-P1' AND ValidToLocalDate IS NULL;"));
        Assert.Equal(0,Number("SELECT COUNT(*) FROM PersonAssignments WHERE PersonId='TST-LIFE-P1' AND ValidFromLocalDate<='2026-02-15' AND (ValidToLocalDate IS NULL OR ValidToLocalDate>'2026-02-15');"));
        Assert.Equal(3,Number("SELECT RowVersion FROM Persons WHERE PersonId='TST-LIFE-P1';"));
        var again = await service.RestoreArchivedPersonAsync(Actor,"test-admin",payload,CancellationToken.None);
        Assert.False(again.Success);
        Assert.Equal(1,Number("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_RESTORE';"));
    }
}
