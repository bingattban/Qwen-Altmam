using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class PersonnelSeparationIntegrationTests:IAsyncLifetime,IDisposable
{
    private sealed class TestClock:TimeProvider
    {
        private DateTimeOffset _now=TimeProvider.System.GetUtcNow();
        public override DateTimeOffset GetUtcNow()=>_now;
        public override TimeZoneInfo LocalTimeZone=>TimeZoneInfo.Local;
        public void Advance(TimeSpan delta)=>_now=_now.Add(delta);
    }
    private readonly TestClock _clock=new();
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal Actor=new("TEST-SEPARATION-ADMIN","admin",
        UserRole.GeneralAdministration,"8da34cb1047a44af84a988572b47aa77");
    private string Today()=>_clock.GetLocalNow().ToString("yyyy-MM-dd");
    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TEST-SEP-P1','فرد مختبر للفصل','فرد مختبر للفصل','SEP-001','SEP-001',
                   $yesterday,'2026-01-01T00:00:00Z',1,1,1);
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TEST-SEP-A1','TEST-SEP-P1','SITE-UNSPECIFIED',$yesterday,NULL,NULL,
                   '2026-01-01T00:00:00Z');
            """;
        q.Parameters.AddWithValue("$yesterday",DateOnly.Parse(Today()).AddDays(-1).ToString("yyyy-MM-dd"));
        await q.ExecuteNonQueryAsync();
    }
    private JsonObject Data()=>new()
    {
        ["personId"]="TEST-SEP-P1",
        ["reason"]="سبب فصل قانوني للاختبار",
        ["businessDate"]=Today()
    };
    private static string Token(BridgeResponse result)
    {
        Assert.True(result.Success,result.UserMessage);
        return Assert.IsType<JsonObject>(result.Data)["previewToken"]!.GetValue<string>();
    }
    private long Count(string table)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM "+table;
        return Convert.ToInt64(q.ExecuteScalar());
    }
    [Fact]
    public async Task SeparationRequiresServerDelay_ThenKeepsHistory_AuditAndMonthlyCell()
    {
        var svc=new PersonnelSeparationService(_db,_clock);
        var preview=await svc.PreviewAsync(Data(),Actor,CancellationToken.None);
        string token=Token(preview);
        var tooEarly=await svc.CommitAsync(new JsonObject{["previewToken"]=token},
            Actor,CancellationToken.None);
        Assert.False(tooEarly.Success);
        Assert.Equal(0L,Count("PersonnelMovements"));
        _clock.Advance(TimeSpan.FromSeconds(6));
        var saved=await svc.CommitAsync(new JsonObject{["previewToken"]=token},
            Actor,CancellationToken.None);
        Assert.True(saved.Success,saved.UserMessage);
        Assert.Equal(1L,Count("PersonnelMovements"));
        Assert.Equal(1L,Count("AuditEvents"));
        using(var c=_db.CreateOpenConnection())
        {
            using var q=c.CreateCommand();
            q.CommandText="""
                SELECT OrganizationalStatus,ActiveFlag FROM Persons WHERE PersonId='TEST-SEP-P1';
                """;
            using(var reader=await q.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(3,reader.GetInt32(0));
                Assert.Equal(0,reader.GetInt32(1));
            }
            q.CommandText="""
                SELECT ValidToLocalDate FROM PersonAssignments WHERE AssignmentId='TEST-SEP-A1';
                """;
            Assert.Equal(Today(),(string?)await q.ExecuteScalarAsync());
        }
        var second=await svc.CommitAsync(new JsonObject{["previewToken"]=token},
            Actor,CancellationToken.None);
        Assert.False(second.Success);
        Assert.Equal(1L,Count("PersonnelMovements"));
        var archive=await svc.ArchiveAsync(new JsonObject{["term"]="SEP-001"},CancellationToken.None);
        var result=Assert.IsType<JsonObject>(archive.Data);
        Assert.Equal(1L,result["total"]!.GetValue<long>());
        var row=Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(result["items"])[0]);
        Assert.Contains("سبب فصل",row["reason"]!.GetValue<string>());
        var detail=await svc.DetailsAsync(new JsonObject{["movementId"]=row["movementId"]!.GetValue<string>()},
            CancellationToken.None);
        Assert.True(detail.Success);
        var date=DateOnly.Parse(Today());
        var monthly=await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject
        {
            ["year"]=date.Year,["month"]=date.Month,
            ["siteId"]="SITE-UNSPECIFIED",["page"]=1,["pageSize"]=20
        },CancellationToken.None);
        Assert.True(monthly.Success,monthly.UserMessage);
        var items=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(monthly.Data)["items"]);
        Assert.Single(items);
        var days=Assert.IsType<JsonObject>(items[0]!["days"]);
        var cell=Assert.IsType<JsonObject>(days[Today()]);
        Assert.Equal("separation",cell["kind"]!.GetValue<string>());
        Assert.Equal("مفصول",cell["name"]!.GetValue<string>());
    }
    [Fact]
    public async Task MissingReasonAndWrongPersonDoNotChangeDatabase()
    {
        var svc=new PersonnelSeparationService(_db,_clock);
        var data=Data();
        data["reason"]=" ";
        Assert.False((await svc.PreviewAsync(data,Actor,CancellationToken.None)).Success);
        data=Data();
        data["personId"]="UNKNOWN";
        Assert.False((await svc.PreviewAsync(data,Actor,CancellationToken.None)).Success);
        Assert.Equal(0L,Count("PersonnelMovements"));
        Assert.Equal(0L,Count("AuditEvents"));
    }
    [Fact]
    public async Task PreparedPersonCannotBeSeparatedOnSameDay()
    {
        using(var c=_db.CreateOpenConnection())
        {
            using var q=c.CreateCommand();
            q.CommandText="""
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,StatusNameSnapshot,
                    StatusSymbolSnapshot,StatusColorSnapshot,AssignmentIdAtEffectiveDate,
                    SiteIdAtEffectiveDate,SiteNameSnapshot,Source,CreatedByUserId,
                    CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES('TEST-SEP-P1',$today,'STS-PRESENT','موجود','م','#38BDF8','TEST-SEP-A1',
                    'SITE-UNSPECIFIED','غير محدد','manual','admin',
                    '2026-10-23T00:00:00Z',1,'SEP-PREPARED-TEST');
                """;
            q.Parameters.AddWithValue("$today",Today());
            await q.ExecuteNonQueryAsync();
        }
        var service=new PersonnelSeparationService(_db,_clock);
        var result=await service.PreviewAsync(Data(),Actor,CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0L,Count("PersonnelMovements"));
    }
    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose(){_db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);}
}
