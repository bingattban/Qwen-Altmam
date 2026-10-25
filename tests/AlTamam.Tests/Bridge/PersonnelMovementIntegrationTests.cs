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
        DateTime now=DateTime.Now;
        foreach(var (location,expected) in new[] {
            ("SITE-UNSPECIFIED","تم نقله إلى"),
            ("SITE-DEST","تم نقله من")
        })
        {
            var monthly=await month.MonthlyGridAsync(new JsonObject{
                ["year"]=now.Year,["month"]=now.Month,["siteId"]=location,
                ["page"]=1,["pageSize"]=50
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
            }
            Assert.Equal(2,transferCells);
        }
        var archivePeriods=await month.ArchivePeriodsAsync(CancellationToken.None);
        Assert.Contains(DateTime.Now.ToString("yyyy-MM"),
            Assert.IsType<JsonArray>(archivePeriods.Data).Select(x=>x!.GetValue<string>()));
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
    public async Task InvalidBulkOrAlreadyPreparedCannotTransferAnyPerson()
    {
        var svc=new PersonnelMovementService(_db);
        var unknown=await svc.PreviewAsync(Plan(["TST-MOVEMENT-P1","UNKNOWN"]),
            Actor,false,CancellationToken.None);
        Assert.False(unknown.Success);
        Assert.Equal(0L,Count("PersonnelMovements"));
        // A valid attendance record on the transfer day forbids moving that person.
        using(var c=_db.CreateOpenConnection())
        {
            using var cmd=c.CreateCommand();
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
        var blocked=await svc.PreviewAsync(Plan(["TST-MOVEMENT-P1","TST-MOVEMENT-P2"]),
            Actor,false,CancellationToken.None);
        Assert.False(blocked.Success);
        Assert.Equal(0L,Count("PersonnelMovements"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
