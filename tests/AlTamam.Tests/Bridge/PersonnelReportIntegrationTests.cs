using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
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
            VALUES('TST-REPORT-1','فرد التقرير','فرد التقرير','990001','990001',
              '2026-01-01','2026-01-01T08:00:00Z',1,1,1),
              ('TST-REPORT-2','فرد آخر','فرد آخر','990002','990002',
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
        Assert.Equal("990001",data["person"]!["serviceNumberA"]!.GetValue<string>());
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
    public async Task GroupReport_PreservesIndependentPersonIds_WhenPersonnelNumbersWereReused()
    {
        // A dismissed person's number is legitimately reused, but historical
        // attendance and movements must NEVER become the new person's record.
        using(var c=_db.CreateOpenConnection())
        using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                UPDATE Persons SET ActiveFlag=0,OrganizationalStatus=3
                WHERE PersonId='TST-REPORT-1';
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-REPORT-NEW','فرد جديد مستقل','فرد جديد مستقل',
                    '990001','990001','2026-02-01','2026-02-01T08:00:00Z',1,1,1);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES('TST-REPORT-NEW','فرد جديد مستقل','990001','2026-02-01',NULL,
                    'TEST','2026-02-01T08:00:00Z','إدراج مستقل');
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                    ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TST-R-NEW','TST-REPORT-NEW','SITE-UNSPECIFIED','2026-02-01',
                    NULL,NULL,'2026-02-01T08:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var svc=new PersonnelReportService(_db);
        var onlyNew=await svc.ReportAsync(new JsonObject{
            ["personIds"]=new JsonArray("TST-REPORT-NEW")
        },CancellationToken.None);
        Assert.True(onlyNew.Success,onlyNew.UserMessage);
        var newData=Assert.IsType<JsonObject>(onlyNew.Data);
        Assert.Equal("group",newData["mode"]!.GetValue<string>());
        Assert.Equal(0L,newData["attendanceTotal"]!.GetValue<long>());
        Assert.Empty(Assert.IsType<JsonArray>(newData["statusTotals"]));
        var whole=await svc.ReportAsync(new JsonObject{
            ["personIds"]=new JsonArray("TST-REPORT-NEW","TST-REPORT-1")
        },CancellationToken.None);
        Assert.True(whole.Success,whole.UserMessage);
        var content=Assert.IsType<JsonObject>(whole.Data);
        Assert.Equal(2,content["personCount"]!.GetValue<int>());
        Assert.Equal(2L,content["attendanceTotal"]!.GetValue<long>());
        var people=Assert.IsType<JsonArray>(content["persons"]);
        Assert.Equal("TST-REPORT-NEW",people[0]!["personId"]!.GetValue<string>());
        Assert.Equal(0L,people[0]!["attendanceTotal"]!.GetValue<long>());
        Assert.Equal("TST-REPORT-1",people[1]!["personId"]!.GetValue<string>());
        Assert.Equal(2L,people[1]!["attendanceTotal"]!.GetValue<long>());
        Assert.Equal("990001",people[0]!["serviceNumberA"]!.GetValue<string>());
        Assert.Equal("990001",people[1]!["serviceNumberA"]!.GetValue<string>());
        var single=await svc.ReportAsync(new JsonObject{
            ["personId"]="TST-REPORT-NEW"
        },CancellationToken.None);
        Assert.Equal(0L,Assert.IsType<JsonObject>(single.Data)["attendanceTotal"]!.GetValue<long>());
    }

    [Fact]
    public async Task GroupReport_DateRangeAndInvalidInputs_AreStrictAndReadOnly()
    {
        var svc=new PersonnelReportService(_db);
        var result=await svc.ReportAsync(new JsonObject{
            ["personIds"]=new JsonArray("TST-REPORT-1","TST-REPORT-2"),
            ["dateFrom"]="2026-01-16",["dateTo"]="2026-01-16"
        },CancellationToken.None);
        Assert.True(result.Success,result.UserMessage);
        var grouped=Assert.IsType<JsonObject>(result.Data);
        Assert.Equal(2L,grouped["attendanceTotal"]!.GetValue<long>());
        var people=Assert.IsType<JsonArray>(grouped["persons"]);
        Assert.Equal(1L,people[0]!["attendanceTotal"]!.GetValue<long>());
        Assert.Equal(1L,people[1]!["attendanceTotal"]!.GetValue<long>());
        foreach(var request in new[]{
            new JsonObject{["personIds"]=new JsonArray()},
            new JsonObject{["personIds"]=new JsonArray("TST-REPORT-1","TST-REPORT-1")},
            new JsonObject{["personIds"]=new JsonArray("TST-REPORT-1","MISSING-PERSON")},
            new JsonObject{["personIds"]=new JsonArray("TST-REPORT-1"),["dateFrom"]="2026-13-44"},
            new JsonObject{["personIds"]=new JsonArray("TST-REPORT-1"),
                ["dateFrom"]="2026-02-01",["dateTo"]="2026-01-01"}
        })
        {
            var rejected=await svc.ReportAsync(request,CancellationToken.None);
            Assert.False(rejected.Success);
        }
        using var check=_db.CreateOpenConnection();
        using var cmd=check.CreateCommand();
        cmd.CommandText="SELECT COUNT(*) FROM AttendanceRecords;";
        Assert.Equal(3L,Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task HistoricalReportFilters_ComposeDatesSitesAndStatuses_BeforeAggregating()
    {
        // Reassign P1 after the first saved attendance date. Historical site
        // filtering MUST use the event's SiteIdAtEffectiveDate, NOT today's site.
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,
                    ActiveFlag,CreatedAtUtc,RowVersion)
                VALUES('SITE-REPORT-NEW','موقع التقرير الجديد','موقع التقرير الجديد',0,
                    1,'2026-01-01T00:00:00Z',1);
                INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,
                    ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
                VALUES('SITE-REPORT-NEW','موقع التقرير الجديد','2026-01-01',
                    NULL,'TEST','2026-01-01T00:00:00Z');
                UPDATE PersonAssignments SET ValidToLocalDate='2026-01-16'
                  WHERE AssignmentId='TST-R-A1';
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                    ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TST-R-NEW-SITE','TST-REPORT-1','SITE-REPORT-NEW',
                    '2026-01-16',NULL,NULL,'2026-01-16T07:00:00Z');
                UPDATE AttendanceRecords SET
                  AssignmentIdAtEffectiveDate='TST-R-NEW-SITE',
                  SiteIdAtEffectiveDate='SITE-REPORT-NEW',
                  SiteNameSnapshot='موقع التقرير الجديد'
                WHERE PersonId='TST-REPORT-1' AND AttendanceLocalDate='2026-01-16';
                """;
            await q.ExecuteNonQueryAsync();
        }
        var svc=new PersonnelReportService(_db);
        var oldSite=await svc.ReportAsync(new JsonObject{
            ["personId"]="TST-REPORT-1",["siteId"]="SITE-UNSPECIFIED"
        },CancellationToken.None);
        Assert.True(oldSite.Success,oldSite.UserMessage);
        var oldData=Assert.IsType<JsonObject>(oldSite.Data);
        Assert.Equal(1L,oldData["attendanceTotal"]!.GetValue<long>());
        Assert.Equal("موجود",oldData["attendance"]![0]!["statusName"]!.GetValue<string>());
        Assert.Equal("2026-01-15",oldData["attendance"]![0]!["date"]!.GetValue<string>());
        Assert.Equal(2L,oldData["siteTotal"]!.GetValue<long>());
        Assert.Equal(2,Assert.IsType<JsonArray>(oldData["siteHistory"]).Count);

        var recent=await svc.ReportAsync(new JsonObject{
            ["personId"]="TST-REPORT-1",["siteId"]="SITE-REPORT-NEW",
            ["dateFrom"]="2026-01-16",["dateTo"]="2026-01-16",
            ["statusId"]="STS-ABSENT"
        },CancellationToken.None);
        Assert.True(recent.Success,recent.UserMessage);
        var data=Assert.IsType<JsonObject>(recent.Data);
        Assert.Equal(1L,data["attendanceTotal"]!.GetValue<long>());
        Assert.Equal("غياب",data["attendance"]![0]!["statusName"]!.GetValue<string>());
        Assert.Single(Assert.IsType<JsonArray>(data["statusTotals"]));
        Assert.Single(Assert.IsType<JsonArray>(data["monthlyTotals"]));

        var wrongStatus=await svc.ReportAsync(new JsonObject{
            ["personId"]="TST-REPORT-1",["siteId"]="SITE-REPORT-NEW",
            ["statusId"]="STS-PRESENT"
        },CancellationToken.None);
        Assert.Equal(0L,Assert.IsType<JsonObject>(wrongStatus.Data)["attendanceTotal"]!.GetValue<long>());

        var group=await svc.ReportAsync(new JsonObject{
            ["personIds"]=new JsonArray("TST-REPORT-1","TST-REPORT-2"),
            ["siteId"]="SITE-UNSPECIFIED",["statusId"]="STS-PRESENT"
        },CancellationToken.None);
        Assert.True(group.Success,group.UserMessage);
        var grouped=Assert.IsType<JsonObject>(group.Data);
        Assert.Equal(2L,grouped["attendanceTotal"]!.GetValue<long>());
        Assert.Equal(1L,grouped["persons"]![0]!["attendanceTotal"]!.GetValue<long>());
        Assert.Equal(1L,grouped["persons"]![1]!["attendanceTotal"]!.GetValue<long>());
        Assert.All(Assert.IsType<JsonArray>(grouped["statusTotals"]),
            x=>Assert.Equal("STS-PRESENT",x!["statusId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task HistoricalReportFilters_MovementTypeAuditValuesAndInvalidInput()
    {
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,
                    ServiceNumberASnapshot,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                    DestinationExternalName,EffectiveLocalDate,CommittedAtUtc,ActorUserId,
                    IdempotencyKey,SeparationReason)
                VALUES('TST-RPT-MOV-1','TST-REPORT-1','فرد التقرير','990001',2,
                    'SITE-UNSPECIFIED','غير محدد','شركة أخرى','2026-01-18',
                    '2026-01-18T09:00:00Z','TEST','RPT-MOV-ONE',NULL),
                      ('TST-RPT-MOV-2','TST-REPORT-1','فرد التقرير','990001',4,
                    'SITE-UNSPECIFIED','غير محدد',NULL,'2026-01-19',
                    '2026-01-19T09:00:00Z','TEST','RPT-MOV-TWO',NULL);
                """;
            await q.ExecuteNonQueryAsync();
        }
        var svc=new PersonnelReportService(_db);
        var returned=await svc.ReportAsync(new JsonObject{
            ["personId"]="TST-REPORT-1",["movementType"]=4
        },CancellationToken.None);
        Assert.True(returned.Success,returned.UserMessage);
        var data=Assert.IsType<JsonObject>(returned.Data);
        Assert.Equal(1L,data["movementTotal"]!.GetValue<long>());
        Assert.Equal("عودة للخدمة",data["movements"]![0]!["type"]!.GetValue<string>());
        var byDate=await svc.ReportAsync(new JsonObject{
            ["personIds"]=new JsonArray("TST-REPORT-1"),
            ["dateFrom"]="2026-01-18",["dateTo"]="2026-01-18",
            ["movementType"]=2,["siteId"]="SITE-UNSPECIFIED"
        },CancellationToken.None);
        Assert.True(byDate.Success,byDate.UserMessage);
        Assert.Equal(1L,Assert.IsType<JsonObject>(byDate.Data)["movementTotal"]!.GetValue<long>());
        foreach(var bad in new[]{
            new JsonObject{["personId"]="TST-REPORT-1",["movementType"]=5},
            new JsonObject{["personId"]="TST-REPORT-1",["siteId"]=new string('x',129)},
            new JsonObject{["personId"]="TST-REPORT-1",["dateFrom"]="2026-02-01",
                ["dateTo"]="2026-01-01"},
            new JsonObject{["personIds"]=new JsonArray("TST-REPORT-1"),["movementType"]=-1}
        })
            Assert.False((await svc.ReportAsync(bad,CancellationToken.None)).Success);
    }

    [Fact]
    public async Task ReportListsEveryAttendanceCorrectionFromImmutableRevisionLedger()
    {
        using(var connection=_db.CreateOpenConnection())
        using(var command=connection.CreateCommand())
        {
            command.CommandText="""
                INSERT INTO AttendanceRevisions(PersonId,TargetLocalDate,BeforeStatusId,
                    BeforeStatusNameSnapshot,AfterStatusId,AfterStatusNameSnapshot,Reason,
                    ActorUserId,ExecutedAtUtc,AuthorizationMethod)
                VALUES('TST-REPORT-1','2026-01-15','STS-ABSENT','غياب',
                    'STS-PRESENT','موجود','تصحيح أول','TEST',
                    '2026-01-15T10:00:00Z','authenticated-session-preview'),
                      ('TST-REPORT-1','2026-01-16','STS-PRESENT','موجود',
                    'STS-ABSENT','غياب','تصحيح ثان','TEST',
                    '2026-01-16T11:00:00Z','general-admin-enabled-one-time-preview');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var service=new PersonnelReportService(_db);
        var result=await service.ReportAsync(new JsonObject{["personId"]="TST-REPORT-1",
            ["siteId"]="SITE-UNSPECIFIED"},CancellationToken.None);
        Assert.True(result.Success,result.UserMessage);
        var data=Assert.IsType<JsonObject>(result.Data);
        Assert.Equal(2L,data["revisionTotal"]!.GetValue<long>());
        var revisions=Assert.IsType<JsonArray>(data["revisions"]);
        Assert.Equal(2,revisions.Count);
        Assert.Equal("تصحيح ثان",revisions[0]!["reason"]!.GetValue<string>());
        Assert.Equal("موجود",revisions[0]!["before"]!.GetValue<string>());
        Assert.Equal("غياب",revisions[0]!["after"]!.GetValue<string>());
        var filtered=await service.ReportAsync(new JsonObject{["personId"]="TST-REPORT-1",
            ["dateFrom"]="2026-01-16",["dateTo"]="2026-01-16",
            ["statusId"]="STS-PRESENT"},CancellationToken.None);
        Assert.True(filtered.Success,filtered.UserMessage);
        Assert.Equal(1L,Assert.IsType<JsonObject>(filtered.Data)["revisionTotal"]!.GetValue<long>());
        Assert.Equal(2L,data["attendanceTotal"]!.GetValue<long>());
    }

    [Fact]
    public async Task InvalidPersonIsNeverReplacedWithAnUnrelatedIdentity()
    {
        var result=await new PersonnelReportService(_db).ReportAsync(
            new JsonObject{["personId"]="TST-REPORT-NOT-FOUND"},CancellationToken.None);
        Assert.False(result.Success);
    }

    private const string ReportNonce = "77777777777777777777777777777777";
    private static readonly BridgePrincipal ReportPrincipal = new(
        "TST-REPORT-ROLE", "test-report-user", UserRole.Supervisor, ReportNonce);

    private Task<BridgeDispatchResult> DispatchReportAlias(
        string action, JsonObject payload, string nonce = ReportNonce)
    {
        var request = new JsonObject
        {
            ["requestId"] = "TST-REPORT-ALIAS-" + Guid.NewGuid().ToString("N"),
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = action,
            ["sessionNonce"] = nonce,
            ["payload"] = payload
        };
        var dispatcher = BridgeHandlerCatalog.Create(new BridgeSharedServices(_db));
        return dispatcher.DispatchAsync(request.ToJsonString(), LocalWebUiOrigin.BaseUrl,
            true, n => Task.FromResult<BridgePrincipal?>(n == ReportNonce ? ReportPrincipal : null));
    }

    [Theory]
    [InlineData("QueryPersonAttendanceTimeline")]
    [InlineData("QueryPersonAttendanceTotals")]
    [InlineData("QueryPersonOperationsTimeline")]
    [InlineData("QueryPersonMovements")]
    public async Task LegacyReadContracts_UseTheOneCanonicalImmutablePersonReport(string action)
    {
        var filters = new JsonObject
        {
            ["personId"] = "TST-REPORT-1",
            ["dateFrom"] = "2026-01-16",
            ["dateTo"] = "2026-01-16",
            ["siteId"] = "SITE-UNSPECIFIED"
        };
        var expected = await new PersonnelReportService(_db)
            .ReportAsync((JsonObject)filters.DeepClone(), CancellationToken.None);
        Assert.True(expected.Success, expected.UserMessage);

        var response = await DispatchReportAlias(action, (JsonObject)filters.DeepClone());
        Assert.Equal(BridgeOutcomeKind.Success, response.Kind);
        var data = Assert.IsType<JsonObject>(response.Response.Data);
        var canonical = Assert.IsType<JsonObject>(expected.Data);
        // No competing report SQL/schema: the aliases return the exact same
        // effective-dated PersonId projection as BuildPersonLifetimeReport.
        Assert.Equal(canonical.ToJsonString(), data.ToJsonString());
        Assert.Equal("TST-REPORT-1", data["person"]!["personId"]!.GetValue<string>());
        Assert.Equal(1L, data["attendanceTotal"]!.GetValue<long>());
        Assert.Equal(0L, data["movementTotal"]!.GetValue<long>());

        using var connection = _db.CreateOpenConnection();
        using var attendance = connection.CreateCommand();
        attendance.CommandText = "SELECT COUNT(*) FROM AttendanceRecords;";
        Assert.Equal(3L, Convert.ToInt64(await attendance.ExecuteScalarAsync()));
        using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT COUNT(*) FROM AuditEvents;";
        Assert.Equal(0L, Convert.ToInt64(await audit.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData("QueryPersonAttendanceTimeline")]
    [InlineData("QueryPersonAttendanceTotals")]
    [InlineData("QueryPersonOperationsTimeline")]
    [InlineData("QueryPersonMovements")]
    public async Task LegacyReadContracts_DoNotBypassSessionOrFabricateMissingPerson(string action)
    {
        var payload = new JsonObject { ["personId"] = "TST-REPORT-1" };
        var unauthenticated = await DispatchReportAlias(action, (JsonObject)payload.DeepClone(),
            "ffffffffffffffffffffffffffffffff");
        Assert.Equal(BridgeOutcomeKind.Unauthorized, unauthenticated.Kind);

        var missing = await DispatchReportAlias(action,
            new JsonObject { ["personId"] = "TST-REPORT-NOT-FOUND" });
        Assert.False(missing.Response.Success);
        Assert.Equal(BridgeErrorCodes.MissingData, missing.Response.ErrorCode);

        var invalid = await DispatchReportAlias(action,
            new JsonObject { ["personId"] = "TST-REPORT-1",
                ["dateFrom"] = "2026-02-01", ["dateTo"] = "2026-01-01" });
        Assert.False(invalid.Response.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError, invalid.Response.ErrorCode);
    }


    [Fact]
    public async Task Phase09_GroupDailyStatusSummary_PreservesSeparatePeopleAndActualDates()
    {
        var result=await new PersonnelReportService(_db).ReportAsync(new JsonObject
        {
            ["personIds"]=new JsonArray("TST-REPORT-1","TST-REPORT-2"),
            ["dateFrom"]="2026-01-15",["dateTo"]="2026-01-16"
        },CancellationToken.None);
        Assert.True(result.Success,result.UserMessage);
        var data=Assert.IsType<JsonObject>(result.Data);
        var daily=Assert.IsType<JsonArray>(data["dailyStatusTotals"]);
        Assert.Equal(3,daily.Count);
        Assert.Equal(3L,data["attendanceTotal"]!.GetValue<long>());
        Assert.Equal(3L,daily.Sum(x=>x!["count"]!.GetValue<long>()));
        Assert.Contains(daily,x=>x!["date"]!.GetValue<string>()=="2026-01-15" &&
            x["statusName"]!.GetValue<string>()=="موجود" && x["count"]!.GetValue<long>()==1);
        Assert.Contains(daily,x=>x!["date"]!.GetValue<string>()=="2026-01-16" &&
            x["statusName"]!.GetValue<string>()=="غياب" && x["count"]!.GetValue<long>()==1);
        Assert.Contains(daily,x=>x!["date"]!.GetValue<string>()=="2026-01-16" &&
            x["statusName"]!.GetValue<string>()=="موجود" && x["count"]!.GetValue<long>()==1);
        Assert.False(data["dailyStatusHasNext"]!.GetValue<bool>());
        var second=await new PersonnelReportService(_db).ReportAsync(new JsonObject
        {
            ["personIds"]=new JsonArray("TST-REPORT-1","TST-REPORT-2"),
            ["dateFrom"]="2026-01-16",["dateTo"]="2026-01-16"
        },CancellationToken.None);
        Assert.True(second.Success,second.UserMessage);
        var selected=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(second.Data)["dailyStatusTotals"]);
        Assert.All(selected,x=>Assert.Equal("2026-01-16",x!["date"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Phase09_GroupDailyStatusSummary_PaginatesWithoutLosingFacts()
    {
        using(var conn=_db.CreateOpenConnection())
        using(var transaction=conn.BeginTransaction())
        {
            using var add=conn.CreateCommand(); add.Transaction=transaction;
            add.CommandText="""
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                    StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                    AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                    Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
                VALUES('TST-REPORT-1',$date,'STS-PRESENT','موجود','م','#38BDF8',
                    'TST-R-A1','SITE-UNSPECIFIED','غير محدد','manual','TEST',
                    $created,$key);
                """;
            for(int i=0;i<120;i++)
            {
                var date=new DateOnly(2026,3,1).AddDays(i).ToString("yyyy-MM-dd");
                add.Parameters.Clear();
                add.Parameters.AddWithValue("$date",date);
                add.Parameters.AddWithValue("$created",date+"T08:00:00Z");
                add.Parameters.AddWithValue("$key","P09-PAGED-"+i);
                await add.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        var svc=new PersonnelReportService(_db);
        long total=0;int pages=0;
        for(int page=1;page<=5;page++)
        {
            var response=await svc.ReportAsync(new JsonObject
            {
                ["personIds"]=new JsonArray("TST-REPORT-1","TST-REPORT-2"),
                ["summaryPage"]=page
            },CancellationToken.None);
            Assert.True(response.Success,response.UserMessage);
            var data=Assert.IsType<JsonObject>(response.Data);
            total+=Assert.IsType<JsonArray>(data["dailyStatusTotals"]).Sum(x=>x!["count"]!.GetValue<long>());
            pages++;
            if(!data["dailyStatusHasNext"]!.GetValue<bool>())break;
        }
        Assert.True(pages>=2);
        Assert.Equal(123L,total);
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
