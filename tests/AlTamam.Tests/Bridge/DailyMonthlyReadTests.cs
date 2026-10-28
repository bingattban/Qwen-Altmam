using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Read-only integration tests using encrypted, synthetic local records.</summary>
public sealed class DailyMonthlyReadTests : IAsyncLifetime, IDisposable
{
    private readonly string _file = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_file, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Persons (PersonId, CurrentVisibleName, NormalizedName, CurrentServiceNumberA,
                                NormalizedServiceNumberA, FirstRecordedLocalDate, FirstRecordedAtUtc,
                                OrganizationalStatus, ActiveFlag, RowVersion)
            VALUES ('TST-P-1','شخص اختباري','شخص اختباري','930001','930001',
                    '2026-10-01','2026-10-01T07:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-P-1','شخص اختباري','930001','2026-10-01',NULL,'TST-U',
                '2026-10-01T07:00:00Z','هوية اختبار محفوظة');
            INSERT INTO PersonAssignments (AssignmentId, PersonId, SiteId, ValidFromLocalDate,
                                           ValidToLocalDate, SourceMovementId, CreatedAtUtc)
            VALUES ('TST-A-1','TST-P-1','SITE-UNSPECIFIED','2026-10-01',
                    NULL,NULL,'2026-10-01T07:00:00Z');
            INSERT INTO AttendanceRecords (PersonId, AttendanceLocalDate, StatusId,
                                           StatusNameSnapshot, StatusSymbolSnapshot, StatusColorSnapshot,
                                           AssignmentIdAtEffectiveDate, SiteIdAtEffectiveDate,
                                           SiteNameSnapshot, Source, CreatedByUserId, CommittedAtUtc, IdempotencyKey)
            VALUES ('TST-P-1','2026-10-19','STS-ABSENT','غياب','غ','#EF4444',
                    'TST-A-1','SITE-UNSPECIFIED','غير محدد','manual','TST-U',
                    '2026-10-19T07:00:00Z','TST-IDEM-READ-1');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Dashboard_UsesSavedStatusAndSiteFilter()
    {
        var svc = new DailyMonthlyReadService(_db);
        var payload = new JsonObject { ["businessDate"] = "2026-10-19", ["siteId"] = "" };
        var summary = await svc.DashboardSummaryAsync(payload, CancellationToken.None);
        Assert.True(summary.Success);
        var data = Assert.IsType<JsonObject>(summary.Data);
        Assert.Equal(1L, data["totalForce"]!.GetValue<long>());
        Assert.Equal(0L, data["unprepared"]!.GetValue<long>());
        var statuses = Assert.IsType<JsonArray>(data["statusCounts"]);
        var absence = statuses.OfType<JsonObject>().Single(s => s["statusId"]!.GetValue<string>() == "STS-ABSENT");
        Assert.Equal(1L, absence["count"]!.GetValue<long>());

        var roster = await svc.DailyRosterAsync(new JsonObject
        {
            ["businessDate"] = "2026-10-19", ["siteId"] = "SITE-UNSPECIFIED",
            ["statusId"] = "STS-ABSENT", ["page"] = 1, ["pageSize"] = 10
        }, CancellationToken.None);
        Assert.True(roster.Success);
        var rows = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(roster.Data)["items"]);
        Assert.Single(rows);
        Assert.Equal("930001", rows[0]!["serviceNumberA"]!.GetValue<string>());
        var filtered = await svc.DailyRosterAsync(new JsonObject
        {
            ["businessDate"] = "2026-10-19", ["siteId"] = "SITE-UNKNOWN",
            ["statusId"] = "STS-ABSENT"
        }, CancellationToken.None);
        Assert.Equal(0L, Assert.IsType<JsonObject>(filtered.Data)["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task AutomaticAttendance_IsCountedInStatusTotalsAndReadOnlyRegisterEvenAfterManualRevision()
    {
        // The original durable idempotency key survives an attendance revision
        // even though Source is changed to 'manual' for the latest action.
        using(var connection=_db.CreateOpenConnection())
        using(var cmd=connection.CreateCommand())
        {
            cmd.CommandText="""
                UPDATE AttendanceRecords
                SET IdempotencyKey='AUTO08H-2026-10-19-TST-P-1',
                    Source='manual',RevisionNumber=2
                WHERE PersonId='TST-P-1' AND AttendanceLocalDate='2026-10-19';
                """;
            Assert.Equal(1,await cmd.ExecuteNonQueryAsync());
        }

        var svc=new DailyMonthlyReadService(_db);
        var input=new JsonObject{["businessDate"]="2026-10-19",["siteId"]="SITE-UNSPECIFIED"};
        var summary=await svc.DashboardSummaryAsync(input,default);
        Assert.True(summary.Success,summary.UserMessage);
        var counts=Assert.IsType<JsonObject>(summary.Data);
        Assert.Equal(1L,counts["automaticallyPrepared"]!.GetValue<long>());
        Assert.Equal(0L,counts["unprepared"]!.GetValue<long>());
        var statusCounts=Assert.IsType<JsonArray>(counts["statusCounts"]);
        Assert.Equal(1L,statusCounts.OfType<JsonObject>()
            .Single(x=>x["statusId"]!.GetValue<string>()=="STS-ABSENT")
            ["count"]!.GetValue<long>());

        var automatic=await svc.DailyRosterAsync(new JsonObject{
            ["businessDate"]="2026-10-19",["siteId"]="SITE-UNSPECIFIED",
            ["statusId"]="__AUTO_PREPARED__",["term"]="930001",
            ["page"]=1,["pageSize"]=100
        },default);
        Assert.True(automatic.Success,automatic.UserMessage);
        var data=Assert.IsType<JsonObject>(automatic.Data);
        Assert.Equal(1L,data["total"]!.GetValue<long>());
        var person=Assert.Single(Assert.IsType<JsonArray>(data["items"]));
        Assert.Equal("TST-P-1",person!["personId"]!.GetValue<string>());
        Assert.Equal("غياب",person["statusName"]!.GetValue<string>());
        var noMatch=await svc.DailyRosterAsync(new JsonObject{
            ["businessDate"]="2026-10-19",["siteId"]="SITE-OTHER",
            ["statusId"]="__AUTO_PREPARED__"
        },default);
        Assert.Equal(0L,Assert.IsType<JsonObject>(noMatch.Data)["total"]!.GetValue<long>());

        using var verify=_db.CreateOpenConnection();
        using var check=verify.CreateCommand();
        check.CommandText="SELECT COUNT(*) FROM AttendanceRecords;";
        Assert.Equal(1L,Convert.ToInt64(await check.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task UnpreparedCounter_ReturnsOnlyPeopleWithoutSavedAttendance()
    {
        using (var connection=_db.CreateOpenConnection())
        using (var cmd=connection.CreateCommand())
        {
            cmd.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                    NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-P-UNPREPARED','فرد غير محضر','فرد غير محضر','12345','12345',
                    '2026-10-01','2026-10-01T07:00:00Z',1,1,1);
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TST-A-UNPREPARED','TST-P-UNPREPARED','SITE-UNSPECIFIED',
                    '2026-10-01',NULL,NULL,'2026-10-01T07:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var service=new DailyMonthlyReadService(_db);
        var date="2026-10-19";
        var summary=await service.DashboardSummaryAsync(new JsonObject{
            ["businessDate"]=date,["siteId"]="SITE-UNSPECIFIED"
        },CancellationToken.None);
        Assert.True(summary.Success,summary.UserMessage);
        var totals=Assert.IsType<JsonObject>(summary.Data);
        Assert.Equal(2L,totals["totalForce"]!.GetValue<long>());
        Assert.Equal(1L,totals["unprepared"]!.GetValue<long>());
        var unprepared=await service.DailyRosterAsync(new JsonObject{
            ["businessDate"]=date,["siteId"]="SITE-UNSPECIFIED",
            ["statusId"]="__UNPREPARED__",["page"]=1,["pageSize"]=10
        },CancellationToken.None);
        Assert.True(unprepared.Success,unprepared.UserMessage);
        var rows=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(unprepared.Data)["items"]);
        var row=Assert.Single(rows);
        Assert.Equal("TST-P-UNPREPARED",row!["personId"]!.GetValue<string>());
        Assert.Equal("غير محضر",row["statusName"]!.GetValue<string>());
        var all=await service.DailyRosterAsync(new JsonObject{
            ["businessDate"]=date,["statusId"]=""
        },CancellationToken.None);
        Assert.Equal(2L,Assert.IsType<JsonObject>(all.Data)["total"]!.GetValue<long>());
        var absence=await service.DailyRosterAsync(new JsonObject{
            ["businessDate"]=date,["statusId"]="STS-ABSENT"
        },CancellationToken.None);
        Assert.Equal(1L,Assert.IsType<JsonObject>(absence.Data)["total"]!.GetValue<long>());
        var outside=await service.DailyRosterAsync(new JsonObject{
            ["businessDate"]=date,["siteId"]="SITE-OTHER",
            ["statusId"]="__UNPREPARED__"
        },CancellationToken.None);
        Assert.Equal(0L,Assert.IsType<JsonObject>(outside.Data)["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task DashboardSearch_MatchesNameAndArabicDigitsWithCombinedFiltersWithoutWriting()
    {
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-SEARCH','فرد بحث جديد','فرد بحث جديد','12345','12345',
                    '2026-10-01','2026-10-01T07:00:00Z',1,1,1);
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                    ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TST-A-SEARCH','TST-SEARCH','SITE-UNSPECIFIED','2026-10-01',
                    NULL,NULL,'2026-10-01T07:00:00Z');
                """;
            await q.ExecuteNonQueryAsync();
        }
        var service=new DailyMonthlyReadService(_db);
        async Task<JsonObject> Run(string term,string status="",string site="SITE-UNSPECIFIED")
        {
            var result=await service.DailyRosterAsync(new JsonObject{
                ["businessDate"]="2026-10-19",["term"]=term,["statusId"]=status,
                ["siteId"]=site,["page"]=1,["pageSize"]=50
            },CancellationToken.None);
            Assert.True(result.Success,result.UserMessage);
            return Assert.IsType<JsonObject>(result.Data);
        }
        var name=await Run("شخص إختباري","STS-ABSENT");
        Assert.Equal("TST-P-1",name["items"]![0]!["personId"]!.GetValue<string>());
        Assert.Equal(1L,name["total"]!.GetValue<long>());
        foreach(var numeral in new[]{"١٢٣","۱۲۳","123"})
        {
            var found=await Run(numeral,"__UNPREPARED__");
            Assert.Equal(1L,found["total"]!.GetValue<long>());
            Assert.Equal("TST-SEARCH",found["items"]![0]!["personId"]!.GetValue<string>());
        }
        Assert.Equal(0L,(await Run("123","STS-ABSENT"))["total"]!.GetValue<long>());
        Assert.Equal(0L,(await Run("123","__UNPREPARED__","SITE-UNKNOWN"))["total"]!.GetValue<long>());
        Assert.Equal(0L,(await Run("%"))["total"]!.GetValue<long>());
        var tooLong=await service.DailyRosterAsync(new JsonObject{
            ["businessDate"]="2026-10-19",["term"]=new string('z',101)
        },CancellationToken.None);
        Assert.False(tooLong.Success);
        var dashboard=await service.DashboardSummaryAsync(new JsonObject{
            ["businessDate"]="2026-10-19",["siteId"]="SITE-UNSPECIFIED"
        },CancellationToken.None);
        Assert.Equal(2L,Assert.IsType<JsonObject>(dashboard.Data)["totalForce"]!.GetValue<long>());
        using var verify=_db.CreateOpenConnection();
        using var check=verify.CreateCommand();
        check.CommandText="SELECT COUNT(*) FROM AttendanceRecords;";
        Assert.Equal(1L,Convert.ToInt64(await check.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Monthly_IsReadOnlyAndRetainsSavedDailySnapshot()
    {
        var svc = new DailyMonthlyReadService(_db);
        var result = await svc.MonthlyGridAsync(new JsonObject
        {
            ["year"] = 2026, ["month"] = 10, ["page"] = 1, ["pageSize"] = 10
        }, CancellationToken.None);
        Assert.True(result.Success);
        var content = Assert.IsType<JsonObject>(result.Data);
        Assert.Equal(31, content["daysInMonth"]!.GetValue<int>());
        var items = Assert.IsType<JsonArray>(content["items"]);
        Assert.Single(items);
        var days = Assert.IsType<JsonObject>(items[0]!["days"]);
        Assert.Equal("غير محدد",items[0]!["siteName"]!.GetValue<string>());
        Assert.Equal("غياب", days["2026-10-19"]!["name"]!.GetValue<string>());

        var periods = await svc.ArchivePeriodsAsync(CancellationToken.None);
        Assert.Contains(Assert.IsType<JsonArray>(periods.Data),
            node => node!.GetValue<string>() == "2026-10");
    }

    [Fact]
    public async Task ArchivePeriods_IncludeRosterOnlyMonths_ExcludeClosedEndMonthAndGaps()
    {
        // No attendance in 2026-08/09. A real membership still makes the
        // complete monthly roster viewable in the archive.
        using(var connection=_db.CreateOpenConnection())
        using(var cmd=connection.CreateCommand())
        {
            cmd.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,
                    ActiveFlag,RowVersion)
                VALUES('TST-ARCHIVE-ONLY','فرد بلا تحضير','فرد بلا تحضير',
                    '99111','99111','2026-06-15','2026-06-15T09:00:00Z',4,0,1);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES('TST-ARCHIVE-ONLY','فرد بلا تحضير','99111',
                    '2026-06-15',NULL,'TST-U','2026-06-15T09:00:00Z','سجل سابق');
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                    ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TST-ARCHIVE-JUNE','TST-ARCHIVE-ONLY','SITE-UNSPECIFIED',
                    '2026-06-15','2026-08-01',NULL,'2026-06-15T09:00:00Z'),
                  ('TST-ARCHIVE-SEPT','TST-ARCHIVE-ONLY','SITE-UNSPECIFIED',
                    '2026-09-05','2026-10-01',NULL,'2026-09-05T09:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var read=new DailyMonthlyReadService(_db);
        var periods=await read.ArchivePeriodsAsync(CancellationToken.None);
        Assert.True(periods.Success,periods.UserMessage);
        var values=Assert.IsType<JsonArray>(periods.Data)
            .Select(node=>node!.GetValue<string>()).ToArray();
        foreach(var month in new[]{"2026-06","2026-07","2026-09","2026-10"})
            Assert.Contains(month,values);
        Assert.Equal(values.Length,values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(values.OrderByDescending(x=>x,StringComparer.Ordinal),values);
        // The CLOSED first-of-month boundary must not create an August cell.
        // If August is present for any other person, this person is still absent.
        var august=await read.MonthlyGridAsync(new JsonObject{
            ["year"]=2026,["month"]=8,["page"]=1,["pageSize"]=50
        },CancellationToken.None);
        Assert.True(august.Success,august.UserMessage);
        Assert.DoesNotContain(Assert.IsType<JsonArray>(((JsonObject)august.Data!)["items"]),
            x=>x!["personId"]!.GetValue<string>()=="TST-ARCHIVE-ONLY");
        var september=await read.MonthlyGridAsync(new JsonObject{
            ["year"]=2026,["month"]=9,["page"]=1,["pageSize"]=50
        },CancellationToken.None);
        Assert.True(september.Success,september.UserMessage);
        var septemberItem=Assert.Single(Assert.IsType<JsonArray>(((JsonObject)september.Data!)["items"]));
        Assert.Equal("TST-ARCHIVE-ONLY",septemberItem!["personId"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonObject>(septemberItem["days"]));
        Assert.Equal("غير محدد",septemberItem["siteName"]!.GetValue<string>());
        var detail=await read.MonthlyCellDetailsAsync(new JsonObject{
            ["personId"]="TST-ARCHIVE-ONLY",["businessDate"]="2026-09-05",
            ["siteId"]="SITE-UNSPECIFIED"
        },CancellationToken.None);
        Assert.True(detail.Success,detail.UserMessage);
        Assert.Equal("غير محدد",((JsonObject)detail.Data!)["siteName"]!.GetValue<string>());
        Assert.Null(((JsonObject)detail.Data!)["attendance"]);
        Assert.Null(((JsonObject)detail.Data!)["movement"]);
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM AttendanceRecords WHERE PersonId='TST-ARCHIVE-ONLY';";
        Assert.Equal(0L,Convert.ToInt64(await q.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task InvalidReadDate_IsRejectedWithoutMutation()
    {
        var result = await new DailyMonthlyReadService(_db).DailyRosterAsync(
            new JsonObject { ["businessDate"] = "invalid" }, CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task MonthlyCellDetails_UsesSavedStatusAndRevisionEvidence()
    {
        using (var connection = _db.CreateOpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO AttendanceRevisions(PersonId,TargetLocalDate,BeforeStatusId,
                    BeforeStatusNameSnapshot,AfterStatusId,AfterStatusNameSnapshot,
                    Reason,ActorUserId,ExecutedAtUtc,AuthorizationMethod)
                VALUES('TST-P-1','2026-10-19','STS-PRESENT','موجود','STS-ABSENT',
                    'غياب','تعديل اختباري موثق','TST-U','2026-10-19T08:00:00Z','authorized');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var svc = new DailyMonthlyReadService(_db);
        var details = await svc.MonthlyCellDetailsAsync(new JsonObject
        {
            ["personId"] = "TST-P-1", ["businessDate"] = "2026-10-19",
            ["siteId"] = "SITE-UNSPECIFIED"
        }, CancellationToken.None);
        Assert.True(details.Success, details.UserMessage);
        var data = Assert.IsType<JsonObject>(details.Data);
        Assert.Equal("930001", data["serviceNumberA"]!.GetValue<string>());
        Assert.Equal("شخص اختباري", data["personName"]!.GetValue<string>());
        var attendance = Assert.IsType<JsonObject>(data["attendance"]);
        Assert.Equal("غياب", attendance["statusName"]!.GetValue<string>());
        Assert.Equal("#EF4444", attendance["color"]!.GetValue<string>());
        Assert.Equal("2026-10-19T07:00:00Z", attendance["committedAtUtc"]!.GetValue<string>());
        var revision = Assert.Single(Assert.IsType<JsonArray>(data["revisions"]));
        Assert.Equal("تعديل اختباري موثق", revision!["reason"]!.GetValue<string>());
        Assert.Null(data["movement"]);
    }

    [Fact]
    public async Task MonthlyCellDetails_RejectsUnknownDateSiteAndPerson()
    {
        var svc = new DailyMonthlyReadService(_db);
        var badDate = await svc.MonthlyCellDetailsAsync(new JsonObject
        {
            ["personId"] = "TST-P-1", ["businessDate"] = "invalid"
        }, CancellationToken.None);
        Assert.False(badDate.Success);
        Assert.Equal(AlTamam.Contracts.Bridge.BridgeErrorCodes.ValidationError, badDate.ErrorCode);
        var foreignSite = await svc.MonthlyCellDetailsAsync(new JsonObject
        {
            ["personId"] = "TST-P-1", ["businessDate"] = "2026-10-19", ["siteId"] = "SITE-OTHER"
        }, CancellationToken.None);
        Assert.False(foreignSite.Success);
        Assert.Equal(AlTamam.Contracts.Bridge.BridgeErrorCodes.MissingData, foreignSite.ErrorCode);
        var unknown = await svc.MonthlyCellDetailsAsync(new JsonObject
        {
            ["personId"] = "TST-P-UNKNOWN", ["businessDate"] = "2026-10-19"
        }, CancellationToken.None);
        Assert.False(unknown.Success);
    }

    [Fact]
    public async Task MonthlyCellDetails_ShowsMovementWithoutInventingAttendance()
    {
        using (var connection = _db.CreateOpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,
                    ServiceNumberASnapshot,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                    DestinationExternalName,EffectiveLocalDate,CommittedAtUtc,ActorUserId,IdempotencyKey)
                VALUES('TST-M-2','TST-P-1','شخص اختباري','930001',2,'SITE-UNSPECIFIED',
                    'غير محدد','جهة خارجية','2026-10-20','2026-10-20T08:00:00Z','TST-U','TST-M-IDEM');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var result = await new DailyMonthlyReadService(_db).MonthlyCellDetailsAsync(new JsonObject
        {
            ["personId"] = "TST-P-1", ["businessDate"] = "2026-10-20"
        }, CancellationToken.None);
        Assert.True(result.Success, result.UserMessage);
        var data = Assert.IsType<JsonObject>(result.Data);
        Assert.Null(data["attendance"]);
        Assert.Empty(Assert.IsType<JsonArray>(data["revisions"]));
        var movement = Assert.IsType<JsonObject>(data["movement"]);
        Assert.Equal(2, movement["type"]!.GetValue<int>());
        Assert.Equal("جهة خارجية", movement["destinationExternalName"]!.GetValue<string>());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_file);
    }
}
