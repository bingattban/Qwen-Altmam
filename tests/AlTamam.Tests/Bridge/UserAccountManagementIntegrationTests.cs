using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Security;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class UserAccountManagementIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private const string SafePassword="Abc123!secure";
    private readonly BridgePrincipal _general = new("TST-GA","ga-test",UserRole.GeneralAdministration,
        "0123456789abcdef0123456789abcdef");
    private readonly BridgePrincipal _ordinary = new("TST-OA","oa-test",UserRole.OrdinaryAdministration,
        "abcdef0123456789abcdef0123456789");

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
              NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
              OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-ACTIVE-1','مستخدم اختباري','مستخدم اختباري','TEST-001','TEST-001',
              '2026-10-01','2026-10-01T10:00:00Z',1,1,1);
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
              NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
              OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-ACTIVE-2','مشرف اختباري','مشرف اختباري','TEST-002','TEST-002',
              '2026-10-01','2026-10-01T10:00:00Z',1,1,1);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task GeneralAdministration_CreatesLinkedOrdinaryAccountWithAuditAndArgon2id()
    {
        var svc=new UserAccountManagementService(_db);
        var result=await svc.CreateAsync(new JsonObject {
            ["username"]="Person.Test",["password"]=SafePassword,
            ["personId"]="TST-ACTIVE-1",["role"]="OrdinaryAdministration"
        },_general,CancellationToken.None);
        Assert.True(result.Success,result.UserMessage);
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="""
            SELECT CredentialHash,PasswordSalt,LinkedPersonId,NormalizedUsername
            FROM Users WHERE Username='Person.Test';
            """;
        string hash,salt,personId,normalized;
        using(var reader=await q.ExecuteReaderAsync()) {
            Assert.True(await reader.ReadAsync());
            hash=reader.GetString(0);salt=reader.GetString(1);
            personId=reader.GetString(2);normalized=reader.GetString(3);
        }
        Assert.True(PasswordSecurity.VerifyPassword(SafePassword,hash,salt));
        Assert.Equal("TST-ACTIVE-1",personId);
        Assert.Equal("person.test",normalized);
        q.CommandText="SELECT NewValueJson FROM AuditEvents WHERE OperationCode='USER_CREATE';";
        var eventValue=Assert.IsType<string>(await q.ExecuteScalarAsync());
        Assert.DoesNotContain(SafePassword,eventValue);
        Assert.DoesNotContain(hash,eventValue);
        Assert.DoesNotContain(salt,eventValue);
        q.CommandText="SELECT COUNT(*) FROM UserRoleHistory;";
        Assert.Equal(1L,Convert.ToInt64(await q.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task OrdinaryAdministration_CanOnlyCreateSupervisorNotOrdinary()
    {
        var svc=new UserAccountManagementService(_db);
        var denied=await svc.CreateAsync(new JsonObject {
            ["username"]="denied",["password"]=SafePassword,
            ["personId"]="TST-ACTIVE-1",["role"]="OrdinaryAdministration"
        },_ordinary,CancellationToken.None);
        Assert.False(denied.Success);
        var success=await svc.CreateAsync(new JsonObject {
            ["username"]="supervisor-test",["password"]=SafePassword,
            ["personId"]="TST-ACTIVE-2",["role"]="Supervisor"
        },_ordinary,CancellationToken.None);
        Assert.True(success.Success,success.UserMessage);
        var users=await svc.ListAsync(new JsonObject(),_ordinary,CancellationToken.None);
        Assert.Equal(1L,Assert.IsType<JsonObject>(users.Data)["total"]!.GetValue<long>());
        var supervisor=new BridgePrincipal("TST-SUP","sup",UserRole.Supervisor,
            "99887766554433221100aabbccddeeff");
        Assert.False((await svc.ListAsync(new JsonObject(),supervisor,CancellationToken.None)).Success);
    }

    [Fact]
    public async Task RotationRevokesPriorSessionsAndDisablingDoesNotEraseAccount()
    {
        var svc=new UserAccountManagementService(_db);
        var create=await svc.CreateAsync(new JsonObject {
            ["username"]="person-password",["password"]=SafePassword,
            ["personId"]="TST-ACTIVE-1",["role"]="Supervisor"
        },_general,CancellationToken.None);
        Assert.True(create.Success,create.UserMessage);
        string userId=Assert.IsType<JsonObject>(create.Data)["userId"]!.GetValue<string>();
        using(var c=_db.CreateOpenConnection()) {
            using var cmd=c.CreateCommand();
            cmd.CommandText="""
                INSERT INTO UserSessions(SessionNonce,UserId,Role,IssuedAtUtc,ExpiresAtUtc,RevokedFlag)
                VALUES('00112233445566778899aabbccddeeff',$id,3,
                  '2026-10-01T00:00:00Z','2028-10-01T00:00:00Z',0);
                """;
            cmd.Parameters.AddWithValue("$id",userId);
            await cmd.ExecuteNonQueryAsync();
        }
        var bad=await svc.ChangePasswordAsync(new JsonObject {
            ["userId"]=userId,["expectedRevision"]=1L,["password"]="abc"
        },_general,CancellationToken.None);
        Assert.False(bad.Success);
        var rotated=await svc.ChangePasswordAsync(new JsonObject {
            ["userId"]=userId,["expectedRevision"]=1L,["password"]="New123!secure"
        },_general,CancellationToken.None);
        Assert.True(rotated.Success,rotated.UserMessage);
        using(var c=_db.CreateOpenConnection()) {
            using var q=c.CreateCommand();
            q.CommandText="SELECT RevokedFlag FROM UserSessions WHERE UserId=$id;";
            q.Parameters.AddWithValue("$id",userId);
            Assert.Equal(1L,Convert.ToInt64(await q.ExecuteScalarAsync()));
        }
        var disabled=await svc.DeactivateAsync(new JsonObject {
            ["userId"]=userId,["expectedRevision"]=2L
        },_general,CancellationToken.None);
        Assert.True(disabled.Success,disabled.UserMessage);
        using(var c=_db.CreateOpenConnection()) {
            using var q=c.CreateCommand();
            q.CommandText="SELECT ActiveFlag FROM Users WHERE UserId=$id;";
            q.Parameters.AddWithValue("$id",userId);
            Assert.Equal(0L,Convert.ToInt64(await q.ExecuteScalarAsync()));
        }
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose() {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
