using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Domain.Policies;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Synthetic encrypted integration tests for SECTION 04.03: archived candidate
/// disambiguation, explicit return, independent identity, temporal continuity.
/// </summary>
public sealed class ArchivedIdentityReturnIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new(
        "TST-RETURN-USER","return-tester",UserRole.GeneralAdministration,
        "fedcbafedcbafedcbafedcbafedcbafe");
    private static string Today => TimeProvider.System.GetLocalNow().ToString("yyyy-MM-dd");
    private static string ExitDay => DateOnly.Parse(Today).AddDays(-1).ToString("yyyy-MM-dd");
    private static string AdmissionDay => DateOnly.Parse(Today).AddDays(-10).ToString("yyyy-MM-dd");

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        using var s=c.CreateCommand();
        s.CommandText="""
            INSERT INTO Sites (SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
            VALUES('SITE-RETURN','موقع العودة','موقع العودة',0,1,'2026-01-01T00:00:00Z',1);
            """;
        await s.ExecuteNonQueryAsync();
        await SeedArchived("TST-RETURN-EXT","شخص سابق منقول","4141",2);
        await SeedArchived("TST-RETURN-SEP","شخص سابق مفصول","5151",3);
    }

    private async Task SeedArchived(string id,string name,string number,int status)
    {
        string type=status==2?"2":"3",movement="MOV-"+id;
        string normalized=ArabicTextNormalizer.NormalizeForSearch(name);
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText=$"""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES($id,$name,$normalized,$num,$num,$start,'2026-01-01T00:00:00Z',$status,0,2);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES($id,$name,$num,$start,NULL,'TST-SEED','2026-01-01T00:00:00Z','هوية أصلية');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES($assign,$id,'SITE-UNSPECIFIED',$start,$exit,NULL,'2026-01-01T00:00:00Z');
            INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,ServiceNumberASnapshot,
                MovementType,SourceSiteId,SourceSiteNameSnapshot,DestinationSiteId,
                DestinationSiteNameSnapshot,DestinationExternalName,SeparationReason,
                EffectiveLocalDate,CommittedAtUtc,ActorUserId,IdempotencyKey)
            VALUES($move,$id,$name,$num,{type},'SITE-UNSPECIFIED','غير محدد',
                NULL,NULL,$external,$reason,$exit,'2026-01-01T00:00:00Z','TST-SEED',$move);
            """;
        q.Parameters.AddWithValue("$id",id);
        q.Parameters.AddWithValue("$status",status);
        q.Parameters.AddWithValue("$name",name);
        q.Parameters.AddWithValue("$normalized",normalized);
        q.Parameters.AddWithValue("$num",number);
        q.Parameters.AddWithValue("$start",AdmissionDay);
        q.Parameters.AddWithValue("$exit",ExitDay);
        q.Parameters.AddWithValue("$assign","ASG-"+id);
        q.Parameters.AddWithValue("$move",movement);
        q.Parameters.AddWithValue("$external",status==2?"جهة خارجية":(object)DBNull.Value);
        q.Parameters.AddWithValue("$reason",status==3?"فصل تجريبي":(object)DBNull.Value);
        await q.ExecuteNonQueryAsync();
    }

    private static JsonObject Input(string name,string number,string site="SITE-RETURN")=>new()
    {
        ["visibleName"]=name,["serviceNumberA"]=number,["siteId"]=site
    };
    private long Scalar(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText=sql;
        return Convert.ToInt64(q.ExecuteScalar());
    }
    private string Value(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText=sql;
        return q.ExecuteScalar()?.ToString()??"";
    }
    private static JsonObject Require(BridgeResponse response)
    {
        Assert.True(response.Success,response.UserMessage);
        return Assert.IsType<JsonObject>(response.Data);
    }

    [Fact]
    public async Task ExternalTransferReturn_ReusesOriginalIdAndAddsDatedReturnMovement()
    {
        var svc=new ArchivedReturnService(_db);
        var preview=Require(await svc.PreviewAsync(Input("شخص عاد باسم جديد","4141"),Actor,default));
        Assert.True(preview["requiresResolution"]!.GetValue<bool>());
        var candidates=Assert.IsType<JsonArray>(preview["candidates"]);
        var one=Assert.IsType<JsonObject>(Assert.Single(candidates));
        Assert.Equal("TST-RETURN-EXT",one["personId"]!.GetValue<string>());
        Assert.Equal("نقل خارجي",one["exitType"]!.GetValue<string>());
        Assert.Equal(ExitDay,one["exitDate"]!.GetValue<string>());
        Assert.True(one["numberMatch"]!.GetValue<bool>());
        var payload=new JsonObject
        {
            ["previewToken"]=preview["previewToken"]!.GetValue<string>(),
            ["personId"]="TST-RETURN-EXT",["decision"]="same",
            ["operationId"]="TST-RETURN-REACTIVATE-0001"
        };
        var done=Require(await svc.CommitReturnAsync(payload,Actor,default));
        Assert.True(done["returned"]!.GetValue<bool>());
        Assert.Equal("TST-RETURN-EXT",done["personId"]!.GetValue<string>());
        Assert.Equal(2L,Scalar("SELECT COUNT(*) FROM Persons"));
        Assert.Equal(1L,Scalar("SELECT ActiveFlag FROM Persons WHERE PersonId='TST-RETURN-EXT'"));
        Assert.Equal(1L,Scalar("SELECT OrganizationalStatus FROM Persons WHERE PersonId='TST-RETURN-EXT'"));
        Assert.Equal(2L,Scalar("SELECT COUNT(*) FROM PersonAssignments WHERE PersonId='TST-RETURN-EXT'"));
        Assert.Equal("SITE-RETURN",Value("SELECT SiteId FROM PersonAssignments WHERE PersonId='TST-RETURN-EXT' AND ValidToLocalDate IS NULL"));
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM PersonnelMovements WHERE PersonId='TST-RETURN-EXT' AND MovementType=4"));
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_RETURN'"));
        Assert.Equal(2L,Scalar("SELECT COUNT(*) FROM PersonIdentityHistory WHERE PersonId='TST-RETURN-EXT'"));
        Assert.Equal(ExitDay,Value("SELECT ValidToLocalDate FROM PersonAssignments WHERE AssignmentId='ASG-TST-RETURN-EXT'"));
        Assert.Equal(Today,Value("SELECT ValidToLocalDate FROM PersonIdentityHistory WHERE PersonId='TST-RETURN-EXT' ORDER BY HistoryId LIMIT 1"));
        Assert.Equal(0L,Scalar("SELECT COUNT(*) FROM AttendanceRecords"));

        var month=DateOnly.Parse(Today);
        var read=new DailyMonthlyReadService(_db);
        var grid=Require(await read.MonthlyGridAsync(new JsonObject
        {
            ["year"]=month.Year,["month"]=month.Month,["siteId"]="SITE-RETURN",
            ["page"]=1,["pageSize"]=50
        },default));
        var rows=Assert.IsType<JsonArray>(grid["items"]);
        var row=Assert.IsType<JsonObject>(Assert.Single(rows));
        var days=Assert.IsType<JsonObject>(row["days"]);
        Assert.Equal("return",days[Today]!["kind"]!.GetValue<string>());
        Assert.Contains("عاد إلى القوة",days[Today]!["name"]!.GetValue<string>());
        Assert.Null(days[ExitDay]);

        // A return is not an internal-transfer-day restriction. Ordinary attendance
        // is eligible on admission date and still needs a human preview/confirmation.
        var attendance=new AttendanceCommitService(_db,new SyntheticDaytimeClock(12));
        var eligible=await attendance.PreviewAsync(new JsonObject
        {
            ["businessDate"]=Today,["siteId"]="SITE-RETURN",
            ["personIds"]=new JsonArray("TST-RETURN-EXT"),
            ["statusId"]="STS-PRESENT",["mode"]="initial"
        },Actor,default);
        Assert.True(eligible.Success,eligible.UserMessage);
        var savedAttendance=await attendance.CommitAsync(new JsonObject
        {
            ["previewToken"]=Require(eligible)["previewToken"]!.GetValue<string>()
        },Actor,false,default);
        Assert.True(savedAttendance.Success,savedAttendance.UserMessage);
        var afterAttendance=Require(await read.MonthlyGridAsync(new JsonObject
        {
            ["year"]=month.Year,["month"]=month.Month,["siteId"]="SITE-RETURN",
            ["page"]=1,["pageSize"]=50
        },default));
        var afterRows=Assert.IsType<JsonArray>(afterAttendance["items"]);
        var afterDay=Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(Assert.Single(afterRows))["days"]![Today]);
        Assert.Equal("موجود",afterDay["name"]!.GetValue<string>());
        Assert.Equal("return",afterDay["movementKind"]!.GetValue<string>());
        Assert.Contains("عاد إلى القوة",afterDay["movementNote"]!.GetValue<string>());
        var retry=Require(await svc.CommitReturnAsync((JsonObject)payload.DeepClone(),Actor,default));
        Assert.True(retry["returned"]!.GetValue<bool>()); // receipt-based retry
        var conflict=await svc.CommitReturnAsync(new JsonObject
        {
            ["previewToken"]=payload["previewToken"]!.GetValue<string>(),
            ["personId"]="TST-RETURN-EXT",["decision"]="same",
            ["operationId"]="TST-RETURN-REACTIVATE-0002"
        },Actor,default);
        Assert.False(conflict.Success);
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM PersonnelMovements WHERE PersonId='TST-RETURN-EXT' AND MovementType=4"));
    }

    [Fact]
    public async Task SeparationReturn_WithNewNumber_KeepsOldPeriodAndChangesIdentityOnlyFromToday()
    {
        var service=new ArchivedReturnService(_db);
        var preview=Require(await service.PreviewAsync(Input("شخص سابق مفصول","5152"),Actor,default));
        var match=Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(preview["candidates"])));
        Assert.Equal("TST-RETURN-SEP",match["personId"]!.GetValue<string>());
        Assert.True(match["nameMatch"]!.GetValue<bool>());
        Assert.False(match["numberMatch"]!.GetValue<bool>());
        var returned=Require(await service.CommitReturnAsync(new JsonObject
        {
            ["previewToken"]=preview["previewToken"]!.GetValue<string>(),
            ["personId"]="TST-RETURN-SEP",["decision"]="same"
        },Actor,default));
        Assert.True(returned["returned"]!.GetValue<bool>());
        Assert.Equal("5152",Value("SELECT CurrentServiceNumberA FROM Persons WHERE PersonId='TST-RETURN-SEP'"));
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM PersonnelMovements WHERE PersonId='TST-RETURN-SEP' AND MovementType=4"));
        Assert.Equal("5151",Value("SELECT ServiceNumberASnapshot FROM PersonIdentityHistory WHERE PersonId='TST-RETURN-SEP' ORDER BY ValidFromLocalDate LIMIT 1"));
        Assert.Equal("5152",Value("SELECT ServiceNumberASnapshot FROM PersonIdentityHistory WHERE PersonId='TST-RETURN-SEP' ORDER BY ValidFromLocalDate DESC LIMIT 1"));
        Assert.Equal(Today,Value("SELECT ValidFromLocalDate FROM PersonIdentityHistory WHERE PersonId='TST-RETURN-SEP' ORDER BY ValidFromLocalDate DESC LIMIT 1"));
        Assert.Equal(0L,Scalar("SELECT COUNT(*) FROM AttendanceRecords WHERE PersonId='TST-RETURN-SEP'"));
        Assert.Equal(2L,Scalar("SELECT COUNT(*) FROM Persons"));
    }

    [Fact]
    public async Task ArchivedNumberCannotCreateAnotherIdentity_EvenWithDifferentDecision()
    {
        var service=new ArchivedReturnService(_db);
        var preview=Require(await service.PreviewAsync(Input("اسم مختلف تماماً","5151"),Actor,default));
        Assert.True(preview["numberContested"]!.GetValue<bool>());
        var create=await new PersonRosterService(_db).CreatePersonAsync(Actor.UserId,Actor.Username,
            new JsonObject
            {
                ["visibleName"]="اسم مختلف تماماً",["serviceNumberA"]="5151",
                ["siteId"]="SITE-RETURN",["identityDecision"]="different",
                ["identityResolutionToken"]=preview["previewToken"]!.GetValue<string>()
            },default);
        Assert.False(create.Success);
        Assert.Contains("الأرشيف",create.UserMessage);
        Assert.Equal(2L,Scalar("SELECT COUNT(*) FROM Persons"));
    }

    [Fact]
    public async Task NameOnlyCollision_ExplicitDifferentHumanCanCreateIndependentId()
    {
        var service=new ArchivedReturnService(_db);
        var requested=Input("شخص سابق مفصول","6262");
        var preview=Require(await service.PreviewAsync(requested,Actor,default));
        Assert.Single(Assert.IsType<JsonArray>(preview["candidates"]));
        Assert.False(preview["numberContested"]!.GetValue<bool>());
        var roster=new PersonRosterService(_db);
        var direct=await roster.CreatePersonAsync(Actor.UserId,Actor.Username,
            (JsonObject)requested.DeepClone(),default);
        Assert.False(direct.Success); // name-only match cannot be ignored without resolution
        var approved=(JsonObject)requested.DeepClone();
        approved["identityDecision"]="different";
        approved["identityResolutionToken"]=preview["previewToken"]!.GetValue<string>();
        approved["operationId"]="TST-DIFFERENT-HUMAN-0001";
        var created=Require(await roster.CreatePersonAsync(Actor.UserId,Actor.Username,approved,default));
        var id=created["personId"]!.GetValue<string>();
        Assert.NotEqual("TST-RETURN-SEP",id);
        Assert.Equal(3L,Scalar("SELECT COUNT(*) FROM Persons"));
        Assert.Equal(0L,Scalar("SELECT ActiveFlag FROM Persons WHERE PersonId='TST-RETURN-SEP'"));
        Assert.Equal(1L,Scalar("SELECT ActiveFlag FROM Persons WHERE PersonId='"+id+"'"));
        var repeated=Require(await roster.CreatePersonAsync(Actor.UserId,Actor.Username,
            (JsonObject)approved.DeepClone(),default));
        Assert.Equal(id,repeated["personId"]!.GetValue<string>());
        Assert.Equal(3L,Scalar("SELECT COUNT(*) FROM Persons"));
    }

    [Fact]
    public async Task ArchivedReturnRejectsForgedCandidateAndStaleRevision()
    {
        var service=new ArchivedReturnService(_db);
        var preview=Require(await service.PreviewAsync(Input("شخص سابق مفصول","5151"),Actor,default));
        var forged=await service.CommitReturnAsync(new JsonObject
        {
            ["previewToken"]=preview["previewToken"]!.GetValue<string>(),
            ["personId"]="TST-RETURN-EXT",["decision"]="same"
        },Actor,default);
        Assert.False(forged.Success);
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="UPDATE Persons SET RowVersion=RowVersion+1 WHERE PersonId='TST-RETURN-SEP';";
            q.ExecuteNonQuery();
        }
        var stale=await service.CommitReturnAsync(new JsonObject
        {
            ["previewToken"]=preview["previewToken"]!.GetValue<string>(),
            ["personId"]="TST-RETURN-SEP",["decision"]="same"
        },Actor,default);
        Assert.False(stale.Success);
        Assert.Equal(0L,Scalar("SELECT COUNT(*) FROM PersonnelMovements WHERE MovementType=4"));
        Assert.Equal(0L,Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_RETURN'"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
