using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// A service number is NOT the immutable person identity. Verify that
/// dismissed and newly admitted people never share a monthly cell, detail,
/// attendance statistic or lifetime history when the displayed number matches.
/// </summary>
public sealed class MonthlyHistoryReuseTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-HISTORY-OLD','فرد مفصول سابق','فرد مفصول سابق',
                '5151','5151','2026-09-01','2026-09-01T07:00:00Z',3,0,2),
                  ('TST-HISTORY-NEW','فرد جديد مستقل','فرد جديد مستقل',
                '5151','5151','2026-10-06','2026-10-06T07:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,
                ServiceNumberASnapshot,ValidFromLocalDate,ValidToLocalDate,
                ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-HISTORY-OLD','فرد مفصول سابق','5151',
                    '2026-09-01',NULL,'TEST','2026-09-01T07:00:00Z','هوية المفصول'),
                  ('TST-HISTORY-NEW','فرد جديد مستقل','5151',
                    '2026-10-06',NULL,'TEST','2026-10-06T07:00:00Z','هوية جديدة مستقلة');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-H-OLD-ASG','TST-HISTORY-OLD','SITE-UNSPECIFIED',
                    '2026-09-01','2026-10-05',NULL,'2026-09-01T07:00:00Z'),
                  ('TST-H-NEW-ASG','TST-HISTORY-NEW','SITE-UNSPECIFIED',
                    '2026-10-06',NULL,NULL,'2026-10-06T07:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
            VALUES('TST-HISTORY-OLD','2026-10-04','STS-ABSENT','غياب','غ','#EF4444',
                'TST-H-OLD-ASG','SITE-UNSPECIFIED','غير محدد','manual','TEST',
                '2026-10-04T07:00:00Z','TEST-H-OLD-ATT'),
                  ('TST-HISTORY-NEW','2026-10-07','STS-PRESENT','موجود','م','#38BDF8',
                'TST-H-NEW-ASG','SITE-UNSPECIFIED','غير محدد','manual','TEST',
                '2026-10-07T07:00:00Z','TEST-H-NEW-ATT');
            INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,
                ServiceNumberASnapshot,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                SeparationReason,EffectiveLocalDate,CommittedAtUtc,ActorUserId,IdempotencyKey)
            VALUES('TST-HISTORY-EXIT','TST-HISTORY-OLD','فرد مفصول سابق','5151',
                3,'SITE-UNSPECIFIED','غير محدد','فصل معتمد',
                '2026-10-05','2026-10-05T07:00:00Z','TEST','TEST-H-EXIT');
            """;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SameNumber_SeparateMonthlyRows_MovementAndAttendanceRemainOriginal()
    {
        var service = new DailyMonthlyReadService(_db);
        var result = await service.MonthlyGridAsync(new JsonObject
        {
            ["year"] = 2026, ["month"] = 10, ["siteId"] = "SITE-UNSPECIFIED",
            ["page"] = 1, ["pageSize"] = 50
        }, CancellationToken.None);
        Assert.True(result.Success, result.UserMessage);
        var json = Assert.IsType<JsonObject>(result.Data);
        Assert.Equal(2L, json["total"]!.GetValue<long>());
        var rows = Assert.IsType<JsonArray>(json["items"]).OfType<JsonObject>()
            .ToDictionary(x => x["personId"]!.GetValue<string>(), StringComparer.Ordinal);
        var old = rows["TST-HISTORY-OLD"];
        var recent = rows["TST-HISTORY-NEW"];
        Assert.Equal("5151", old["serviceNumberA"]!.GetValue<string>());
        Assert.Equal("5151", recent["serviceNumberA"]!.GetValue<string>());
        Assert.Equal("فرد مفصول سابق", old["name"]!.GetValue<string>());
        Assert.Equal("فرد جديد مستقل", recent["name"]!.GetValue<string>());
        Assert.Equal("غير محدد", old["siteName"]!.GetValue<string>());
        Assert.Equal("غير محدد", recent["siteName"]!.GetValue<string>());

        var oldDays = Assert.IsType<JsonObject>(old["days"]);
        var newDays = Assert.IsType<JsonObject>(recent["days"]);
        Assert.Equal("غياب", oldDays["2026-10-04"]!["name"]!.GetValue<string>());
        Assert.Equal("separation", oldDays["2026-10-05"]!["kind"]!.GetValue<string>());
        Assert.Null(oldDays["2026-10-07"]);
        Assert.Null(newDays["2026-10-04"]);
        Assert.Null(newDays["2026-10-05"]);
        Assert.Equal("موجود", newDays["2026-10-07"]!["name"]!.GetValue<string>());
        Assert.Equal("فرد جديد مستقل", newDays["2026-10-07"]!["personName"]!.GetValue<string>());
    }

    [Fact]
    public async Task SameNumber_HistoricalDetailsAndLifetimeReports_NeverInheritPreviousPerson()
    {
        var service = new DailyMonthlyReadService(_db);
        var oldDetail = await service.MonthlyCellDetailsAsync(new JsonObject
        {
            ["personId"]="TST-HISTORY-OLD", ["businessDate"]="2026-10-05",
            ["siteId"]="SITE-UNSPECIFIED"
        }, CancellationToken.None);
        Assert.True(oldDetail.Success, oldDetail.UserMessage);
        var oldJson = Assert.IsType<JsonObject>(oldDetail.Data);
        Assert.Equal("فرد مفصول سابق", oldJson["personName"]!.GetValue<string>());
        Assert.Null(oldJson["attendance"]);
        Assert.Equal(3, oldJson["movement"]!["type"]!.GetValue<int>());
        var newDetail = await service.MonthlyCellDetailsAsync(new JsonObject
        {
            ["personId"]="TST-HISTORY-NEW", ["businessDate"]="2026-10-07",
            ["siteId"]="SITE-UNSPECIFIED"
        }, CancellationToken.None);
        Assert.True(newDetail.Success, newDetail.UserMessage);
        var newJson = Assert.IsType<JsonObject>(newDetail.Data);
        Assert.Equal("فرد جديد مستقل", newJson["personName"]!.GetValue<string>());
        Assert.Equal("موجود", newJson["attendance"]!["statusName"]!.GetValue<string>());
        Assert.Null(newJson["movement"]);

        var earlyNew = await service.MonthlyCellDetailsAsync(new JsonObject
        {
            ["personId"]="TST-HISTORY-NEW", ["businessDate"]="2026-10-04",
            ["siteId"]="SITE-UNSPECIFIED"
        }, CancellationToken.None);
        Assert.False(earlyNew.Success);
        var oldHistory = await new PersonnelReportService(_db).ReportAsync(new JsonObject{
            ["personId"]="TST-HISTORY-OLD"
        }, CancellationToken.None);
        var newHistory = await new PersonnelReportService(_db).ReportAsync(new JsonObject{
            ["personId"]="TST-HISTORY-NEW"
        }, CancellationToken.None);
        Assert.True(oldHistory.Success && newHistory.Success);
        var historical = Assert.IsType<JsonObject>(oldHistory.Data);
        var newlyAdmitted = Assert.IsType<JsonObject>(newHistory.Data);
        Assert.Equal(1L, historical["attendanceTotal"]!.GetValue<long>());
        Assert.Equal(1L, newlyAdmitted["attendanceTotal"]!.GetValue<long>());
        Assert.Equal(1L, historical["movementTotal"]!.GetValue<long>());
        Assert.Equal(0L, newlyAdmitted["movementTotal"]!.GetValue<long>());
        Assert.DoesNotContain("فرد مفصول سابق", newlyAdmitted.ToJsonString());
    }

    [Fact]
    public async Task Periods_FollowDocumentedEnrollmentAndDismissal_NotTodayPersonNumber()
    {
        var reader=new DailyMonthlyReadService(_db);
        var periods=await reader.ArchivePeriodsAsync(CancellationToken.None);
        Assert.True(periods.Success, periods.UserMessage);
        var list=Assert.IsType<JsonArray>(periods.Data)
            .Select(x=>x!.GetValue<string>()).ToArray();
        Assert.Contains("2026-09",list);
        Assert.Contains("2026-10",list);
        var september=await reader.MonthlyGridAsync(new JsonObject{
            ["year"]=2026,["month"]=9,["siteId"]="SITE-UNSPECIFIED"
        },CancellationToken.None);
        Assert.True(september.Success,september.UserMessage);
        var one=Assert.Single(Assert.IsType<JsonArray>(((JsonObject)september.Data!)["items"]));
        Assert.Equal("TST-HISTORY-OLD",one!["personId"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonObject>(one["days"]));
    }

    public Task DisposeAsync()=>Task.CompletedTask;

    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
