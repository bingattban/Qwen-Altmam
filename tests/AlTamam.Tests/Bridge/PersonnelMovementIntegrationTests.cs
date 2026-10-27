using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class PersonnelMovementIntegrationTests:IAsyncLifetime,IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal Actor=new("TST-TRANSFER-ADMIN","tester",
        UserRole.GeneralAdministration,"afeeaabbaabbaabbaabbaabbaabbaabb");
    private static string Today()=>TimeProvider.System.GetLocalNow().ToString("yyyy-MM-dd");
    private static string Yesterday()=>DateOnly.Parse(Today()).AddDays(-1).ToString("yyyy-MM-dd");
    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        using(var setup=c.CreateCommand())
        {
            setup.CommandText="""
                INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
                VALUES('SITE-DEST','الموقع الجديد','الموقع الجديد',0,1,'2026-01-01T00:00:00Z',1);
                """;
            await setup.ExecuteNonQueryAsync();
        }
        for(int i=1;i<=3;i++)
        {
            using var cmd=c.CreateCommand();
            cmd.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                    NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($id,$name,$name,$number,$number,$start,$now,1,1,1);
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES($assignment,$id,'SITE-UNSPECIFIED',$start,NULL,NULL,$now);
                """;
            cmd.Parameters.AddWithValue("$id","TST-MOVEMENT-P"+i);
            cmd.Parameters.AddWithValue("$name","فرد تجريبي "+i);
            cmd.Parameters.AddWithValue("$number","MOVEMENT-"+i);
            cmd.Parameters.AddWithValue("$assignment","TST-MOVEMENT-A"+i);
            cmd.Parameters.AddWithValue("$start",Yesterday());
            cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static JsonObject Plan(string[] people,bool external=false)=>new()
    {
        ["businessDate"]=Today(),["sourceSiteId"]="SITE-UNSPECIFIED",
        ["destinationSiteId"]=external?null:"SITE-DEST",
        ["destinationExternalName"]=external?"جهة خارجية معتمدة":null,
        ["personIds"]=new JsonArray(people.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray())
    };
    private static string Token(BridgeResponse response)
    {
        Assert.True(response.Success,response.UserMessage);
        return Assert.IsType<JsonObject>(response.Data)["previewToken"]!.GetValue<string>();
    }
    private long Count(string table)
    {
        using var c=_db.CreateOpenConnection();using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM "+table;
        return Convert.ToInt64(q.ExecuteScalar());
    }

    [Fact]
    public async Task MovementArchive_CombinedSiteAndDateFilters_ReturnOnlyMatchingMovements()
    {
        var svc = new PersonnelMovementService(_db);
        var first = await svc.PreviewAsync(Plan(["TST-MOVEMENT-P1"]), Actor, false, CancellationToken.None);
        var saved = await svc.CommitAsync(new JsonObject { ["previewToken"] = Token(first) },
            Actor, false, CancellationToken.None);
        Assert.True(saved.Success, saved.UserMessage);

        var second = await svc.PreviewAsync(Plan(["TST-MOVEMENT-P2"], external: true),
            Actor, true, CancellationToken.None);
        var exited = await svc.CommitAsync(new JsonObject { ["previewToken"] = Token(second) },
            Actor, true, CancellationToken.None);
        Assert.True(exited.Success, exited.UserMessage);

        async Task<JsonObject> Archive(string site, string type, string from, string to)
        {
            var response = await svc.ArchiveAsync(new JsonObject {
                ["siteId"] = site, ["type"] = type, ["dateFrom"] = from,
                ["dateTo"] = to, ["page"] = 1, ["pageSize"] = 50
            }, CancellationToken.None);
            Assert.True(response.Success, response.UserMessage);
            return Assert.IsType<JsonObject>(response.Data);
        }

        string today = Today();
        var destination = await Archive("SITE-DEST", "internal", today, today);
        Assert.Equal(1L, destination["total"]!.GetValue<long>());
        var internalRow = Assert.IsType<JsonObject>(Assert.Single(
            Assert.IsType<JsonArray>(destination["items"])));
        Assert.Equal("TST-MOVEMENT-P1", internalRow["personId"]!.GetValue<string>());
        var source = await Archive("SITE-UNSPECIFIED", "external", today, today);
        Assert.Equal(1L, source["total"]!.GetValue<long>());
        Assert.Equal(0L, (await Archive("SITE-DEST", "external", today, today))["total"]!.GetValue<long>());
        Assert.Equal(0L, (await Archive("", "", Yesterday(), Yesterday()))["total"]!.GetValue<long>());
        var local=DateOnly.Parse(today);
        var monthly=await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject{
            ["year"]=local.Year,["month"]=local.Month,
            ["siteId"]="SITE-UNSPECIFIED",["archiveMode"]=true,
            ["page"]=1,["pageSize"]=50
        },CancellationToken.None);
        Assert.True(monthly.Success,monthly.UserMessage);
        var people=Assert.IsType<JsonArray>(((JsonObject)monthly.Data!)["items"]);
        var externalPerson=people.OfType<JsonObject>().Single(p=>
            p["personId"]!.GetValue<string>()=="TST-MOVEMENT-P2");
        var movedDay=Assert.IsType<JsonObject>(((JsonObject)externalPerson["days"]!)[today]);
        Assert.Equal("نقل (خ)",movedDay["symbol"]!.GetValue<string>());
        Assert.StartsWith("نقل (خ)",movedDay["name"]!.GetValue<string>());
        var summary=await new DailyMonthlyReadService(_db).DashboardSummaryAsync(
            new JsonObject{["businessDate"]=today,["siteId"]=""},CancellationToken.None);
        Assert.True(summary.Success,summary.UserMessage);
        // P1 moved internally and P3 stayed active; external P2 is no longer force.
        Assert.Equal(2L,((JsonObject)summary.Data!)["totalForce"]!.GetValue<long>());
        var personReader=new PersonRosterService(_db);
        var operational=await personReader.QueryPersonsAsync(new JsonObject{
            ["page"]=1,["pageSize"]=50
        },default);
        Assert.True(operational.Success,operational.UserMessage);
        var activeData=Assert.IsType<JsonObject>(operational.Data);
        Assert.Equal(2L,activeData["total"]!.GetValue<long>());
        Assert.DoesNotContain(Assert.IsType<JsonArray>(activeData["items"]).OfType<JsonObject>(),
            p=>p["personId"]!.GetValue<string>()=="TST-MOVEMENT-P2");
        var history=await personReader.QueryPersonsAsync(new JsonObject{
            ["includeArchived"]=true,["page"]=1,["pageSize"]=50
        },default);
        Assert.True(history.Success,history.UserMessage);
        Assert.Equal(3L,Assert.IsType<JsonObject>(history.Data)["total"]!.GetValue<long>());
        var liveMonth=await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject{
            ["year"]=local.Year,["month"]=local.Month,["page"]=1,["pageSize"]=50
        },default);
        Assert.True(liveMonth.Success,liveMonth.UserMessage);
        var liveData=Assert.IsType<JsonObject>(liveMonth.Data);
        Assert.Equal(2L,liveData["total"]!.GetValue<long>());
        Assert.DoesNotContain(Assert.IsType<JsonArray>(liveData["items"]).OfType<JsonObject>(),
            p=>p["personId"]!.GetValue<string>()=="TST-MOVEMENT-P2");
    }

    [Fact]
    public async Task InternalBulkTransfer_EffectiveDatesAndAudit_OneTime()
    {
        var svc=new PersonnelMovementService(_db);
        var pre=await svc.PreviewAsync(Plan(["TST-MOVEMENT-P1","TST-MOVEMENT-P2"]),
            Actor,false,CancellationToken.None);
        string token=Token(pre);
        var commit=await svc.CommitAsync(new JsonObject{["previewToken"]=token},Actor,false,CancellationToken.None);
        Assert.True(commit.Success,commit.UserMessage);
        Assert.Equal(2L,Count("PersonnelMovements"));
        Assert.Equal(2L,Count("AuditEvents"));
        var month=new DailyMonthlyReadService(_db);
        // Internal transfers remain in current active strength and the monthly
        // grid at their new location; archived source-site history is unchanged.
        async Task<long> ActiveForce(string site)
        {
            var response=await month.DashboardSummaryAsync(new JsonObject{
                ["businessDate"]=Today(),["siteId"]=site
            },CancellationToken.None);
            Assert.True(response.Success,response.UserMessage);
            return ((JsonObject)response.Data!)["totalForce"]!.GetValue<long>();
        }
        Assert.Equal(3L,await ActiveForce(""));
        Assert.Equal(1L,await ActiveForce("SITE-UNSPECIFIED"));
        Assert.Equal(2L,await ActiveForce("SITE-DEST"));
        DateTime now=DateTime.Now;
        foreach(var (location,expected) in new[] {
            ("SITE-UNSPECIFIED","تم نقله إلى"),
            ("SITE-DEST","تم نقله من")
        })
        {
            var monthly=await month.MonthlyGridAsync(new JsonObject{
                ["year"]=now.Year,["month"]=now.Month,["siteId"]=location,
                ["archiveMode"]=true,["page"]=1,["pageSize"]=50
            },CancellationToken.None);
            Assert.True(monthly.Success,monthly.UserMessage);
            var items=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(monthly.Data)["items"]);
            Assert.Equal(location=="SITE-UNSPECIFIED"?3:2,items.Count);
            int transferCells=0;
            foreach(var item in items)
            {
                var days=Assert.IsType<JsonObject>(item!["days"]);
                var transferCell=days[Today()] as JsonObject;
                if(transferCell is null)continue; // The third person stays at the source site.
                transferCells++;
                Assert.Equal("movement",transferCell["kind"]!.GetValue<string>());
                Assert.Contains(expected,transferCell["name"]!.GetValue<string>());
                Assert.Equal("نقل (د)",transferCell["symbol"]!.GetValue<string>());
            }
            Assert.Equal(2,transferCells);
            var live=await month.MonthlyGridAsync(new JsonObject{
                ["year"]=now.Year,["month"]=now.Month,["siteId"]=location,
                ["page"]=1,["pageSize"]=50
            },CancellationToken.None);
            Assert.True(live.Success,live.UserMessage);
            var operational=(JsonObject)live.Data!;
            Assert.Equal(location=="SITE-UNSPECIFIED"?1L:2L,
                operational["total"]!.GetValue<long>());
            // At the source site only the person who did NOT move stays in the
            // operational roster. The target site contains both active movers.
            var liveRows=Assert.IsType<JsonArray>(operational["items"]);
            if(location=="SITE-DEST")
            {
                Assert.All(liveRows.OfType<JsonObject>(),p=>
                    Assert.Equal("نقل (د)",((JsonObject)p["days"]!)[Today()]!["symbol"]!.GetValue<string>()));
            }
        }
        var archivePeriods=await month.ArchivePeriodsAsync(CancellationToken.None);
        var archiveMonths=Assert.IsType<JsonArray>(archivePeriods.Data)
            .Select(x=>x!.GetValue<string>()).ToArray();
        Assert.Contains(DateTime.Now.ToString("yyyy-MM"),archiveMonths);
        // Internal transfer day has no attendance, but its origin and
        // destination are both independently documented for this PersonId.
        foreach(var location in new[]{"SITE-UNSPECIFIED","SITE-DEST"})
        {
            var historic=await month.MonthlyCellDetailsAsync(new JsonObject{
                ["personId"]="TST-MOVEMENT-P1",["businessDate"]=Today(),
                ["siteId"]=location
            },CancellationToken.None);
            Assert.True(historic.Success,historic.UserMessage);
            var details=Assert.IsType<JsonObject>(historic.Data);
            Assert.Null(details["attendance"]);
            var movement=Assert.IsType<JsonObject>(details["movement"]);
            Assert.Equal(1,movement["type"]!.GetValue<int>());
            Assert.Equal("SITE-UNSPECIFIED",movement["sourceSiteId"]!.GetValue<string>());
            Assert.Equal("SITE-DEST",movement["destinationSiteId"]!.GetValue<string>());
        }
        using(var c=_db.CreateOpenConnection())
        {
            using var cmd=c.CreateCommand();
            cmd.CommandText="""
                SELECT COUNT(*) FROM PersonAssignments
                WHERE SiteId='SITE-UNSPECIFIED' AND ValidToLocalDate=$date;
                """;
            cmd.Parameters.AddWithValue("$date",Today());
            Assert.Equal(2L,Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText="""
                SELECT COUNT(*) FROM PersonAssignments
                WHERE SiteId='SITE-DEST' AND ValidFromLocalDate=$date AND ValidToLocalDate IS NULL;
                """;
            Assert.Equal(2L,Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText="SELECT COUNT(*) FROM AttendanceRecords;";
            Assert.Equal(0L,Convert.ToInt64(cmd.ExecuteScalar()));
        }
        var replay=await svc.CommitAsync(new JsonObject{["previewToken"]=token},Actor,false,CancellationToken.None);
        Assert.False(replay.Success);
        Assert.Equal(2L,Count("PersonnelMovements"));
        var archive=await svc.ArchiveAsync(new JsonObject{["type"]="internal"},CancellationToken.None);
        Assert.Equal(2L,Assert.IsType<JsonObject>(archive.Data)["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task IdenticalInternalTransferRetry_ReturnsRecordedResultWithoutExtraMovementsOrAudit()
    {
        var svc=new PersonnelMovementService(_db);
        string token=Token(await svc.PreviewAsync(Plan(["TST-MOVEMENT-P1","TST-MOVEMENT-P2"]),
            Actor,false,CancellationToken.None));
        const string operationId="transfer-retry-intent-20261025";
        var request=new JsonObject{["previewToken"]=token,["operationId"]=operationId};
        var committed=await svc.CommitAsync(request,Actor,false,CancellationToken.None);
        Assert.True(committed.Success,committed.UserMessage);
        Assert.Equal(2L,Count("PersonnelMovements"));
        Assert.Equal(2L,Count("AuditEvents"));
        Assert.Equal(1L,Count("MutationReceipts"));

        // Simulates UI retry after the first success response was lost.
        var replay=await svc.CommitAsync((JsonObject)request.DeepClone(),Actor,false,CancellationToken.None);
        Assert.True(replay.Success,replay.UserMessage);
        Assert.Equal(Assert.IsType<JsonObject>(committed.Data).ToJsonString(),
            Assert.IsType<JsonObject>(replay.Data).ToJsonString());
        Assert.Equal(2L,Count("PersonnelMovements"));
        Assert.Equal(2L,Count("AuditEvents"));
        Assert.Equal(1L,Count("MutationReceipts"));

        // Operation IDs cannot be used to swap a different preview or action.
        var changed=await svc.CommitAsync(new JsonObject{
            ["previewToken"]="0000000000000000000000000000000000000000000000000000000000000000",
            ["operationId"]=operationId
        },Actor,false,CancellationToken.None);
        Assert.False(changed.Success);
        var wrongAction=await svc.CommitAsync((JsonObject)request.DeepClone(),
            Actor,true,CancellationToken.None);
        Assert.False(wrongAction.Success);
        Assert.Equal(2L,Count("PersonnelMovements"));
        Assert.Equal(2L,Count("AuditEvents"));
    }

    [Fact]
    public async Task FailedTransferValidation_DoesNotReserveOperationId_AndActorCannotReplayAnotherUser()
    {
        var svc=new PersonnelMovementService(_db);
        const string operationId="transfer-valid-after-failed-preview";
        var invalid=await svc.CommitAsync(new JsonObject{
            ["previewToken"]="0000000000000000000000000000000000000000000000000000000000000000",
            ["operationId"]=operationId
        },Actor,false,CancellationToken.None);
        Assert.False(invalid.Success);
        Assert.Equal(0L,Count("MutationReceipts"));

        string token=Token(await svc.PreviewAsync(Plan(["TST-MOVEMENT-P3"]),
            Actor,false,CancellationToken.None));
        var request=new JsonObject{["previewToken"]=token,["operationId"]=operationId};
        var success=await svc.CommitAsync(request,Actor,false,CancellationToken.None);
        Assert.True(success.Success,success.UserMessage);
        var anotherActor=Actor with {UserId="TST-OTHER-ADMIN"};
        var crossUser=await svc.CommitAsync(request,anotherActor,false,CancellationToken.None);
        Assert.False(crossUser.Success);
        Assert.Equal(1L,Count("PersonnelMovements"));
        Assert.Equal(1L,Count("AuditEvents"));
        Assert.Equal(1L,Count("MutationReceipts"));
    }

    [Fact]
    public async Task ExternalTransfer_RetriesAreAtomicAndNeverReactivateArchivedPerson()
    {
        var svc=new PersonnelMovementService(_db);
        string token=Token(await svc.PreviewAsync(Plan(["TST-MOVEMENT-P3"],true),
            Actor,true,CancellationToken.None));
        var request=new JsonObject{["previewToken"]=token,
            ["operationId"]="external-transfer-retry-20261025"};
        Assert.True((await svc.CommitAsync(request,Actor,true,CancellationToken.None)).Success);
        Assert.True((await svc.CommitAsync(request,Actor,true,CancellationToken.None)).Success);
        Assert.Equal(1L,Count("PersonnelMovements"));
        Assert.Equal(1L,Count("AuditEvents"));
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT ActiveFlag FROM Persons WHERE PersonId='TST-MOVEMENT-P3';";
        Assert.Equal(0L,Convert.ToInt64(q.ExecuteScalar()));
    }

    [Fact]
    public async Task ExternalTransfer_DisablesLinkedAccountRevokesSessionsAndKeepsImmutableAudit()
    {
        var accounts=new UserAccountManagementService(_db);
        var created=await accounts.CreateAsync(new JsonObject{
            ["username"]="external-linked",["password"]="Abc123!secure",
            ["passwordConfirmation"]="Abc123!secure",
            ["personId"]="TST-MOVEMENT-P3",["role"]="Supervisor"
        },Actor,default);
        Assert.True(created.Success,created.UserMessage);
        string accountId=Assert.IsType<JsonObject>(created.Data)["userId"]!.GetValue<string>();
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO UserSessions(SessionNonce,UserId,Role,IssuedAtUtc,ExpiresAtUtc,RevokedFlag)
                VALUES('11223344556677889900aabbccddeeff',$id,3,
                    '2026-10-25T00:00:00Z','2028-10-25T00:00:00Z',0);
                """;
            q.Parameters.AddWithValue("$id",accountId);q.ExecuteNonQuery();
        }
        var movements=new PersonnelMovementService(_db);
        string token=Token(await movements.PreviewAsync(Plan(["TST-MOVEMENT-P3"],true),
            Actor,true,default));
        var request=new JsonObject{
            ["previewToken"]=token,["operationId"]="external-account-disable-20261025"
        };
        Assert.True((await movements.CommitAsync(request,Actor,true,default)).Success);
        Assert.True((await movements.CommitAsync(request,Actor,true,default)).Success);
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT ActiveFlag FROM Users WHERE UserId=$id;";
            q.Parameters.AddWithValue("$id",accountId);
            Assert.Equal(0L,Convert.ToInt64(q.ExecuteScalar()));
            q.CommandText="SELECT RevokedFlag FROM UserSessions WHERE UserId=$id;";
            Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
            q.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='USER_LIFECYCLE_SUSPEND';";
            Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
            q.CommandText="SELECT COUNT(*) FROM PersonnelMovements WHERE MovementType=2;";
            Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
            // Returning the person to the active flags NEVER silently reactivates an account.
            q.CommandText="""
                UPDATE Persons SET ActiveFlag=1,OrganizationalStatus=1
                WHERE PersonId='TST-MOVEMENT-P3';
                """;
            q.ExecuteNonQuery();
            q.CommandText="SELECT ActiveFlag FROM Users WHERE UserId=$id;";
            Assert.Equal(0L,Convert.ToInt64(q.ExecuteScalar()));
        }
    }

    [Fact]
    public async Task IndependentFounder_RemainsActiveWhenDifferentRosterPersonTransfersExternally()
    {
        var (hash,salt)=AlTamam.Infrastructure.Security.PasswordSecurity.HashPassword("Abc123!secure");
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
                    CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,
                    FailedLoginCount,CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,RowVersion)
                VALUES('TST-FOUNDER',NULL,'founder','founder',1,
                    $hash,$salt,0,1,0,'TST-FOUNDER','founder','2026-01-01T00:00:00Z',1);
                """;
            q.Parameters.AddWithValue("$hash",hash);q.Parameters.AddWithValue("$salt",salt);
            q.ExecuteNonQuery();
        }
        var svc=new PersonnelMovementService(_db);
        var preview=await svc.PreviewAsync(Plan(["TST-MOVEMENT-P3"],true),Actor,true,default);
        var result=await svc.CommitAsync(new JsonObject{
            ["previewToken"]=Token(preview)
        },Actor,true,default);
        Assert.True(result.Success,result.UserMessage);
        Assert.Equal(1L,Count("PersonnelMovements"));
        Assert.True(Count("AuditEvents")>=1L);
        using var check=_db.CreateOpenConnection();using var cmd=check.CreateCommand();
        cmd.CommandText="SELECT ActiveFlag FROM Persons WHERE PersonId='TST-MOVEMENT-P3';";
        Assert.Equal(0L,Convert.ToInt64(cmd.ExecuteScalar()));
        cmd.CommandText="SELECT ActiveFlag FROM Users WHERE UserId='TST-FOUNDER';";
        Assert.Equal(1L,Convert.ToInt64(cmd.ExecuteScalar()));
        cmd.CommandText="SELECT LinkedPersonId FROM Users WHERE UserId='TST-FOUNDER';";
        Assert.Equal(DBNull.Value,cmd.ExecuteScalar());
    }

    [Fact]
    public async Task ExternalTransferRemovesActiveRosterButPreservesArchive()
    {
        var svc=new PersonnelMovementService(_db);
        var preview=await svc.PreviewAsync(Plan(["TST-MOVEMENT-P3"],true),
            Actor,true,CancellationToken.None);
        var result=await svc.CommitAsync(new JsonObject{["previewToken"]=Token(preview)},
            Actor,true,CancellationToken.None);
        Assert.True(result.Success,result.UserMessage);
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
            SELECT OrganizationalStatus,ActiveFlag FROM Persons WHERE PersonId='TST-MOVEMENT-P3';
            """;
        using(var read=await cmd.ExecuteReaderAsync())
        {
            Assert.True(await read.ReadAsync());
            Assert.Equal(2,read.GetInt32(0));
            Assert.Equal(0,read.GetInt32(1));
        }
        cmd.CommandText="SELECT COUNT(*) FROM PersonnelMovements WHERE MovementType=2;";
        Assert.Equal(1L,Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        var archive=await svc.ArchiveAsync(new JsonObject{["type"]="external"},CancellationToken.None);
        Assert.Equal(1L,Assert.IsType<JsonObject>(archive.Data)["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task PreparedPeopleCanTransfer_WithoutLosingAttendanceOrMonthlyHistory()
    {
        var svc=new PersonnelMovementService(_db);
        var unknown=await svc.PreviewAsync(Plan(["TST-MOVEMENT-P1","UNKNOWN"]),
            Actor,false,CancellationToken.None);
        Assert.False(unknown.Success);
        Assert.Equal(0L,Count("PersonnelMovements"));

        using(var c=_db.CreateOpenConnection())
        using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,StatusNameSnapshot,
                    StatusSymbolSnapshot,StatusColorSnapshot,AssignmentIdAtEffectiveDate,
                    SiteIdAtEffectiveDate,SiteNameSnapshot,Source,CreatedByUserId,
                    CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES('TST-MOVEMENT-P1',$day,'STS-PRESENT','موجود','م','#38BDF8',
                    'TST-MOVEMENT-A1','SITE-UNSPECIFIED','غير محدد','manual',
                    'TST-TRANSFER-ADMIN','2026-10-23T00:00:00Z',1,'TEST-ATTEND-MOVE');
                """;
            cmd.Parameters.AddWithValue("$day",Today());
            await cmd.ExecuteNonQueryAsync();
        }

        var preview=await svc.PreviewAsync(Plan(["TST-MOVEMENT-P1","TST-MOVEMENT-P2"]),
            Actor,false,CancellationToken.None);
        var committed=await svc.CommitAsync(new JsonObject{["previewToken"]=Token(preview)},
            Actor,false,CancellationToken.None);
        Assert.True(committed.Success,committed.UserMessage);
        Assert.Equal(2L,Count("PersonnelMovements"));
        Assert.Equal(1L,Count("AttendanceRecords"));

        var today=DateOnly.Parse(Today());
        var monthly=await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject{
            ["year"]=today.Year,["month"]=today.Month,["siteId"]="",
            ["page"]=1,["pageSize"]=50
        },default);
        Assert.True(monthly.Success,monthly.UserMessage);
        var items=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(monthly.Data)["items"]);
        Assert.Equal(3,items.Count);
        var destination=await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject{
            ["year"]=today.Year,["month"]=today.Month,["siteId"]="SITE-DEST",
            ["page"]=1,["pageSize"]=50
        },default);
        Assert.True(destination.Success,destination.UserMessage);
        var atDestination=Assert.IsType<JsonArray>(
            Assert.IsType<JsonObject>(destination.Data)["items"]);
        Assert.Equal(2,atDestination.Count);
        var newSiteCell=Assert.IsType<JsonObject>(((JsonObject)Assert.Single(
            atDestination.OfType<JsonObject>(),p=>
                p["personId"]!.GetValue<string>()=="TST-MOVEMENT-P1")["days"]!)[Today()]);
        Assert.Equal("نقل (د)",newSiteCell["symbol"]!.GetValue<string>());
        var attended=Assert.Single(items.OfType<JsonObject>(),p=>
            p["personId"]!.GetValue<string>()=="TST-MOVEMENT-P1");
        var cell=Assert.IsType<JsonObject>(((JsonObject)attended["days"]!)[Today()]);
        Assert.Equal("م",cell["symbol"]!.GetValue<string>());
        Assert.Equal("internal-transfer",cell["movementKind"]!.GetValue<string>());
        Assert.Contains("نقل (د)",cell["movementNote"]!.GetValue<string>());
        var details=await new DailyMonthlyReadService(_db).MonthlyCellDetailsAsync(new JsonObject{
            ["personId"]="TST-MOVEMENT-P1",["businessDate"]=Today()
        },default);
        Assert.True(details.Success,details.UserMessage);
        Assert.NotNull(Assert.IsType<JsonObject>(details.Data)["attendance"]);
        Assert.NotNull(Assert.IsType<JsonObject>(details.Data)["movement"]);

        // One person cannot have a second same-day movement despite the
        // absence of a restriction on their attendance or status.
        var duplicate=Plan(["TST-MOVEMENT-P1"]);
        duplicate["sourceSiteId"]="SITE-DEST";
        duplicate["destinationSiteId"]="SITE-UNSPECIFIED";
        var blocked=await svc.PreviewAsync(duplicate,Actor,false,default);
        Assert.False(blocked.Success);
        Assert.Equal(2L,Count("PersonnelMovements"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransfersRemainPossibleAfterAttendanceCutoff_AndClosedDay(bool external)
    {
        // A closed attendance period is not a ban on personnel movements.
        // The existing attendance snapshot remains untouched in either case.
        using(var c=_db.CreateOpenConnection())
        using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,StatusNameSnapshot,
                    StatusSymbolSnapshot,StatusColorSnapshot,AssignmentIdAtEffectiveDate,
                    SiteIdAtEffectiveDate,SiteNameSnapshot,Source,CreatedByUserId,
                    CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES('TST-MOVEMENT-P1',$date,'STS-ABSENT','غياب','غ','#DC2626',
                    'TST-MOVEMENT-A1','SITE-UNSPECIFIED','غير محدد','manual',
                    'TST-TRANSFER-ADMIN','2026-10-23T00:00:00Z',1,'TEST-CLOSED-MOVE');
                INSERT INTO PeriodClosures(PeriodKey,PeriodType,IsClosed,ClosedAtUtc,
                    ClosedByActor,PolicyVersion)
                VALUES($date,'DAY',1,'2026-10-27T20:00:00Z','TST-TRANSFER-ADMIN','v1');
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES('DailyAttendanceLockTimeLocal','00:00','2026-10-27T00:00:00Z')
                ON CONFLICT(SettingKey) DO UPDATE SET SettingValue=excluded.SettingValue,
                    UpdatedAtUtc=excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$date",Today());
            await cmd.ExecuteNonQueryAsync();
        }
        var svc=new PersonnelMovementService(_db);
        var request=Plan(["TST-MOVEMENT-P1"],external);
        var preview=await svc.PreviewAsync(request,Actor,external,default);
        Assert.True(preview.Success,preview.UserMessage);
        var committed=await svc.CommitAsync(new JsonObject{["previewToken"]=Token(preview)},
            Actor,external,default);
        Assert.True(committed.Success,committed.UserMessage);
        Assert.Equal(1L,Count("AttendanceRecords"));
        Assert.Equal(1L,Count("PersonnelMovements"));
        var summary=await new DailyMonthlyReadService(_db).DashboardSummaryAsync(new JsonObject{
            ["businessDate"]=Today(),["siteId"]=""
        },default);
        Assert.True(summary.Success);
        Assert.Equal(external?2L:3L,
            Assert.IsType<JsonObject>(summary.Data)["totalForce"]!.GetValue<long>());
        var year=DateOnly.Parse(Today());
        var month=await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject{
            ["year"]=year.Year,["month"]=year.Month,["page"]=1,["pageSize"]=50
        },default);
        Assert.True(month.Success,month.UserMessage);
        var rows=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(month.Data)["items"]);
        Assert.Equal(external?2:3,rows.Count);
        Assert.Equal(!external,rows.OfType<JsonObject>().Any(p=>
            p["personId"]!.GetValue<string>()=="TST-MOVEMENT-P1"));
        // The exited individual's original attendance and transfer are still
        // obtainable only through the historical archive.
        var archive=await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject{
            ["year"]=year.Year,["month"]=year.Month,["archiveMode"]=true,
            ["page"]=1,["pageSize"]=50
        },default);
        Assert.True(archive.Success,archive.UserMessage);
        var stored=Assert.Single(Assert.IsType<JsonArray>(
            Assert.IsType<JsonObject>(archive.Data)["items"]).OfType<JsonObject>(),p=>
            p["personId"]!.GetValue<string>()=="TST-MOVEMENT-P1");
        var archiveDay=Assert.IsType<JsonObject>(((JsonObject)stored["days"]!)[Today()]);
        Assert.Equal("غ",archiveDay["symbol"]!.GetValue<string>());
        Assert.Equal(external?"external-transfer":"internal-transfer",
            archiveDay["movementKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApprovedTransferRule_MovementOnDayAndAttendanceEligibleNextDay()
    {
        var movements = new PersonnelMovementService(_db);
        var preview = await movements.PreviewAsync(
            Plan(["TST-MOVEMENT-P1", "TST-MOVEMENT-P2"]),
            Actor, false, CancellationToken.None);
        var committed = await movements.CommitAsync(new JsonObject
        {
            ["previewToken"] = Token(preview)
        }, Actor, false, CancellationToken.None);
        Assert.True(committed.Success, committed.UserMessage);

        var reads = new DailyMonthlyReadService(_db);
        var today = Today();
        var tomorrow = DateOnly.Parse(today).AddDays(1).ToString("yyyy-MM-dd");

        async Task<JsonObject> Summary(string date) =>
            Assert.IsType<JsonObject>((await reads.DashboardSummaryAsync(new JsonObject
            {
                ["businessDate"] = date, ["siteId"] = "SITE-DEST"
            }, CancellationToken.None)).Data);

        async Task<JsonObject> Roster(string date, string status) =>
            Assert.IsType<JsonObject>((await reads.DailyRosterAsync(new JsonObject
            {
                ["businessDate"] = date, ["siteId"] = "SITE-DEST",
                ["statusId"] = status, ["page"] = 1, ["pageSize"] = 50
            }, CancellationToken.None)).Data);

        var transferDay = await Summary(today);
        Assert.Equal(2L, transferDay["totalForce"]!.GetValue<long>());
        Assert.Equal(2L, transferDay["internalTransferDay"]!.GetValue<long>());
        Assert.Equal(0L, transferDay["attendanceEligible"]!.GetValue<long>());
        Assert.Equal(0L, transferDay["unprepared"]!.GetValue<long>());
        Assert.Equal(0, transferDay["completionPercentage"]!.GetValue<int>());
        Assert.Equal(0L, (await Roster(today, "__UNPREPARED__"))["total"]!.GetValue<long>());
        Assert.Equal(0L, (await Roster(today, "__ELIGIBLE__"))["total"]!.GetValue<long>());
        var transfers = await Roster(today, "__TRANSFER_DAY__");
        Assert.Equal(2L, transfers["total"]!.GetValue<long>());
        foreach (var person in Assert.IsType<JsonArray>(transfers["items"]))
        {
            Assert.True(person!["transferDay"]!.GetValue<bool>());
            Assert.Equal("__TRANSFER_DAY__", person["statusId"]!.GetValue<string>());
            Assert.Contains("تم نقله من", person["statusName"]!.GetValue<string>());
            Assert.Contains("إلى الموقع الجديد", person["statusName"]!.GetValue<string>());
        }

        // On movement day even the destination cannot prepare the transferred person.
        var forbidden = await new AttendanceCommitService(_db,new SyntheticDaytimeClock()).PreviewAsync(new JsonObject
        {
            ["businessDate"] = today, ["siteId"] = "SITE-DEST",
            ["statusId"] = "STS-PRESENT", ["mode"] = "initial",
            ["personIds"] = new JsonArray("TST-MOVEMENT-P1")
        }, Actor, CancellationToken.None);
        Assert.False(forbidden.Success);
        Assert.Equal(BridgeErrorCodes.TransferDayAttendanceForbidden, forbidden.ErrorCode);

        var nextDay = await Summary(tomorrow);
        Assert.Equal(2L, nextDay["totalForce"]!.GetValue<long>());
        Assert.Equal(0L, nextDay["internalTransferDay"]!.GetValue<long>());
        Assert.Equal(2L, nextDay["attendanceEligible"]!.GetValue<long>());
        Assert.Equal(2L, nextDay["unprepared"]!.GetValue<long>());
        Assert.Equal(2L, (await Roster(tomorrow, "__UNPREPARED__"))["total"]!.GetValue<long>());
        Assert.Equal(2L, (await Roster(tomorrow, "__ELIGIBLE__"))["total"]!.GetValue<long>());
        Assert.Equal(0L, (await Roster(tomorrow, "__TRANSFER_DAY__"))["total"]!.GetValue<long>());
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
