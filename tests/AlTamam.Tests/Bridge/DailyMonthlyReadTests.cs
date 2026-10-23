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
            VALUES ('TST-P-1','شخص اختباري','شخص اختباري','X001','X001',
                    '2026-10-01','2026-10-01T07:00:00Z',1,1,1);
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
        Assert.Equal("X001", rows[0]!["serviceNumberA"]!.GetValue<string>());
        var filtered = await svc.DailyRosterAsync(new JsonObject
        {
            ["businessDate"] = "2026-10-19", ["siteId"] = "SITE-UNKNOWN",
            ["statusId"] = "STS-ABSENT"
        }, CancellationToken.None);
        Assert.Equal(0L, Assert.IsType<JsonObject>(filtered.Data)["total"]!.GetValue<long>());
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
        Assert.Equal("غياب", days["2026-10-19"]!["name"]!.GetValue<string>());

        var periods = await svc.ArchivePeriodsAsync(CancellationToken.None);
        Assert.Contains(Assert.IsType<JsonArray>(periods.Data),
            node => node!.GetValue<string>() == "2026-10");
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
        Assert.Equal("X001", data["serviceNumberA"]!.GetValue<string>());
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
                VALUES('TST-M-2','TST-P-1','شخص اختباري','X001',2,'SITE-UNSPECIFIED',
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
