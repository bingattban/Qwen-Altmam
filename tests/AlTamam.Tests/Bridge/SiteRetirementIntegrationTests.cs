using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Removal is a logical site retirement. Earlier attendance, PersonIds and
/// effective-dated assignments are never deleted/reassigned retroactively.
/// </summary>
public sealed class SiteRetirementIntegrationTests : IAsyncLifetime,IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026,10,26,12,0,0,TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private readonly Clock _clock=new();
    private static readonly BridgePrincipal Actor=new(
        "TST-SITE-ACTOR","test-site-admin",UserRole.GeneralAdministration,
        "99999999999999999999999999999999");
    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        using var seed=c.CreateCommand();
        seed.CommandText="""
            INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,
                ActiveFlag,CreatedAtUtc,RowVersion)
            VALUES('SITE-RETIRE','موقع التجربة','موقع التجربة',0,1,
                '2026-09-01T00:00:00Z',3);
            INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,
                ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
            VALUES('SITE-RETIRE','موقع التجربة','2026-09-01',
                NULL,'TST-SITE-ACTOR','2026-09-01T00:00:00Z');
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,
                ActiveFlag,RowVersion)
            VALUES('TST-SITE-OLD','فرد قديم مفصول','فرد قديم مفصول',
                '4411','4411','2026-09-01','2026-09-01T00:00:00Z',3,0,2),
                  ('TST-SITE-NEW','فرد جديد مستقل','فرد جديد مستقل',
                '4411','4411','2026-10-03','2026-10-03T00:00:00Z',1,1,4),
                  ('TST-SITE-ACTIVE','فرد آخر نشط','فرد آخر نشط',
                '5522','5522','2026-09-01','2026-09-01T00:00:00Z',1,1,2);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-SITE-OLD','فرد قديم مفصول','4411','2026-09-01',
                NULL,'TEST','2026-09-01T00:00:00Z','تاريخ مستقل'),
                  ('TST-SITE-NEW','فرد جديد مستقل','4411','2026-10-03',
                NULL,'TEST','2026-10-03T00:00:00Z','تاريخ مستقل'),
                  ('TST-SITE-ACTIVE','فرد آخر نشط','5522','2026-09-01',
                NULL,'TEST','2026-09-01T00:00:00Z','تاريخ مستقل');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-SITE-ARCHIVED','TST-SITE-OLD','SITE-RETIRE',
                '2026-09-01','2026-10-02',NULL,'2026-09-01T00:00:00Z'),
                  ('TST-SITE-A1','TST-SITE-NEW','SITE-RETIRE',
                '2026-10-03',NULL,NULL,'2026-10-03T00:00:00Z'),
                  ('TST-SITE-A2','TST-SITE-ACTIVE','SITE-RETIRE',
                '2026-09-01',NULL,NULL,'2026-09-01T00:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
            VALUES('TST-SITE-OLD','2026-10-01','STS-ABSENT','غياب','غ','#ef4444',
                'TST-SITE-ARCHIVED','SITE-RETIRE','موقع التجربة','manual','TEST',
                '2026-10-01T08:00:00Z','SITE-H-OLD'),
                  ('TST-SITE-NEW','2026-10-21','STS-PRESENT','موجود','م','#38bdf8',
                'TST-SITE-A1','SITE-RETIRE','موقع التجربة','manual','TEST',
                '2026-10-21T08:00:00Z','SITE-H-NEW');
            """;
        await seed.ExecuteNonQueryAsync();
    }
    private static JsonObject Payload(int count=2,long version=3,bool confirmed=true)
        =>new(){
            ["siteId"]="SITE-RETIRE",["expectedActiveCount"]=count,
            ["expectedRevision"]=version,["confirmed"]=confirmed,
            ["operationId"]="site-retirement-20261026-test"
        };
    private long Count(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText=sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private string Scalar(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText=sql;
        return Convert.ToString(cmd.ExecuteScalar())??"";
    }

    [Fact]
    public async Task RetiresSiteOnce_PreservesHistory_AndNeverMergesReusedNumbers()
    {
        var svc=new AdminConfigurationService(_db,_clock);
        var input=Payload();
        var result=await svc.RemoveSiteAndReassignCurrentPersonsAsync(
            input,Actor,CancellationToken.None);
        Assert.True(result.Success,result.UserMessage);
        var data=Assert.IsType<JsonObject>(result.Data);
        Assert.Equal(2,data["reassignedCount"]!.GetValue<int>());
        Assert.Equal("2026-10-26",data["effectiveDate"]!.GetValue<string>());
        Assert.Equal("0",Scalar("SELECT ActiveFlag FROM Sites WHERE SiteId='SITE-RETIRE'"));
        Assert.Equal(5L,Count("SELECT COUNT(*) FROM PersonAssignments"));
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM PersonAssignments WHERE SiteId='SITE-UNSPECIFIED' AND ValidToLocalDate IS NULL"));
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM PersonAssignments WHERE SiteId='SITE-RETIRE' AND ValidToLocalDate='2026-10-26'"));
        Assert.Equal("2026-10-02",Scalar("SELECT ValidToLocalDate FROM PersonAssignments WHERE AssignmentId='TST-SITE-ARCHIVED'"));
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM AttendanceRecords"));
        Assert.Equal("SITE-RETIRE",Scalar("SELECT SiteIdAtEffectiveDate FROM AttendanceRecords WHERE PersonId='TST-SITE-NEW'"));
        Assert.Equal("موقع التجربة",Scalar("SELECT SiteNameSnapshot FROM AttendanceRecords WHERE PersonId='TST-SITE-NEW'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='SITE_RETIRE'"));
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='SITE_RETIRE_PERSON_REASSIGNED'"));
        Assert.Equal(0L,Count("SELECT COUNT(*) FROM AuditEvents WHERE TargetEntityId='TST-SITE-OLD'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM AuditEvents WHERE TargetEntityId='TST-SITE-NEW'"));
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="PRAGMA foreign_key_check;";
            using var r=await q.ExecuteReaderAsync();
            Assert.False(await r.ReadAsync());
        }
        var retried=await svc.RemoveSiteAndReassignCurrentPersonsAsync(
            (JsonObject)input.DeepClone(),Actor,CancellationToken.None);
        Assert.True(retried.Success,retried.UserMessage);
        Assert.Equal(3L,Count("SELECT COUNT(*) FROM AuditEvents"));
        Assert.Equal(5L,Count("SELECT COUNT(*) FROM PersonAssignments"));
        var mismatch=Payload();
        mismatch["operationId"]="site-retirement-mismatched-id-26";
        Assert.False((await svc.RemoveSiteAndReassignCurrentPersonsAsync(
            mismatch,Actor,CancellationToken.None)).Success);
    }

    [Fact]
    public async Task SystemSiteAndUnconfirmedOrStalePayload_CannotChangePersonnel()
    {
        var svc=new AdminConfigurationService(_db,_clock);
        foreach(var data in new[]{
            new JsonObject{["siteId"]="SITE-UNSPECIFIED",["expectedRevision"]=1,
                ["expectedActiveCount"]=0,["confirmed"]=true,
                ["operationId"]="site-retirement-protected-00001"},
            Payload(2,3,false),Payload(2,99),Payload(1,3)
        })
        {
            var result=await svc.RemoveSiteAndReassignCurrentPersonsAsync(
                data,Actor,CancellationToken.None);
            Assert.False(result.Success);
        }
        Assert.Equal("1",Scalar("SELECT ActiveFlag FROM Sites WHERE SiteId='SITE-RETIRE'"));
        Assert.Equal(3L,Count("SELECT COUNT(*) FROM PersonAssignments"));
        Assert.Equal(0L,Count("SELECT COUNT(*) FROM AuditEvents"));
    }

    [Fact]
    public async Task SameDayPreparedOrNewlyEnrolledOrClosedDay_RejectsInsteadOfRewritingPast()
    {
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                    StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                    AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                    Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
                VALUES('TST-SITE-NEW','2026-10-26','STS-PRESENT','موجود','م','#22c55e',
                    'TST-SITE-A1','SITE-RETIRE','موقع التجربة','manual','TEST',
                    '2026-10-26T08:00:00Z','SITE-SAME-DAY');
                """;
            await q.ExecuteNonQueryAsync();
        }
        var svc=new AdminConfigurationService(_db,_clock);
        Assert.False((await svc.RemoveSiteAndReassignCurrentPersonsAsync(
            Payload(),Actor,CancellationToken.None)).Success);
        Assert.Equal(3L,Count("SELECT COUNT(*) FROM PersonAssignments"));
        Assert.Equal("1",Scalar("SELECT ActiveFlag FROM Sites WHERE SiteId='SITE-RETIRE'"));
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                DELETE FROM AttendanceRecords WHERE IdempotencyKey='SITE-SAME-DAY';
                UPDATE PersonAssignments SET ValidFromLocalDate='2026-10-26'
                  WHERE AssignmentId='TST-SITE-A1';
                """;
            await q.ExecuteNonQueryAsync();
        }
        Assert.False((await svc.RemoveSiteAndReassignCurrentPersonsAsync(
            Payload(),Actor,CancellationToken.None)).Success);
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                UPDATE PersonAssignments SET ValidFromLocalDate='2026-10-03'
                  WHERE AssignmentId='TST-SITE-A1';
                INSERT INTO PeriodClosures(PeriodType,PeriodKey,IsClosed,ClosedAtUtc,ClosedByActor,PolicyVersion)
                VALUES('DAY','2026-10-26',1,'2026-10-26T20:00:00Z','TEST','site-retire-test');
                """;
            await q.ExecuteNonQueryAsync();
        }
        Assert.False((await svc.RemoveSiteAndReassignCurrentPersonsAsync(
            Payload(),Actor,CancellationToken.None)).Success);
        Assert.Equal(3L,Count("SELECT COUNT(*) FROM PersonAssignments"));
        Assert.Equal(0L,Count("SELECT COUNT(*) FROM AuditEvents"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
