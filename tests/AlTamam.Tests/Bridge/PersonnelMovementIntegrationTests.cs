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
