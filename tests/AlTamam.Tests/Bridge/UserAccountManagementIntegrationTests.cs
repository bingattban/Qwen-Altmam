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
    public async Task CreateAccount_RepeatedOperation_ReturnsSameUserWithoutExtraAuditOrRoleHistory()
    {
        var svc=new UserAccountManagementService(_db);
        var payload=new JsonObject {
            ["username"]="same-intent",["password"]=SafePassword,
            ["personId"]="TST-ACTIVE-1",["role"]="Supervisor",
            ["operationId"]="OPERATION-USER-000001"
        };
        var first=await svc.CreateAsync(payload,_general,CancellationToken.None);
        var replay=await svc.CreateAsync((JsonObject)payload.DeepClone(),_general,CancellationToken.None);
        Assert.True(first.Success,first.UserMessage);
        Assert.True(replay.Success,replay.UserMessage);
        Assert.Equal(Assert.IsType<JsonObject>(first.Data)["userId"]!.GetValue<string>(),
            Assert.IsType<JsonObject>(replay.Data)["userId"]!.GetValue<string>());
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM Users WHERE Username='same-intent';";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
        q.CommandText="SELECT COUNT(*) FROM UserRoleHistory;";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
        q.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='USER_CREATE';";
        Assert.Equal(1L,Convert.ToInt64(q.ExecuteScalar()));
        var changed=(JsonObject)payload.DeepClone();
        changed["personId"]="TST-ACTIVE-2";
        var conflict=await svc.CreateAsync(changed,_general,CancellationToken.None);
        Assert.False(conflict.Success);
        Assert.Equal(AlTamam.Contracts.Bridge.BridgeErrorCodes.ConflictStale,conflict.ErrorCode);
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


    [Fact]
    public async Task CreatedBy_UsesFrozenActualRosterName_AndListPagesHaveSequentialIndices()
    {
        // Founding administrator is linked to a real person, not synthetic metadata.
        var (hash,salt)=PasswordSecurity.HashPassword(SafePassword);
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
                    CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,
                    FailedLoginCount,CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,RowVersion)
                VALUES('TST-GA','TST-ACTIVE-1','ga-test','ga-test',1,
                    $hash,$salt,0,1,0,'TST-GA','ga-test','2026-01-01T00:00:00Z',1);
                """;
            q.Parameters.AddWithValue("$hash",hash);q.Parameters.AddWithValue("$salt",salt);
            await q.ExecuteNonQueryAsync();
        }
        var svc=new UserAccountManagementService(_db);
        var created=await svc.CreateAsync(new JsonObject {
            ["username"]="created-supervisor",["password"]=SafePassword,
            ["personId"]="TST-ACTIVE-2",["role"]="Supervisor"
        },_general,default);
        Assert.True(created.Success,created.UserMessage);
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT CreatedByDisplaySnapshot FROM Users WHERE Username='created-supervisor';";
            Assert.Equal("ga-test - مستخدم اختباري",q.ExecuteScalar());
            // Current name changes must never rewrite creator's historical snapshot.
            q.CommandText="UPDATE Persons SET CurrentVisibleName='اسم جديد' WHERE PersonId='TST-ACTIVE-1';";
            q.ExecuteNonQuery();
            q.CommandText="SELECT CreatedByDisplaySnapshot FROM Users WHERE Username='created-supervisor';";
            Assert.Equal("ga-test - مستخدم اختباري",q.ExecuteScalar());
        }
        var found=await svc.ListAsync(new JsonObject{
            ["term"]="created-supervisor",["page"]=1,["pageSize"]=20
        },_general,default);
        Assert.True(found.Success,found.UserMessage);
        var items=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(found.Data)["items"]);
        Assert.Single(items);
        Assert.Equal(1L,items[0]!["index"]!.GetValue<long>());
        Assert.Equal("ga-test - مستخدم اختباري",items[0]!["createdBy"]!.GetValue<string>());
        Assert.Equal("TEST-002",items[0]!["serviceNumberA"]!.GetValue<string>());
        var secondPage=await svc.ListAsync(new JsonObject{["page"]=2,["pageSize"]=1},
            _general,default);
        Assert.True(secondPage.Success,secondPage.UserMessage);
        var secondItems=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(secondPage.Data)["items"]);
        Assert.Single(secondItems);
        Assert.Equal(2L,secondItems[0]!["index"]!.GetValue<long>());
    }

    [Fact]
    public async Task ExplicitReactivationRequiresActiveRoster_AuthorizedRoleAndAudit()
    {
        var svc=new UserAccountManagementService(_db);
        var created=await svc.CreateAsync(new JsonObject{
            ["username"]="reactivate-supervisor",["password"]=SafePassword,
            ["personId"]="TST-ACTIVE-2",["role"]="Supervisor"
        },_ordinary,default);
        Assert.True(created.Success,created.UserMessage);
        string id=Assert.IsType<JsonObject>(created.Data)["userId"]!.GetValue<string>();
        var disabled=await svc.DeactivateAsync(new JsonObject{
            ["userId"]=id,["expectedRevision"]=1L
        },_ordinary,default);
        Assert.True(disabled.Success,disabled.UserMessage);
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="UPDATE Persons SET ActiveFlag=0,OrganizationalStatus=3 WHERE PersonId='TST-ACTIVE-2';";
            q.ExecuteNonQuery();
        }
        var intent=new JsonObject{["userId"]=id,["expectedRevision"]=2L,
            ["reason"]="عاد الفرد إلى القوة رسمياً"};
        Assert.False((await svc.ReactivateAsync(intent,_ordinary,default)).Success);
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="UPDATE Persons SET ActiveFlag=1,OrganizationalStatus=1 WHERE PersonId='TST-ACTIVE-2';";
            q.ExecuteNonQuery();
        }
        // Re-enrolling the person MUST NOT silently activate their old account.
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT ActiveFlag FROM Users WHERE UserId=$id;";
            q.Parameters.AddWithValue("$id",id);
            Assert.Equal(0L,Convert.ToInt64(q.ExecuteScalar()));
        }
        var supervisor=new BridgePrincipal(id,"reactivate-supervisor",UserRole.Supervisor,
            "00112233445566778899aabbccddeeff");
        Assert.False((await svc.ReactivateAsync(intent,supervisor,default)).Success);
        var reactivated=await svc.ReactivateAsync(intent,_ordinary,default);
        Assert.True(reactivated.Success,reactivated.UserMessage);
        Assert.Equal(3L,Assert.IsType<JsonObject>(reactivated.Data)["rowVersion"]!.GetValue<long>());
        Assert.False((await svc.ReactivateAsync(intent,_ordinary,default)).Success);
        using var check=_db.CreateOpenConnection();
        using var query=check.CreateCommand();
        query.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='USER_REACTIVATE';";
        Assert.Equal(1L,Convert.ToInt64(query.ExecuteScalar()));
        query.CommandText="SELECT Username FROM Users WHERE UserId=$id;";
        query.Parameters.AddWithValue("$id",id);
        Assert.Equal("reactivate-supervisor",query.ExecuteScalar());
    }

    [Fact]
    public async Task PasswordEditRejectsRoleUsernameAndPersonMutationAttempts()
    {
        var svc=new UserAccountManagementService(_db);
        var created=await svc.CreateAsync(new JsonObject{
            ["username"]="immutable-account",["password"]=SafePassword,
            ["personId"]="TST-ACTIVE-1",["role"]="Supervisor"
        },_general,default);
        Assert.True(created.Success,created.UserMessage);
        string id=Assert.IsType<JsonObject>(created.Data)["userId"]!.GetValue<string>();
        foreach(var extra in new[]{"role","username","personId","linkedPersonId","active"})
        {
            var attempt=new JsonObject{
                ["userId"]=id,["expectedRevision"]=1L,["password"]="New123!secure"
            };
            attempt[extra]=extra=="active"?JsonValue.Create(true):JsonValue.Create("tamper");
            var denied=await svc.ChangePasswordAsync(attempt,_general,default);
            Assert.False(denied.Success);
        }
        using var c=_db.CreateOpenConnection();using var q=c.CreateCommand();
        q.CommandText="SELECT Role,Username,LinkedPersonId,RowVersion FROM Users WHERE UserId=$id;";
        q.Parameters.AddWithValue("$id",id);
        using var reader=q.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal((int)UserRole.Supervisor,reader.GetInt32(0));
        Assert.Equal("immutable-account",reader.GetString(1));
        Assert.Equal("TST-ACTIVE-1",reader.GetString(2));
        Assert.Equal(1L,reader.GetInt64(3));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose() {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
