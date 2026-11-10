using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Real encrypted SQLCipher to XLSX; no production data or Windows UI.</summary>
public sealed class AutomaticMonthlyExcelTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _folder = Path.Combine(Path.GetTempPath(),
        "altamam-auto-monthly-test-" + Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db = null!;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026,11,5,21,30,0,TimeSpan.Zero));
    private static readonly BridgePrincipal Admin = new("TST-AUTO-EXCEL-OWNER",
        "test-owner", UserRole.GeneralAdministration, "dddddddddddddddddddddddddddddddd");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset At {get;set;} = now;
        public override DateTimeOffset GetUtcNow() => At;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _db=new AlTamamDbContext(_dbPath,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
              CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
              FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-AUTO-P','فرد الجدولة','فرد الجدولة','90887','90887',
              '2026-11-01','2026-11-01T08:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
              ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-AUTO-P','فرد الجدولة','90887','2026-11-01',NULL,
              'USR-SYSTEM','2026-11-01T08:00:00Z','اختبار مستقل');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
              ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-AUTO-ASSIGN','TST-AUTO-P','SITE-UNSPECIFIED',
              '2026-11-01',NULL,NULL,'2026-11-01T08:00:00Z');
            INSERT INTO CustomFieldDefinitions(FieldDefinitionId,TechnicalKey,
              ArabicCaption,FieldType,DisplayOrder,IsSystemProtected,
              VisibleFlag,ActiveFlag,CreatedAtUtc)
            VALUES('FLD-AUTO-SCHOOL','custom_auto_school','المؤهل العلمي',1,5,0,1,1,
              '2026-11-01T08:00:00Z');
            INSERT INTO CustomFieldValues(PersonId,FieldDefinitionId,FieldValueText,UpdatedAtUtc)
            VALUES('TST-AUTO-P','FLD-AUTO-SCHOOL','بكالوريوس','2026-11-01T08:00:00Z');
            """;
        await q.ExecuteNonQueryAsync();
    }

    private static JsonObject Configuration(bool enabled,string folder,string id) => new()
    {
        ["enabled"]=enabled,["directory"]=folder,["exportTime"]="21:00",
        ["operationId"]=id
    };
    private static string[] ReadStrings(string path)
    {
        using var archive=ZipFile.OpenRead(path);
        using var stream=archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml=XDocument.Load(stream);
        XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Empty(xml.Descendants(ns+"f"));
        return xml.Descendants(ns+"t").Select(x=>x.Value).ToArray();
    }

    [Fact]
    public async Task FirstUseIsOff_OnlyAuthorizedAdminsCanConfigure()
    {
        var svc=new AutomaticMonthlyExcelService(_db,_clock);
        var state=await svc.ReadAsync();
        Assert.False(state.Enabled);
        Assert.Equal("21:00",state.ExportTime);
        Assert.Equal("",state.Directory);
        Assert.False(await svc.RunIfDueAsync());
        var supervisor=Admin with { Role=UserRole.Supervisor };
        var rejected=await svc.ConfigureAsync(Configuration(true,_folder,"test-auto-unauthorized-001"),
            supervisor,default);
        Assert.False(rejected.Success);
        Assert.False((await svc.ReadAsync()).Enabled);
        var invalid=await svc.ConfigureAsync(Configuration(true,"/not/a/real/folder",
            "test-auto-bad-folder-0002"),Admin,default);
        Assert.False(invalid.Success);
        Assert.False((await svc.ReadAsync()).Enabled);
    }

    [Fact]
    public async Task MonthlyExcel_MatchesRosterCustomColumns_UpdatesExistingFiles_AndCatchesUp()
    {
        var svc=new AutomaticMonthlyExcelService(_db,_clock);
        var result=await svc.ConfigureAsync(Configuration(true,_folder,
            "test-auto-enable-20261105"),Admin,default);
        Assert.True(result.Success,result.UserMessage);
        Assert.True(await svc.RunIfDueAsync());
        var november=new DateTime(2026,11,1).ToString("MMMM yyyy",new CultureInfo("ar-YE"));
        string monthRoot=Path.Combine(_folder,"تمام شهر "+november);
        string combined=Path.Combine(monthRoot,"التمام الكلي للقوة",
            "التمام الكلي للقوة لشهر "+november+".xlsx");
        Assert.True(File.Exists(combined));
        Assert.Equal(2,Directory.GetFiles(monthRoot,"*.xlsx",SearchOption.AllDirectories).Length);
        string[] first=ReadStrings(combined);
        foreach(var expected in new[]{"م","الرقم (ع)","الاسم","الموقع",
            "المؤهل العلمي","فرد الجدولة","90887","بكالوريوس"})
            Assert.Contains(expected,first);
        Assert.False(await svc.RunIfDueAsync()); // repeated scheduler tick has no side effects

        using(var c=_db.CreateOpenConnection())
        using(var update=c.CreateCommand())
        {
            update.CommandText="""
                UPDATE CustomFieldValues SET FieldValueText='ماجستير'
                WHERE PersonId='TST-AUTO-P' AND FieldDefinitionId='FLD-AUTO-SCHOOL';
                """;
            await update.ExecuteNonQueryAsync();
        }
        _clock.At=new DateTimeOffset(2026,11,7,10,0,0,TimeSpan.Zero);
        Assert.True(await svc.RunIfDueAsync()); // caught up missed Nov 6
        string[] revised=ReadStrings(combined);
        Assert.Contains("ماجستير",revised);
        Assert.DoesNotContain("بكالوريوس",revised);
        Assert.Equal(2,Directory.GetFiles(monthRoot,"*.xlsx",SearchOption.AllDirectories).Length);
        Assert.Equal("2026-11-06",(await svc.ReadAsync()).LastSuccessDate);
        _clock.At=new DateTimeOffset(2026,12,2,21,30,0,TimeSpan.Zero);
        Assert.True(await svc.RunIfDueAsync()); // rebuild November + create December
        var december=new DateTime(2026,12,1).ToString("MMMM yyyy",new CultureInfo("ar-YE"));
        string newMonth=Path.Combine(_folder,"تمام شهر "+december);
        Assert.Equal(2,Directory.GetFiles(newMonth,"*.xlsx",SearchOption.AllDirectories).Length);
        Assert.Equal("2026-12-02",(await svc.ReadAsync()).LastSuccessDate);
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task FirstActivationWaitsUntilSelectedClock_NotDuringMorning()
    {
        _clock.At=new DateTimeOffset(2026,11,5,10,0,0,TimeSpan.Zero);
        var svc=new AutomaticMonthlyExcelService(_db,_clock);
        Assert.True((await svc.ConfigureAsync(Configuration(true,_folder,
            "test-auto-schedule-first-check"),Admin,default)).Success);
        Assert.False(await svc.RunIfDueAsync());
        Assert.Empty(Directory.GetFiles(_folder,"*.xlsx",SearchOption.AllDirectories));
        _clock.At=new DateTimeOffset(2026,11,5,21,30,0,TimeSpan.Zero);
        Assert.True(await svc.RunIfDueAsync());
        Assert.NotEmpty(Directory.GetFiles(_folder,"*.xlsx",SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ClosedCurrentDayNeverCreatesAutomaticExcelFiles()
    {
        _clock.At=new DateTimeOffset(2026,11,5,21,30,0,TimeSpan.Zero);
        var svc=new AutomaticMonthlyExcelService(_db,_clock);
        Assert.True((await svc.ConfigureAsync(Configuration(true,_folder,
            "test-auto-closed-day-first-check"),Admin,default)).Success);
        using(var connection=_db.CreateOpenConnection())
        using(var close=connection.CreateCommand())
        {
            close.CommandText="""
                INSERT INTO PeriodClosures
                  (PeriodKey,PeriodType,IsClosed,ClosedAtUtc,ClosedByActor,PolicyVersion)
                VALUES('2026-11-05','DAY',1,'2026-11-05T20:00:00Z',
                    'USR-SYSTEM','test-closed-day');
                """;
            await close.ExecuteNonQueryAsync();
        }
        Assert.False(await svc.RunIfDueAsync());
        Assert.Empty(Directory.GetFiles(_folder,"*.xlsx",SearchOption.AllDirectories));
        Assert.Equal("",(await svc.ReadAsync()).LastSuccessDate);
        _clock.At=new DateTimeOffset(2026,11,6,21,30,0,TimeSpan.Zero);
        Assert.True(await svc.RunIfDueAsync());
        Assert.NotEmpty(Directory.GetFiles(_folder,"*.xlsx",SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DisablingScheduleNeverRemovesExistingMonthlyWorkbooks()
    {
        var svc=new AutomaticMonthlyExcelService(_db,_clock);
        Assert.True((await svc.ConfigureAsync(Configuration(true,_folder,
            "test-auto-first-enable-001"),Admin,default)).Success);
        Assert.True(await svc.RunIfDueAsync());
        int before=Directory.GetFiles(_folder,"*.xlsx",SearchOption.AllDirectories).Length;
        Assert.True((await svc.ConfigureAsync(Configuration(false,_folder,
            "test-auto-disable-002"),Admin,default)).Success);
        _clock.At=new DateTimeOffset(2026,11,6,22,0,0,TimeSpan.Zero);
        Assert.False(await svc.RunIfDueAsync());
        Assert.Equal(before,Directory.GetFiles(_folder,"*.xlsx",SearchOption.AllDirectories).Length);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_dbPath);
        try { if(Directory.Exists(_folder))Directory.Delete(_folder,true); }
        catch(IOException){}
        catch(UnauthorizedAccessException){}
    }
}
