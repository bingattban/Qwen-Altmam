using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class AttendanceCommitIntegrationTests : IAsyncLifetime,IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal Actor=new("TST-ATTEND-USER","test-attendance",
        UserRole.GeneralAdministration,"aabbccddeeff00112233445566778899");
    private static string Today()=>TimeProvider.System.GetLocalNow().ToString("yyyy-MM-dd");
    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        for(int i=1;i<=2;i++)
        {
            using var cmd=c.CreateCommand();
            cmd.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                    NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($id,$name,$name,$number,$number,$date,$now,1,1,1);
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES($assign,$id,'SITE-UNSPECIFIED',$date,NULL,NULL,$now);
                """;
            cmd.Parameters.AddWithValue("$id","TST-ATTEND-P"+i);
            cmd.Parameters.AddWithValue("$assign","TST-ATTEND-A"+i);
            cmd.Parameters.AddWithValue("$name","فرد اختبار رقم "+i);
            cmd.Parameters.AddWithValue("$number","92"+i.ToString("D4"));
            cmd.Parameters.AddWithValue("$date",Today());
            cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
    }
    private static JsonObject Payload(string mode,string statusId,string[] ids,string reason="")=>new()
    {
        ["mode"]=mode,["statusId"]=statusId,["businessDate"]=Today(),
        ["siteId"]="SITE-UNSPECIFIED",["reason"]=reason,
        ["personIds"]=new JsonArray(ids.Select(id=>(JsonNode?)JsonValue.Create(id)).ToArray())
    };
    private static string Token(BridgeResponse r)
    {
        Assert.True(r.Success,r.UserMessage);
        return Assert.IsType<JsonObject>(r.Data)["previewToken"]!.GetValue<string>();
    }
    private long Count(string table)
    {
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT COUNT(*) FROM "+table;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    [Fact]
    public async Task BulkCommitAndRevision_AuditAtomicAndOneTimePreview()
    {
        var service=new AttendanceCommitService(_db,new SyntheticDaytimeClock());
        var initial=await service.PreviewAsync(Payload("initial","STS-PRESENT",
            ["TST-ATTEND-P1","TST-ATTEND-P2"]),Actor,CancellationToken.None);
        string token=Token(initial);
        var saved=await service.CommitAsync(new JsonObject{["previewToken"]=token},Actor,false,CancellationToken.None);
        Assert.True(saved.Success,saved.UserMessage);
        Assert.Equal(2L,Count("AttendanceRecords"));
        Assert.Equal(2L,Count("AuditEvents"));
        var replay=await service.CommitAsync(new JsonObject{["previewToken"]=token},Actor,false,CancellationToken.None);
        Assert.False(replay.Success);
        Assert.Equal(2L,Count("AttendanceRecords"));
        var revision=await service.PreviewAsync(Payload("revise","STS-ABSENT",
            ["TST-ATTEND-P1"],"تصحيح بيانات التحضير"),Actor,CancellationToken.None);
        Assert.True(revision.Success,revision.UserMessage);
        var revised=await service.CommitAsync(new JsonObject{["previewToken"]=Token(revision)},
            Actor,true,CancellationToken.None);
        Assert.True(revised.Success,revised.UserMessage);
        Assert.Equal(1L,Count("AttendanceRevisions"));
        Assert.Equal(3L,Count("AuditEvents"));
        using var c=_db.CreateOpenConnection();
        using var check=c.CreateCommand();
        check.CommandText="""
            SELECT RevisionNumber FROM AttendanceRecords WHERE PersonId='TST-ATTEND-P1';
            """;
        Assert.Equal(2L,Convert.ToInt64(check.ExecuteScalar()));
    }
    [Fact]
    public async Task ReviseTodayWithoutTypedReason_RequiresPreviewAndPreservesAutomaticAuditReason()
    {
        var service=new AttendanceCommitService(_db,new SyntheticDaytimeClock());
        var initial=await service.PreviewAsync(Payload("initial","STS-PRESENT",
            ["TST-ATTEND-P1"]),Actor,CancellationToken.None);
        var saved=await service.CommitAsync(new JsonObject{["previewToken"]=Token(initial)},
            Actor,false,CancellationToken.None);
        Assert.True(saved.Success,saved.UserMessage);
        var changed=await service.PreviewAsync(Payload("revise","STS-ABSENT",
            ["TST-ATTEND-P1"]),Actor,CancellationToken.None);
        Assert.True(changed.Success,changed.UserMessage);
        var preview=Assert.IsType<JsonObject>(changed.Data);
        Assert.Equal("تغيير حالة التمام بتأكيد المستخدم",
            preview["reason"]!.GetValue<string>());
        Assert.Equal(0L,Count("AttendanceRevisions")); // preview cannot save changes
        var done=await service.CommitAsync(new JsonObject{
            ["previewToken"]=Token(changed)},Actor,true,CancellationToken.None);
        Assert.True(done.Success,done.UserMessage);
        Assert.Equal(1L,Count("AttendanceRevisions"));
        using var connection=_db.CreateOpenConnection();
        using var command=connection.CreateCommand();
        command.CommandText="SELECT Reason FROM AttendanceRevisions WHERE PersonId='TST-ATTEND-P1';";
        Assert.Equal("تغيير حالة التمام بتأكيد المستخدم",
            Convert.ToString(command.ExecuteScalar()));
        var secondAttempt=await service.CommitAsync(new JsonObject{
            ["previewToken"]=Token(changed)},Actor,true,CancellationToken.None);
        Assert.False(secondAttempt.Success); // one-use token
    }

    [Fact]
    public async Task PreviewRejectsAlreadyPreparedIndividualWithFullNameAndServiceNumber()
    {
        var service=new AttendanceCommitService(_db,new SyntheticDaytimeClock());
        var first=await service.PreviewAsync(Payload("initial","STS-PRESENT",
            ["TST-ATTEND-P1"]),Actor,CancellationToken.None);
        var saved=await service.CommitAsync(new JsonObject{["previewToken"]=Token(first)},
            Actor,false,CancellationToken.None);
        Assert.True(saved.Success,saved.UserMessage);
        var rejected=await service.PreviewAsync(Payload("initial","STS-ABSENT",
            ["TST-ATTEND-P1"]),Actor,CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Contains("فرد اختبار رقم 1",rejected.UserMessage);
        Assert.Contains("920001",rejected.UserMessage);
        Assert.Equal(1L,Count("AttendanceRecords"));
        var same=await service.PreviewAsync(Payload("revise","STS-PRESENT",
            ["TST-ATTEND-P1"]),Actor,CancellationToken.None);
        Assert.False(same.Success);
        Assert.Contains("فرد اختبار رقم 1",same.UserMessage);
        Assert.Contains("920001",same.UserMessage);
        Assert.Equal(0L,Count("AttendanceRevisions"));
    }

    [Fact]
    public async Task RejectsPersonWithNoValidAssignmentByNameAndNumber()
    {
        using(var connection=_db.CreateOpenConnection())
        using(var command=connection.CreateCommand())
        {
            command.CommandText="""
                UPDATE Persons SET ActiveFlag=0 WHERE PersonId='TST-ATTEND-P2';
                """;
            await command.ExecuteNonQueryAsync();
        }
        var service=new AttendanceCommitService(_db,new SyntheticDaytimeClock());
        var rejected=await service.PreviewAsync(Payload("initial","STS-PRESENT",
            ["TST-ATTEND-P1","TST-ATTEND-P2"]),Actor,CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Contains("فرد اختبار رقم 2",rejected.UserMessage);
        Assert.Contains("920002",rejected.UserMessage);
        Assert.Equal(0L,Count("AttendanceRecords"));
        Assert.Equal(0L,Count("AuditEvents"));
    }

    [Fact]
    public async Task StalePreviewIdentifiesIndividualWhoseAttendanceChanged()
    {
        var service=new AttendanceCommitService(_db,new SyntheticDaytimeClock());
        var first=await service.PreviewAsync(Payload("initial","STS-PRESENT",
            ["TST-ATTEND-P1"]),Actor,CancellationToken.None);
        var competing=await service.PreviewAsync(Payload("initial","STS-ABSENT",
            ["TST-ATTEND-P1"]),Actor,CancellationToken.None);
        Assert.True(competing.Success,competing.UserMessage);
        var accepted=await service.CommitAsync(
            new JsonObject{["previewToken"]=Token(first)},
            Actor,false,CancellationToken.None);
        Assert.True(accepted.Success,accepted.UserMessage);
        var stale=await service.CommitAsync(
            new JsonObject{["previewToken"]=Token(competing)},
            Actor,false,CancellationToken.None);
        Assert.False(stale.Success);
        Assert.Contains("فرد اختبار رقم 1",stale.UserMessage);
        Assert.Contains("920001",stale.UserMessage);
        Assert.Equal(1L,Count("AttendanceRecords"));
    }

    [Fact]
    public async Task InvalidBulkSelection_WritesNothing()
    {
        var svc=new AttendanceCommitService(_db,new SyntheticDaytimeClock());
        var rejected=await svc.PreviewAsync(Payload("initial","STS-PRESENT",
            ["TST-ATTEND-P1","TST-PERSON-NONEXISTENT"]),Actor,CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Equal(0L,Count("AttendanceRecords"));
        Assert.Equal(0L,Count("AuditEvents"));
    }
    [Fact]
    public async Task HistoricDateIsRejected()
    {
        var svc=new AttendanceCommitService(_db,new SyntheticDaytimeClock());
        var data=Payload("initial","STS-PRESENT",["TST-ATTEND-P1"]);
        data["businessDate"]=DateOnly.FromDateTime(DateTime.Now).AddDays(-1).ToString("yyyy-MM-dd");
        var rejected=await svc.PreviewAsync(data,Actor,CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Equal(0L,Count("AttendanceRecords"));
    }
    [Fact]
    public async Task EightPm_LocalCutoffBlocksNewPreviewAndStaleApproval()
    {
        var clock=new SyntheticDaytimeClock(19,59);
        var service=new AttendanceCommitService(_db,clock);
        var valid=await service.PreviewAsync(Payload("initial","STS-PRESENT",
            ["TST-ATTEND-P1"]),Actor,CancellationToken.None);
        Assert.True(valid.Success,valid.UserMessage);
        string token=Token(valid);
        clock.SetLocalTime(20,0);
        var stopped=await service.PreviewAsync(Payload("initial","STS-ABSENT",
            ["TST-ATTEND-P2"]),Actor,CancellationToken.None);
        Assert.False(stopped.Success);
        Assert.Equal(BridgeErrorCodes.HistoricDayLocked,stopped.ErrorCode);
        var lateCommit=await service.CommitAsync(new JsonObject{["previewToken"]=token},
            Actor,false,CancellationToken.None);
        Assert.False(lateCommit.Success);
        Assert.Equal(BridgeErrorCodes.HistoricDayLocked,lateCommit.ErrorCode);
        Assert.Equal(0L,Count("AttendanceRecords"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
