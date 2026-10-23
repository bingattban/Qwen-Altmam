using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class PersonnelReportIntegrationTests : IAsyncLifetime,IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
              NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
              OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-REPORT-1','فرد التقرير','فرد التقرير','R001','R001',
              '2026-01-01','2026-01-01T08:00:00Z',1,1,1),
              ('TST-REPORT-2','فرد آخر','فرد آخر','R002','R002',
              '2026-01-01','2026-01-01T08:00:00Z',1,1,1);
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
              ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-R-A1','TST-REPORT-1','SITE-UNSPECIFIED','2026-01-01',
              NULL,NULL,'2026-01-01T08:00:00Z'),
              ('TST-R-A2','TST-REPORT-2','SITE-UNSPECIFIED','2026-01-01',
              NULL,NULL,'2026-01-01T08:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
              StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
              AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
              Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
            VALUES('TST-REPORT-1','2026-01-15','STS-PRESENT','موجود','م','#38BDF8',
                   'TST-R-A1','SITE-UNSPECIFIED','غير محدد','manual','TEST',
                   '2026-01-15T08:00:00Z','RPT-1'),
                  ('TST-REPORT-1','2026-01-16','STS-ABSENT','غياب','غ','#EF4444',
                   'TST-R-A1','SITE-UNSPECIFIED','غير محدد','manual','TEST',
                   '2026-01-16T08:00:00Z','RPT-2'),
                  ('TST-REPORT-2','2026-01-16','STS-PRESENT','موجود','م','#38BDF8',
                   'TST-R-A2','SITE-UNSPECIFIED','غير محدد','manual','TEST',
                   '2026-01-16T08:00:00Z','RPT-3');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SinglePersonReport_PreservesSavedDailyAndMonthlyCounts()
    {
        var svc=new PersonnelReportService(_db);
        var result=await svc.ReportAsync(new JsonObject{["personId"]="TST-REPORT-1"},CancellationToken.None);
        Assert.True(result.Success,result.UserMessage);
        var data=Assert.IsType<JsonObject>(result.Data);
        Assert.Equal("R001",data["person"]!["serviceNumberA"]!.GetValue<string>());
        Assert.Equal(2L,data["attendanceTotal"]!.GetValue<long>());
        var statuses=Assert.IsType<JsonArray>(data["statusTotals"]);
        Assert.Equal(2,statuses.Count);
        Assert.All(statuses,record=>Assert.Equal(1L,record!["count"]!.GetValue<long>()));
        var monthly=Assert.IsType<JsonArray>(data["monthlyTotals"]);
        Assert.All(monthly,record=>Assert.Equal("2026-01",record!["period"]!.GetValue<string>()));
        Assert.Equal(2,Assert.IsType<JsonArray>(data["attendance"]).Count);
        Assert.Equal(0L,data["auditTotal"]!.GetValue<long>());
        Assert.Empty(Assert.IsType<JsonArray>(data["movements"]));
    }
    [Fact]
    public async Task InvalidPersonIsNeverReplacedWithAnUnrelatedIdentity()
    {
        var result=await new PersonnelReportService(_db).ReportAsync(
            new JsonObject{["personId"]="TST-REPORT-NOT-FOUND"},CancellationToken.None);
        Assert.False(result.Success);
    }
    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
