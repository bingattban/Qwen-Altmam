using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Real SQLCipher, effective-dated closed-yesterday records and a fake local clock.</summary>
public sealed class UnpreparedCarryForwardIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private readonly SyntheticDaytimeClock _clock=new(19,59);
    private static readonly BridgePrincipal Actor = new("TST-CARRY-ADMIN","carry-admin",
        UserRole.OrdinaryAdministration,"123456789abcdef123456789abcdef12");
    private static string Today=>TimeProvider.System.GetLocalNow().ToString("yyyy-MM-dd");
    private static string Yesterday=>DateOnly.Parse(Today).AddDays(-1).ToString("yyyy-MM-dd");
    private static string Earlier=>DateOnly.Parse(Today).AddDays(-6).ToString("yyyy-MM-dd");

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        for(int i=1;i<=3;i++)
        {
            using var q=c.CreateCommand();
            q.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                    NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($id,$name,$name,$num,$num,$start,'2026-01-01T00:00:00Z',1,1,1);
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES($assign,$id,'SITE-UNSPECIFIED',$start,NULL,NULL,'2026-01-01T00:00:00Z');
                """;
            q.Parameters.AddWithValue("$id","TST-CARRY-P"+i);
            q.Parameters.AddWithValue("$name","فرد تجريبي "+i);
            q.Parameters.AddWithValue("$num","500"+i);
            q.Parameters.AddWithValue("$assign","TST-CARRY-A"+i);
            q.Parameters.AddWithValue("$start",Earlier);
            await q.ExecuteNonQueryAsync();
        }
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                    StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                    AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                    Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES ('TST-CARRY-P1',$yday,'STS-PRESENT','موجود','م','#38BDF8',
                    'TST-CARRY-A1','SITE-UNSPECIFIED','غير محدد','manual','TST-ADMIN',
                    '2026-01-01T12:00:00Z',1,'TST-CARRY-Y1'),
                    ('TST-CARRY-P2',$yday,'STS-ABSENT','غياب','غ','#EF4444',
                    'TST-CARRY-A2','SITE-UNSPECIFIED','غير محدد','manual','TST-ADMIN',
                    '2026-01-01T12:00:00Z',1,'TST-CARRY-Y2');
                INSERT INTO PeriodClosures(PeriodKey,PeriodType,IsClosed,
                    ClosedAtUtc,ClosedByActor,PolicyVersion)
                VALUES($yday,'DAY',1,'2026-01-01T20:00:00Z','USR-SYSTEM','1');
                """;
            q.Parameters.AddWithValue("$yday",Yesterday);
            await q.ExecuteNonQueryAsync();
        }
    }
    private long Count(string table)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM "+table;
        return Convert.ToInt64(q.ExecuteScalar());
    }
    private static JsonObject Plan(params string[] ids)=>new()
    {
        ["businessDate"]=Today,
        ["siteId"]="SITE-UNSPECIFIED",
        ["personIds"]=new JsonArray(ids.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray())
    };
    private static JsonObject Data(BridgeResponse response)
    {
        Assert.True(response.Success,response.UserMessage);
        return Assert.IsType<JsonObject>(response.Data);
    }
    private async Task ConfigureDefault()
    {
        var result=await new AutoAttendancePolicyService(_db,_clock).SetAsync(new JsonObject
        {
            ["statusId"]="STS-PERMISSION",["operationId"]="TST-CARRY-DEFAULT-00001"
        },Actor,default);
        Assert.True(result.Success,result.UserMessage);
    }

    [Fact]
    public async Task ExplicitBulkApproval_CopiesIndividualYesterdayStatuses_DefaultAndNeverOverwritesAtEight()
    {
        var service=new UnpreparedCarryForwardService(_db,_clock);
        var rejected=await service.PreviewAsync(Plan("TST-CARRY-P3"),Actor,default);
        Assert.False(rejected.Success); // no yesterday, no administrator default.
        await ConfigureDefault();
        var review=Data(await service.PreviewAsync(Plan(
            "TST-CARRY-P1","TST-CARRY-P2","TST-CARRY-P3"),Actor,default));
        var persons=Assert.IsType<JsonArray>(review["persons"]);
        Assert.Equal(3,persons.Count);
        Assert.Equal("موجود",persons[0]!["statusName"]!.GetValue<string>());
        Assert.Equal("غياب",persons[1]!["statusName"]!.GetValue<string>());
        Assert.Equal("STS-PERMISSION",persons[2]!["statusId"]!.GetValue<string>());
        Assert.False(persons[2]!["fromYesterday"]!.GetValue<bool>());
        Assert.Equal(2L,Count("AttendanceRecords"));

        var payload=new JsonObject{["previewToken"]=review["previewToken"]!.GetValue<string>(),
            ["operationId"]="TST-CARRY-MANUAL-00001"};
        var saved=Data(await service.CommitAsync(payload,Actor,default));
        Assert.Equal(3,saved["count"]!.GetValue<int>());
        Assert.Equal(5L,Count("AttendanceRecords"));
        Assert.Equal(1L,Count("AuditEvents")-1L); // one early approval plus one admin setting.

        using(var c=_db.CreateOpenConnection())
        {
            using var q=c.CreateCommand();
            q.CommandText="SELECT PersonId,StatusId,Source FROM AttendanceRecords WHERE AttendanceLocalDate=$date ORDER BY PersonId;";
            q.Parameters.AddWithValue("$date",Today);
            using var rd=await q.ExecuteReaderAsync();
            var rows=new List<(string,string,string)>();
            while(await rd.ReadAsync())rows.Add((rd.GetString(0),rd.GetString(1),rd.GetString(2)));
            Assert.Equal(new[]
            {
                ("TST-CARRY-P1","STS-PRESENT","bulk"),
                ("TST-CARRY-P2","STS-ABSENT","bulk"),
                ("TST-CARRY-P3","STS-PERMISSION","bulk")
            },rows);
        }

        var same=Data(await service.CommitAsync((JsonObject)payload.DeepClone(),Actor,default));
        Assert.Equal(3,same["count"]!.GetValue<int>());
        var replay=await service.CommitAsync(new JsonObject
        {
            ["previewToken"]=review["previewToken"]!.GetValue<string>(),
            ["operationId"]="TST-CARRY-MANUAL-00002"
        },Actor,default);
        Assert.False(replay.Success);
        Assert.Equal(5L,Count("AttendanceRecords"));

        var auto=new AutoAttendanceService(_db,_clock);
        var done=await auto.RunForDateAsync(DateOnly.Parse(Today),false);
        Assert.Equal(0,done.CopiedCount);
        Assert.Equal(3,done.SkippedManualCount);
        Assert.Equal(5L,Count("AttendanceRecords"));
    }

    [Fact]
    public async Task PreviewAtSevenFiftyNine_CannotCommitAtEightOrAfter()
    {
        var service=new UnpreparedCarryForwardService(_db,_clock);
        var preview=Data(await service.PreviewAsync(Plan("TST-CARRY-P1"),Actor,default));
        _clock.SetLocalTime(20,0);
        var rejected=await service.CommitAsync(new JsonObject
        {
            ["previewToken"]=preview["previewToken"]!.GetValue<string>()
        },Actor,default);
        Assert.False(rejected.Success);
        Assert.Equal(BridgeErrorCodes.HistoricDayLocked,rejected.ErrorCode);
        Assert.Equal(2L,Count("AttendanceRecords"));
        Assert.False((await service.PreviewAsync(Plan("TST-CARRY-P1"),Actor,default)).Success);
    }

    [Fact]
    public async Task ExistingAttendanceAndTransferDayAreNotEligible_NoPartialWrites()
    {
        await ConfigureDefault();
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                    StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                    AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                    Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES ('TST-CARRY-P1',$today,'STS-PRESENT','موجود','م','#38BDF8',
                    'TST-CARRY-A1','SITE-UNSPECIFIED','غير محدد','manual','TST-ADMIN',
                    '2026-01-01T13:00:00Z',1,'TST-CARRY-TODAY');
                INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,ServiceNumberASnapshot,
                    MovementType,SourceSiteId,SourceSiteNameSnapshot,DestinationSiteId,DestinationSiteNameSnapshot,
                    DestinationExternalName,SeparationReason,EffectiveLocalDate,CommittedAtUtc,ActorUserId,IdempotencyKey)
                VALUES('TST-CARRY-MOV','TST-CARRY-P2','فرد تجريبي 2','5002',1,
                    'SITE-UNSPECIFIED','غير محدد','SITE-UNSPECIFIED','غير محدد',NULL,NULL,
                    $today,'2026-01-01T13:00:00Z','TST-ADMIN','TST-CARRY-MOVE');
                """;
            q.Parameters.AddWithValue("$today",Today);
            q.ExecuteNonQuery();
        }
        var service=new UnpreparedCarryForwardService(_db,_clock);
        var blocked=await service.PreviewAsync(Plan("TST-CARRY-P1","TST-CARRY-P2","TST-CARRY-P3"),
            Actor,default);
        Assert.False(blocked.Success);
        Assert.Equal(3L,Count("AttendanceRecords"));
        var valid=Data(await service.PreviewAsync(Plan("TST-CARRY-P3"),Actor,default));
        Assert.Single(Assert.IsType<JsonArray>(valid["persons"]));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
