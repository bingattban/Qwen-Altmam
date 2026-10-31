using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Phase 06 regression: immutable SQLCipher facts, not mock attendance counters.</summary>
public sealed class Phase06AttendanceAcceptanceTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new(
        "TST-PHASE06-ACTOR", "phase06-test", UserRole.GeneralAdministration,
        "aabbccddeeff00112233445566778899");
    private static string Today() => TimeProvider.System.GetLocalNow().ToString("yyyy-MM-dd");
    private static JsonObject Selection(string mode, string status, params string[] ids) => new()
    {
        ["businessDate"] = Today(), ["mode"] = mode, ["statusId"] = status,
        ["personIds"] = new JsonArray(ids.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
    };
    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }
    private void Seed(int count)
    {
        using var connection = _db.CreateOpenConnection();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES($id,$name,$name,$number,$number,$date,$now,1,1,1);
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES($assign,$id,'SITE-UNSPECIFIED',$date,NULL,NULL,$now);
            """;
        for (int i = 1; i <= count; i++)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", $"TST-PHASE06-P{i:D4}");
            cmd.Parameters.AddWithValue("$assign", $"TST-PHASE06-A{i:D4}");
            cmd.Parameters.AddWithValue("$name", $"فرد اختبار العدد {i:D4}");
            cmd.Parameters.AddWithValue("$number", $"93{i:D5}");
            cmd.Parameters.AddWithValue("$date", Today());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private long Count(string table)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task OneThousandActivePeople_ArePaged_AbsentIsCommittedNotUnprepared_AndRetryIsIdempotent()
    {
        Seed(1000);
        var reader = new DailyMonthlyReadService(_db);
        var today = Today();
        var page = await reader.DailyRosterAsync(new JsonObject {
            ["businessDate"] = today, ["page"] = 13, ["pageSize"] = 80,
            ["statusId"] = "__ELIGIBLE__"
        }, default);
        Assert.True(page.Success, page.UserMessage);
        var paged = Assert.IsType<JsonObject>(page.Data);
        Assert.Equal(1000L, paged["total"]!.GetValue<long>());
        Assert.Equal(40, Assert.IsType<JsonArray>(paged["items"]).Count);
        var before = Assert.IsType<JsonObject>((await reader.DashboardSummaryAsync(
            new JsonObject {["businessDate"] = today}, default)).Data);
        Assert.Equal(1000L, before["totalForce"]!.GetValue<long>());
        Assert.Equal(1000L, before["unprepared"]!.GetValue<long>());

        var service = new AttendanceCommitService(_db, new SyntheticDaytimeClock());
        var names = Enumerable.Range(1, 100).Select(i => $"TST-PHASE06-P{i:D4}").ToArray();
        var preview = await service.PreviewAsync(Selection("initial","STS-ABSENT", names),
            Actor, default);
        Assert.True(preview.Success, preview.UserMessage);
        Assert.Equal(0L, Count("AttendanceRecords"));
        var token = Assert.IsType<JsonObject>(preview.Data)["previewToken"]!.GetValue<string>();
        var intent = new JsonObject {
            ["previewToken"] = token, ["operationId"] = "phase06-1000-person-absent-batch-01"
        };
        Assert.True((await service.CommitAsync(intent, Actor, false, default)).Success);
        Assert.True((await service.CommitAsync((JsonObject)intent.DeepClone(), Actor, false, default)).Success);
        Assert.Equal(100L, Count("AttendanceRecords"));
        Assert.Equal(100L, Count("AuditEvents"));
        Assert.Equal(0L, Count("AttendanceRevisions"));
        var after = Assert.IsType<JsonObject>((await reader.DashboardSummaryAsync(
            new JsonObject {["businessDate"] = today}, default)).Data);
        Assert.Equal(1000L, after["totalForce"]!.GetValue<long>());
        Assert.Equal(900L, after["unprepared"]!.GetValue<long>());
        var statuses = Assert.IsType<JsonArray>(after["statusCounts"]);
        Assert.Equal(100L, statuses.OfType<JsonObject>().Single(s =>
            s["statusId"]!.GetValue<string>() == "STS-ABSENT")["count"]!.GetValue<long>());
        Assert.False((await service.PreviewAsync(Selection("revise","STS-ABSENT",
            names[0]), Actor, default)).Success);
        Assert.Equal(0L, Count("AttendanceRevisions"));
    }

    [Fact]
    public async Task BulkPreview_IdentifiesAllConflictingPeopleAndTransferDate_WithoutPartialWrite()
    {
        Seed(2);
        var service = new AttendanceCommitService(_db, new SyntheticDaytimeClock());
        var initial = await service.PreviewAsync(Selection("initial","STS-PRESENT",
            "TST-PHASE06-P0001"), Actor, default);
        Assert.True(initial.Success, initial.UserMessage);
        Assert.True((await service.CommitAsync(new JsonObject {
            ["previewToken"] = ((JsonObject)initial.Data!)["previewToken"]!.GetValue<string>()
        }, Actor, false, default)).Success);
        using (var connection = _db.CreateOpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,
                    ServiceNumberASnapshot,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                    DestinationSiteId,DestinationSiteNameSnapshot,EffectiveLocalDate,
                    CommittedAtUtc,ActorUserId,IdempotencyKey)
                VALUES('TST-PHASE06-MOVE','TST-PHASE06-P0002','فرد اختبار العدد 0002',
                    '9300002',1,'SITE-UNSPECIFIED','غير محدد','SITE-UNSPECIFIED',
                    'غير محدد',$date,$now,'TST-PHASE06-ACTOR','TST-PHASE06-MOVE-IDEM');
                """;
            cmd.Parameters.AddWithValue("$date", Today());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        var rejected = await service.PreviewAsync(Selection("initial","STS-ABSENT",
            "TST-PHASE06-P0001", "TST-PHASE06-P0002"), Actor, default);
        Assert.False(rejected.Success);
        Assert.Contains("فرد اختبار العدد 0001", rejected.UserMessage);
        Assert.Contains("9300001", rejected.UserMessage);
        Assert.Contains("فرد اختبار العدد 0002", rejected.UserMessage);
        Assert.Contains("9300002", rejected.UserMessage);
        Assert.Contains(Today(), rejected.UserMessage);
        var issues = Assert.IsType<JsonObject>(rejected.Data);
        Assert.Equal(2, Assert.IsType<JsonArray>(issues["conflicts"]).Count);
        Assert.Equal(1L, Count("AttendanceRecords"));
        Assert.Equal(1L, Count("AuditEvents"));
    }

    [Fact]
    public async Task InterruptedSecondInsert_RollsBackFirstAttendanceAndItsAudit()
    {
        Seed(2);
        var service = new AttendanceCommitService(_db, new SyntheticDaytimeClock());
        var preview = await service.PreviewAsync(Selection("initial","STS-PRESENT",
            "TST-PHASE06-P0001","TST-PHASE06-P0002"),Actor,default);
        Assert.True(preview.Success,preview.UserMessage);
        Assert.Equal(0L,Count("AttendanceRecords"));
        using(var c=_db.CreateOpenConnection())
        using(var trigger=c.CreateCommand())
        {
            trigger.CommandText="""
                CREATE TRIGGER TST_Phase06_FailSecondInsert
                BEFORE INSERT ON AttendanceRecords
                WHEN NEW.PersonId='TST-PHASE06-P0002'
                BEGIN SELECT RAISE(ABORT,'synthetic disk write failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }
        var request=new JsonObject {
            ["previewToken"]=((JsonObject)preview.Data!)["previewToken"]!.GetValue<string>(),
            ["operationId"]="phase06-fault-injection-rollback-001"
        };
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await service.CommitAsync(request,Actor,false,default));
        Assert.Equal(0L,Count("AttendanceRecords"));
        Assert.Equal(0L,Count("AuditEvents"));
        Assert.Equal(0L,Count("MutationReceipts"));
        Assert.Equal(0L,Count("AttendanceRevisions"));
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
