using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>03.02-03.04: founding administrator is one-time, synthetic-free,
/// role protected and optionally linkable to a real roster person.</summary>
public sealed class Section03FoundingAccountTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _service = null!;
    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _service = new BridgeSharedServices(_db);
    }
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
    private string Scalar(string sql)
    {
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = sql;
        return q.ExecuteScalar()?.ToString() ?? "";
    }
    private void Exec(string sql)
    {
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = sql;
        q.ExecuteNonQuery();
    }

    [Fact]
    public async Task SetupRequiresStrongOwnerChosenCredentials_AndCannotRepeat()
    {
        Assert.False(await _service.IsProvisionedAsync());
        var weak = await _service.ProvisionFirstAdministratorAsync(
            "chosenOwner", "12345678", CancellationToken.None, "صاحب البرنامج");
        Assert.False(weak.Success);
        Assert.False(await _service.IsProvisionedAsync());

        var setup = await _service.ProvisionFirstAdministratorAsync(
            "chosenOwner", "test-password-123", CancellationToken.None, "صاحب البرنامج");
        Assert.True(setup.Success, setup.UserMessage);
        Assert.True(await _service.IsProvisionedAsync());
        Assert.Equal("صاحب البرنامج",Scalar("SELECT FoundingFullName FROM Users WHERE Role=1;"));
        Assert.Equal("", Scalar("SELECT LinkedPersonId FROM Users WHERE Role=1;"));
        Assert.Equal("0",Scalar("SELECT COUNT(*) FROM Persons;"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM Users WHERE Role=1;"));
        Assert.Equal("صاحب البرنامج", Scalar(
            "SELECT ActorRosterFullNameSnapshot FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED';"));

        var duplicate = await _service.ProvisionFirstAdministratorAsync(
            "anotherOwner", "test-password-123", CancellationToken.None);
        Assert.False(duplicate.Success);
        Exec("UPDATE Users SET ActiveFlag=0 WHERE Role=1;");
        Assert.True(await _service.IsProvisionedAsync());
        Assert.False((await _service.ProvisionFirstAdministratorAsync(
            "againOwner", "test-password-123", CancellationToken.None)).Success);
        Assert.Throws<SqliteException>(() => Exec("""
            INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
              CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,
              FailedLoginCount,CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,
              RowVersion)
            VALUES('TST-EVIL-GA',NULL,'second-ga','second-ga',1,'synthetic','salt',0,1,
                   0,'TST','TST','2026-01-01T00:00:00Z',1);
            """));
    }

    [Fact]
    public async Task FounderCanBindOnlyToRealActivePerson_WithoutReassigningIDsOrHistoricalAudit()
    {
        var setup = await _service.ProvisionFirstAdministratorAsync(
            "realOwner", "test-password-123", CancellationToken.None, "صاحب الحساب المؤسس");
        Assert.True(setup.Success, setup.UserMessage);
        string userId = setup.Data!["userId"]!.GetValue<string>();
        var actor = new BridgePrincipal(userId,"realOwner",UserRole.GeneralAdministration,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        Exec("""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,
                ActiveFlag,RowVersion)
            VALUES('PER-FOUNDER-TEST','اسم الفرد الحقيقي','اسم الفرد الحقيقي','5555','5555',
                '2026-01-01','2026-01-01T00:00:00Z',1,1,1);
            """);
        var svc = new UserAccountManagementService(_db);
        var blocked = await svc.LinkFoundingAccountAsync(new JsonObject{
            ["personId"]="PER-NON-EXISTENT",["expectedRevision"]=1
        },actor,CancellationToken.None);
        Assert.False(blocked.Success);
        var link = await svc.LinkFoundingAccountAsync(new JsonObject{
            ["personId"]="PER-FOUNDER-TEST",["expectedRevision"]=1,
            ["reason"]="ربط الحساب المؤسس بعد اعتماد الفرد"
        },actor,CancellationToken.None);
        Assert.True(link.Success,link.UserMessage);
        Assert.Equal("PER-FOUNDER-TEST",Scalar("SELECT LinkedPersonId FROM Users WHERE Role=1;"));
        Assert.Equal(userId,Scalar("SELECT UserId FROM Users WHERE Role=1;"));
        Assert.Equal("صاحب الحساب المؤسس",Scalar(
            "SELECT ActorRosterFullNameSnapshot FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED';"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='FOUNDING_ACCOUNT_LINKED';"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM Persons WHERE PersonId='PER-FOUNDER-TEST';"));
        Assert.False((await svc.LinkFoundingAccountAsync(new JsonObject{
            ["personId"]="PER-FOUNDER-TEST",["expectedRevision"]=1
        },actor,CancellationToken.None)).Success);
    }
}
