using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// 03.09 / 03.13: orphaned linked accounts must fail closed, and an expired
/// temporary login lock must let the next password attempt start from one.
/// All tests use a disposable encrypted SQLCipher database.
/// </summary>
public sealed class Section03AccountEligibilityAndLockoutTests : IAsyncLifetime, IDisposable
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now=new(2026,10,25,9,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>_now;
        public override TimeZoneInfo LocalTimeZone=>TimeZoneInfo.Utc;
        public void Advance(TimeSpan by)=>_now=_now.Add(by);
    }

    private const string Password="Abc123!secure";
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private readonly Clock _time=new();
    private AlTamamDbContext _db=null!;
    private BridgeSharedServices _auth=null!;
    private BridgePrincipal _general=null!;

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _auth=new BridgeSharedServices(_db,_time);
        var first=await _auth.ProvisionFirstAdministratorAsync("founding-owner",Password,default);
        Assert.True(first.Success,first.UserMessage);
        _general=new BridgePrincipal(Assert.IsType<JsonObject>(first.Data)["userId"]!.GetValue<string>(),
            "founding-owner",UserRole.GeneralAdministration,
            "0123456789abcdef0123456789abcdef");
    }

    private long Scalar(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();q.CommandText=sql;
        return Convert.ToInt64(q.ExecuteScalar());
    }

    [Fact]
    public async Task InactiveLinkedPerson_ImmediatelyRejectsSessionAndDisablesOrphanAccountWithAudit()
    {
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                    NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TEST-ELIGIBLE-PERSON','مختبر صلاحية','مختبر صلاحية','TEST-713','TEST-713',
                       '2026-10-24','2026-10-24T00:00:00Z',1,1,1);
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TEST-ELIGIBLE-ASSIGN','TEST-ELIGIBLE-PERSON','SITE-UNSPECIFIED',
                       '2026-10-24',NULL,NULL,'2026-10-24T00:00:00Z');
                """;
            await q.ExecuteNonQueryAsync();
        }
        var accounts=new UserAccountManagementService(_db,_time);
        var created=await accounts.CreateAsync(new JsonObject{
            ["username"]="linked-supervisor",["password"]=Password,
            ["passwordConfirmation"]=Password,
            ["personId"]="TEST-ELIGIBLE-PERSON",["role"]="Supervisor"
        },_general,default);
        Assert.True(created.Success,created.UserMessage);
        var userId=Assert.IsType<JsonObject>(created.Data)["userId"]!.GetValue<string>();

        var (session,error)=await _auth.AuthenticateAsync("linked-supervisor",Password,default);
        Assert.Null(error);
        Assert.NotNull(session);
        Assert.NotNull(await _auth.ResolveSessionAsync(session!.SessionNonce));

        // Simulate a legacy/external writer that bypassed the normal service's
        // transactional account-lifecycle hook. Do not let its open nonce survive.
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                UPDATE PersonAssignments SET ValidToLocalDate='2026-10-25'
                WHERE AssignmentId='TEST-ELIGIBLE-ASSIGN';
                UPDATE Persons SET ActiveFlag=0,OrganizationalStatus=3,RowVersion=RowVersion+1
                WHERE PersonId='TEST-ELIGIBLE-PERSON';
                """;
            await q.ExecuteNonQueryAsync();
        }

        Assert.Null(await _auth.ResolveSessionAsync(session.SessionNonce));
        await _auth.RefreshSessionIdleDeadlineAsync(session.SessionNonce);
        await _auth.RefreshActiveSessionFromNativeInputAsync();
        Assert.Null(await _auth.ResolveSessionAsync(session.SessionNonce));

        var (denied,failure)=await _auth.AuthenticateAsync("linked-supervisor",Password,default);
        Assert.Null(denied);
        Assert.False(failure!.Success);
        Assert.Equal(0L,Scalar("SELECT ActiveFlag FROM Users WHERE Username='linked-supervisor'"));
        Assert.Equal(1L,Scalar("SELECT RevokedFlag FROM UserSessions WHERE UserId=(SELECT UserId FROM Users WHERE Username='linked-supervisor')"));
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='USER_LIFECYCLE_SUSPEND_GUARD'"));

        // Returning the person to the force CANNOT silently reactivate the account.
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                UPDATE Persons SET ActiveFlag=1,OrganizationalStatus=1,RowVersion=RowVersion+1
                WHERE PersonId='TEST-ELIGIBLE-PERSON';
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TEST-ELIGIBLE-RETURN','TEST-ELIGIBLE-PERSON','SITE-UNSPECIFIED',
                       '2026-10-25',NULL,NULL,'2026-10-25T10:00:00Z');
                """;
            await q.ExecuteNonQueryAsync();
        }
        Assert.Null((await _auth.AuthenticateAsync("linked-supervisor",Password,default)).Principal);

        var reactivated=await accounts.ReactivateAsync(new JsonObject{
            ["userId"]=userId,["expectedRevision"]=2L,
            ["reason"]="إعادة تفعيل حساب المشرف بعد العودة إلى كشف القوة"
        },_general,default);
        Assert.True(reactivated.Success,reactivated.UserMessage);
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='USER_REACTIVATE'"));
        Assert.Equal(1L,Scalar("SELECT ActiveFlag FROM Users WHERE Username='linked-supervisor'"));
        Assert.Null(await _auth.ResolveSessionAsync(session.SessionNonce)); // old nonce never reopens
        Assert.NotNull((await _auth.AuthenticateAsync("linked-supervisor",Password,default)).Principal);
    }

    [Fact]
    public async Task FailedLogins_AfterFiveAttemptsLockForExactlyFifteenMinutes_ThenRecover()
    {
        for(int i=1;i<=5;i++)
        {
            var (principal,failure)=await _auth.AuthenticateAsync(
                "founding-owner","wrong-password",default);
            Assert.Null(principal);
            Assert.NotNull(failure);
            Assert.False(failure!.Success);
        }
        Assert.Equal(5L,Scalar("SELECT FailedLoginCount FROM Users WHERE Username='founding-owner'"));
        Assert.Equal(5L,Scalar("SELECT COUNT(*) FROM FailedLoginEvents"));
        Assert.Null((await _auth.AuthenticateAsync("founding-owner",Password,default)).Principal);

        _time.Advance(TimeSpan.FromMinutes(15));
        var (wrong,failureAfter)=await _auth.AuthenticateAsync(
            "founding-owner","still-wrong",default);
        Assert.Null(wrong);
        Assert.NotNull(failureAfter);
        Assert.Equal(1L,Scalar("SELECT FailedLoginCount FROM Users WHERE Username='founding-owner'"));
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT LockedUntilUtc FROM Users WHERE Username='founding-owner';";
            var value=q.ExecuteScalar();
            Assert.True(value is null or DBNull);
        }
        var (recovered,successError)=await _auth.AuthenticateAsync(
            "founding-owner",Password,default);
        Assert.Null(successError);
        Assert.NotNull(recovered);
        Assert.Equal(0L,Scalar("SELECT FailedLoginCount FROM Users WHERE Username='founding-owner'"));
        Assert.Equal(6L,Scalar("SELECT COUNT(*) FROM FailedLoginEvents"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose(){_db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);}
}
