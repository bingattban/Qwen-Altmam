using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>SQLCipher integration coverage for administrator-selected automatic fallback.</summary>
public sealed class AutoAttendanceDefaultPolicyTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal Admin = new("TST-AUTO-ADMIN","test-admin",
        UserRole.OrdinaryAdministration,"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly BridgePrincipal Supervisor = new("TST-AUTO-SUPERVISOR","test-supervisor",
        UserRole.Supervisor,"cccccccccccccccccccccccccccccccc");
    private static readonly DateOnly Date = new(2026,10,15);

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    private static JsonObject Selection(string statusId,string operationId)=>new()
    {
        ["statusId"]=statusId,["operationId"]=operationId
    };

    private long Count(string table)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM "+table;
        return Convert.ToInt64(q.ExecuteScalar());
    }

    [Fact]
    public async Task AdministratorSetsStatus_IdempotentlyAuditedAndCannotDeactivateActiveDefault()
    {
        var policy=new AutoAttendancePolicyService(_db);
        var unset=Assert.IsType<JsonObject>((await policy.GetAsync(default)).Data);
        Assert.False(unset["configured"]!.GetValue<bool>());
        Assert.Equal("08:00",unset["dueTimeLocal"]!.GetValue<string>());

        var denied=await policy.SetAsync(Selection("STS-PERMISSION",
            "AUTO-SV-DENIED-000001"),Supervisor,default);
        Assert.False(denied.Success);
        Assert.Equal(BridgeErrorCodes.Unauthorized,denied.ErrorCode);
        var unknown=await policy.SetAsync(Selection("STS-MISSING",
            "AUTO-INVALID-000001"),Admin,default);
        Assert.False(unknown.Success);
        Assert.Equal(BridgeErrorCodes.CustomStatusInactive,unknown.ErrorCode);

        var selection=Selection("STS-PERMISSION","AUTO-SET-DEFAULT-00001");
        var saved=await policy.SetAsync(selection,Admin,default);
        Assert.True(saved.Success,saved.UserMessage);
        var retry=await policy.SetAsync((JsonObject)selection.DeepClone(),Admin,default);
        Assert.True(retry.Success,retry.UserMessage);
        Assert.Equal(1L,Count("MutationReceipts"));
        Assert.Equal(1L,Count("AuditEvents"));

        var restored=Assert.IsType<JsonObject>((await policy.GetAsync(default)).Data);
        Assert.True(restored["configured"]!.GetValue<bool>());
        Assert.Equal("STS-PERMISSION",restored["defaultStatusId"]!.GetValue<string>());

        var disabled=await new AdminConfigurationService(_db).DeactivateStatusAsync(
            new JsonObject{["statusId"]="STS-PERMISSION",["expectedRevision"]=1L},Admin,default);
        Assert.False(disabled.Success);
        Assert.Contains("الحالة الافتراضية",disabled.UserMessage);
        using var connection=_db.CreateOpenConnection();
        using var q=connection.CreateCommand();
        q.CommandText="SELECT ActiveFlag FROM AttendanceStatusDefinitions WHERE StatusId='STS-PERMISSION';";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
    }

    [Fact]
    public async Task ScheduledDueAndDailyLock_AreIndependentAuthorizedPersistentAndIdempotent()
    {
        var policy=new AutoAttendancePolicyService(_db);
        var initial=Assert.IsType<JsonObject>((await policy.GetAsync(default)).Data);
        Assert.Equal("08:00",initial["dueTimeLocal"]!.GetValue<string>());
        Assert.Equal("20:00",initial["dailyLockTimeLocal"]!.GetValue<string>());
        var choice=new JsonObject{
            ["dueTimeLocal"]="07:15",["dailyLockTimeLocal"]="19:30",
            ["operationId"]="AUTO-CLOCKS-CHANGE-0001"
        };
        var forbidden=await policy.SetScheduleAsync((JsonObject)choice.DeepClone(),Supervisor,default);
        Assert.False(forbidden.Success);
        Assert.Equal(BridgeErrorCodes.Unauthorized,forbidden.ErrorCode);
        var invalid=await policy.SetScheduleAsync(new JsonObject{
            ["dueTimeLocal"]="25:42",["dailyLockTimeLocal"]="19:30",
            ["operationId"]="AUTO-CLOCKS-INVALID-0001"
        },Admin,default);
        Assert.False(invalid.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError,invalid.ErrorCode);

        var accepted=await policy.SetScheduleAsync(choice,Admin,default);
        Assert.True(accepted.Success,accepted.UserMessage);
        Assert.True((await policy.SetScheduleAsync(
            (JsonObject)choice.DeepClone(),Admin,default)).Success);
        var restartedPolicy=new AutoAttendancePolicyService(_db);
        var read=Assert.IsType<JsonObject>((await restartedPolicy.GetAsync(default)).Data);
        Assert.Equal("07:15",read["dueTimeLocal"]!.GetValue<string>());
        Assert.Equal("19:30",read["dailyLockTimeLocal"]!.GetValue<string>());
        Assert.Equal(TimeSpan.FromHours(7)+TimeSpan.FromMinutes(15),
            await new AutoAttendanceService(_db).GetDueTimeAsync());
        Assert.Equal(1L,Count("MutationReceipts"));
        Assert.Equal(1L,Count("AuditEvents"));

        var lockAllDay=await policy.SetScheduleAsync(new JsonObject{
            ["dueTimeLocal"]="07:15",["dailyLockTimeLocal"]="00:00",
            ["operationId"]="AUTO-CLOCKS-CHANGE-0002"
        },Admin,default);
        Assert.True(lockAllDay.Success,lockAllDay.UserMessage);
        var preview=await new AttendanceCommitService(_db).PreviewAsync(new JsonObject{
            ["businessDate"]=DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"),
            ["mode"]="initial"
        },Admin,default);
        Assert.False(preview.Success);
        Assert.Equal(BridgeErrorCodes.HistoricDayLocked,preview.ErrorCode);
    }

    [Fact]
    public async Task MissingYesterday_RequiresAdministratorSettingThenRunsOnceWithoutOverwritingManual()
    {
        using(var c=_db.CreateOpenConnection())
        {
            for(int i=1;i<=3;i++)
            {
                using var q=c.CreateCommand();
                q.CommandText="""
                    INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                        NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                        OrganizationalStatus,ActiveFlag,RowVersion)
                    VALUES($person,$name,$name,$number,$number,$yesterday,'2026-10-01T00:00:00Z',1,1,1);
                    INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                        ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                    VALUES($assignment,$person,'SITE-UNSPECIFIED',$yesterday,NULL,NULL,'2026-10-01T00:00:00Z');
                    """;
                q.Parameters.AddWithValue("$person","TST-AUTO-P"+i);
                q.Parameters.AddWithValue("$assignment","TST-AUTO-A"+i);
                q.Parameters.AddWithValue("$name","فرد تلقائي "+i);
                q.Parameters.AddWithValue("$number","AUTO-NUM-"+i);
                q.Parameters.AddWithValue("$yesterday",Date.AddDays(-1).ToString("yyyy-MM-dd"));
                await q.ExecuteNonQueryAsync();
            }
            using var q2=c.CreateCommand();
            q2.CommandText="""
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                    StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                    AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                    Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES ('TST-AUTO-P1','2026-10-14','STS-ABSENT','غياب','غ','#EF4444',
                    'TST-AUTO-A1','SITE-UNSPECIFIED','غير محدد','manual','TST-ADMIN',
                    '2026-10-14T07:00:00Z',1,'AUTO-TEST-YESTERDAY'),
                   ('TST-AUTO-P3','2026-10-15','STS-PRESENT','موجود','م','#38BDF8',
                    'TST-AUTO-A3','SITE-UNSPECIFIED','غير محدد','manual','TST-ADMIN',
                    '2026-10-15T07:00:00Z',1,'AUTO-TEST-MANUAL');
                """;
            await q2.ExecuteNonQueryAsync();
        }

        var service=new AutoAttendanceService(_db);
        // Missing yesterday and no default must NOT stop the other eligible person.
        var result=await service.RunForDateAsync(Date,false);
        Assert.Equal(1,result.CopiedCount);
        Assert.Equal(1,result.SkippedNoYesterdayCount);
        Assert.Equal(1,result.SkippedManualCount);
        Assert.Equal(3L,Count("AttendanceRecords"));
        Assert.Equal(1L,Count("AutoAttendanceRuns"));
        Assert.Equal(1L,Count("AuditEvents"));

        using(var c=_db.CreateOpenConnection())
        {
            using var q=c.CreateCommand();
            q.CommandText="""
                SELECT PersonId,StatusId,Source,IdempotencyKey FROM AttendanceRecords
                WHERE AttendanceLocalDate='2026-10-15' ORDER BY PersonId;
                """;
            using var rd=await q.ExecuteReaderAsync();
            var rows=new List<(string,string,string,string)>();
            while(await rd.ReadAsync())rows.Add((rd.GetString(0),rd.GetString(1),
                rd.GetString(2),rd.GetString(3)));
            Assert.Equal(new[]
            {
                ("TST-AUTO-P1","STS-ABSENT","08h-auto","AUTO08H-2026-10-15-TST-AUTO-P1"),
                ("TST-AUTO-P3","STS-PRESENT","manual","AUTO-TEST-MANUAL")
            },rows);
        }

        var replay=await service.RunForDateAsync(Date,false);
        Assert.True(replay.WasAlreadyCompleted);
        Assert.Equal(3L,Count("AttendanceRecords"));
        Assert.Equal(1L,Count("AutoAttendanceRuns"));

        var selected=await new AutoAttendancePolicyService(_db).SetAsync(
            Selection("STS-PERMISSION","AUTO-SET-DEFAULT-00002"),Admin,default);
        Assert.True(selected.Success,selected.UserMessage);
        // A subsequent day copies P1 and P3 from yesterday, and uses the
        // configured fallback for P2, without rewriting yesterday's ledger.
        var next=await service.RunForDateAsync(Date.AddDays(1),false);
        Assert.Equal(3,next.CopiedCount);
        Assert.Contains("منها 1 بالحالة الافتراضية",next.SummaryArabic);
        Assert.Equal(6L,Count("AttendanceRecords"));
        Assert.Equal(2L,Count("AutoAttendanceRuns"));
        Assert.Equal(3L,Count("AuditEvents"));
        var secondReplay=await service.RunForDateAsync(Date.AddDays(1),false);
        Assert.True(secondReplay.WasAlreadyCompleted);
        Assert.Equal(6L,Count("AttendanceRecords"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
