using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Owner-approved Section 04 rules: unprepared people remain selectable until
/// 19:59:59 local, 20:00 carries yesterday's committed status forward without
/// overwriting a manually prepared day or generating transfer-day attendance.
/// </summary>
public sealed class Section04EightPmCarryForwardTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private readonly SyntheticFixtures.AdjustableClock _clock = SyntheticFixtures.ClockAtLocalHour(19,59);
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new("TST-04-ADMIN","section04",
        UserRole.GeneralAdministration,"0123456789abcdef0123456789abcdef");

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    private async Task AddPersonAsync(string id,string suffix,bool hasYesterday,
        bool preparedToday=false,bool transferredToday=false)
    {
        var date=Iso(Today);
        var yesterday=Iso(Today.AddDays(-1));
        var assignment="TST-04-ASSIGN-"+suffix;
        var currentNumber="4000"+suffix;
        using var connection=_db.CreateOpenConnection();
        using var command=connection.CreateCommand();
        command.CommandText= """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES($id,$name,$name,$number,$number,$first,$now,1,1,1);
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES($assign,$id,'SITE-UNSPECIFIED',$first,NULL,NULL,$now);
            """;
        command.Parameters.AddWithValue("$id",id);
        command.Parameters.AddWithValue("$name","فرد اختبار الساعة الثامنة "+suffix);
        command.Parameters.AddWithValue("$number",currentNumber);
        command.Parameters.AddWithValue("$first",hasYesterday?yesterday:date);
        command.Parameters.AddWithValue("$now",_clock.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$assign",assignment);
        await command.ExecuteNonQueryAsync();

        if(hasYesterday)
        {
            await InsertStatusAsync(connection,id,assignment,yesterday,"STS-ABSENT",
                "غياب","غ","#EF4444","manual","YESTERDAY-"+suffix);
        }
        if(preparedToday)
        {
            await InsertStatusAsync(connection,id,assignment,date,"STS-PRESENT",
                "موجود","م","#38BDF8","manual","TODAY-"+suffix);
        }
        if(transferredToday)
        {
            using var site=connection.CreateCommand();
            site.CommandText= """
                INSERT OR IGNORE INTO Sites(SiteId,VisibleName,NormalizedName,
                    IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
                VALUES('TST-04-DEST','الموقع المستقبل','الموقع المستقبل',
                    0,1,$now,1);
                """;
            site.Parameters.AddWithValue("$now",_clock.GetUtcNow().ToString("O"));
            await site.ExecuteNonQueryAsync();
            using var movement=connection.CreateCommand();
            movement.CommandText= """
                INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,
                    ServiceNumberASnapshot,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                    DestinationSiteId,DestinationSiteNameSnapshot,EffectiveLocalDate,
                    CommittedAtUtc,ActorUserId,IdempotencyKey)
                VALUES($mov,$id,$name,$number,1,'SITE-UNSPECIFIED','غير محدد',
                    'TST-04-DEST','الموقع المستقبل',$date,$now,'TST-04-ADMIN',$idem);
                """;
            movement.Parameters.AddWithValue("$mov","TST-04-MOV-"+suffix);
            movement.Parameters.AddWithValue("$id",id);
            movement.Parameters.AddWithValue("$name","فرد اختبار الساعة الثامنة "+suffix);
            movement.Parameters.AddWithValue("$number",currentNumber);
            movement.Parameters.AddWithValue("$date",date);
            movement.Parameters.AddWithValue("$now",_clock.GetUtcNow().ToString("O"));
            movement.Parameters.AddWithValue("$idem","TST-04-MOV-IDEM-"+suffix);
            await movement.ExecuteNonQueryAsync();
        }
    }

    private async Task InsertStatusAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        string id,string assignment,string date,string statusId,string name,
        string symbol,string color,string source,string key)
    {
        using var command=connection.CreateCommand();
        command.CommandText= """
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES($id,$date,$status,$name,$symbol,$color,
                $assignment,'SITE-UNSPECIFIED','غير محدد',
                $source,'TST-04-ADMIN',$now,1,$key);
            """;
        command.Parameters.AddWithValue("$id",id);
        command.Parameters.AddWithValue("$date",date);
        command.Parameters.AddWithValue("$status",statusId);
        command.Parameters.AddWithValue("$name",name);
        command.Parameters.AddWithValue("$symbol",symbol);
        command.Parameters.AddWithValue("$color",color);
        command.Parameters.AddWithValue("$assignment",assignment);
        command.Parameters.AddWithValue("$source",source);
        command.Parameters.AddWithValue("$now",_clock.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$key",key);
        await command.ExecuteNonQueryAsync();
    }

    private string? Field(string field,string personId,string date)
    {
        using var connection=_db.CreateOpenConnection();
        using var command=connection.CreateCommand();
        if(field is not ("StatusId" or "Source"))
            throw new ArgumentOutOfRangeException(nameof(field));
        command.CommandText="SELECT "+field+
            " FROM AttendanceRecords WHERE PersonId=$id AND AttendanceLocalDate=$date;";
        command.Parameters.AddWithValue("$id",personId);
        command.Parameters.AddWithValue("$date",date);
        return command.ExecuteScalar() as string;
    }

    private long Count(string table)
    {
        if(table is not ("AttendanceRecords" or "AutoAttendanceRuns" or "AuditEvents"))
            throw new ArgumentOutOfRangeException(nameof(table));
        using var connection=_db.CreateOpenConnection();
        using var command=connection.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM "+table+";";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task PreviewBeforeEight_CannotCommitAfterEight_ThenAutomaticallyCopiesYesterday()
    {
        await AddPersonAsync("TST-04-P1","1",hasYesterday:true);
        var scheduler=new AutoAttendanceService(_db,_clock);
        Assert.False(scheduler.IsDueNow());
        Assert.Empty(await scheduler.RunPendingAsync());
        var manual=new AttendanceCommitService(_db,_clock);
        var preview=await manual.PreviewAsync(new JsonObject
        {
            ["businessDate"]=Iso(Today),["siteId"]="SITE-UNSPECIFIED",
            ["statusId"]="STS-PRESENT",["mode"]="initial",
            ["personIds"]=new JsonArray("TST-04-P1")
        },Actor,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        var token=Assert.IsType<JsonObject>(preview.Data)["previewToken"]!.GetValue<string>();
        Assert.Null(Field("StatusId","TST-04-P1",Iso(Today)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(scheduler.IsDueNow());

        var rejected=await manual.CommitAsync(new JsonObject
        {
            ["previewToken"]=token
        },Actor,false,CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Equal(BridgeErrorCodes.HistoricDayLocked,rejected.ErrorCode);
        var latePreview=await manual.PreviewAsync(new JsonObject
        {
            ["businessDate"]=Iso(Today),["siteId"]="SITE-UNSPECIFIED",
            ["statusId"]="STS-PRESENT",["mode"]="initial",
            ["personIds"]=new JsonArray("TST-04-P1")
        },Actor,CancellationToken.None);
        Assert.False(latePreview.Success);
        Assert.Null(Field("StatusId","TST-04-P1",Iso(Today)));

        var runs=await scheduler.RunPendingAsync();
        var run=Assert.Single(runs);
        Assert.False(run.BlockedClosedDay);
        Assert.Equal(1,run.CopiedCount);
        Assert.Equal("STS-ABSENT",Field("StatusId","TST-04-P1",Iso(Today)));
        Assert.Equal("20h-auto",Field("Source","TST-04-P1",Iso(Today)));
        Assert.Equal(1L,Count("AutoAttendanceRuns"));
        Assert.Empty(await scheduler.RunPendingAsync());
        var replay=await scheduler.RunForDateAsync(Today,false);
        Assert.True(replay.WasAlreadyCompleted);
        Assert.Equal(1L,Count("AutoAttendanceRuns"));
        Assert.Equal(2L,Count("AttendanceRecords"));
    }

    [Fact]
    public async Task EightPm_RespectsManualPrecedenceAndSkipsMovementsAndMissingYesterday()
    {
        await AddPersonAsync("TST-04-P1","1",hasYesterday:true);
        await AddPersonAsync("TST-04-P2","2",hasYesterday:true,preparedToday:true);
        await AddPersonAsync("TST-04-P3","3",hasYesterday:true,transferredToday:true);
        await AddPersonAsync("TST-04-P4","4",hasYesterday:false);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var run=await new AutoAttendanceService(_db,_clock)
            .RunForDateAsync(Today,false);
        Assert.Equal(1,run.CopiedCount);
        Assert.Equal(1,run.SkippedManualCount);
        Assert.Equal(1,run.SkippedTransferExemptCount);
        Assert.Equal(1,run.SkippedNoYesterdayCount);
        Assert.Equal(1L,Count("AutoAttendanceRuns"));
        Assert.Equal(1L,Count("AuditEvents"));
        Assert.Equal("STS-ABSENT",Field("StatusId","TST-04-P1",Iso(Today)));
        Assert.Equal("STS-PRESENT",Field("StatusId","TST-04-P2",Iso(Today)));
        Assert.Equal("manual",Field("Source","TST-04-P2",Iso(Today)));
        Assert.Null(Field("StatusId","TST-04-P3",Iso(Today)));
        Assert.Null(Field("StatusId","TST-04-P4",Iso(Today)));
        var summary=await new DailyMonthlyReadService(_db).DashboardSummaryAsync(
            new JsonObject {["businessDate"]=Iso(Today)},CancellationToken.None);
        Assert.True(summary.Success,summary.UserMessage);
        var facts=Assert.IsType<JsonObject>(summary.Data);
        Assert.Equal(1L,facts["internalTransferDay"]!.GetValue<long>());
        Assert.Equal(1L,facts["unprepared"]!.GetValue<long>());
        Assert.Equal(4L,facts["totalForce"]!.GetValue<long>());
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
