using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>03.02-03.04: the founding administrator is one-time, independent
/// from every personnel record, and may provide a purely optional full name.</summary>
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

        // Subsequent users allow 8, but the founding administrator keeps
        // the previously owner-approved stronger 10-character minimum.
        Assert.False((await _service.ProvisionFirstAdministratorAsync(
            "chosenOwner","Abc123!z",CancellationToken.None)).Success);
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
        var admin = new BridgePrincipal(Assert.IsType<JsonObject>(setup.Data)["userId"]!.GetValue<string>(),
            "chosenOwner",UserRole.GeneralAdministration,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var accounts = await new UserAccountManagementService(_db).ListAsync(
            new JsonObject(),admin,CancellationToken.None);
        Assert.True(accounts.Success,accounts.UserMessage);
        var visible = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(accounts.Data)["items"]);
        Assert.Equal("الإدارة العامة - chosenOwner (صاحب البرنامج)",
            visible[0]!["fullName"]!.GetValue<string>());

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

    [Theory]
    [InlineData("Abc123!x")]
    [InlineData("plainpassword123")]
    public async Task DirectFoundingHelper_CannotBypassStrongCredentialPolicy(
        string password)
    {
        var attempted = await _service.CreateUserInternalAsync(
            "direct-founder", password, UserRole.GeneralAdministration,
            requiresSetup: false, CancellationToken.None);
        Assert.False(attempted.Success);
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM Users;"));
        Assert.False(await _service.IsProvisionedAsync());
    }

    [Theory]
    [InlineData(UserRole.OrdinaryAdministration)]
    [InlineData(UserRole.Supervisor)]
    public async Task InternalFirstUseHelper_MustNotCreateUnlinkedNonfounderAccounts(
        UserRole role)
    {
        var attempted = await _service.CreateUserInternalAsync(
            "unlinked-user", "Abc123!secure", role,
            requiresSetup: false, CancellationToken.None);
        Assert.False(attempted.Success);
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM Users;"));
        Assert.False(await _service.IsProvisionedAsync());
    }

    [Fact]
    public async Task FounderNameMayBeOmitted_AndRoleUsernameRemainVisible()
    {
        var setup = await _service.ProvisionFirstAdministratorAsync(
            "founderOptional", "test-password-123", CancellationToken.None);
        Assert.True(setup.Success,setup.UserMessage);
        Assert.Equal("", Scalar("SELECT FoundingFullName FROM Users WHERE Role=1;"));
        Assert.Equal("الإدارة العامة",Scalar(
            "SELECT ActorRosterFullNameSnapshot FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED';"));
        var actor = new BridgePrincipal(Assert.IsType<JsonObject>(setup.Data)["userId"]!.GetValue<string>(),
            "founderOptional",UserRole.GeneralAdministration,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var accounts = await new UserAccountManagementService(_db).ListAsync(
            new JsonObject(),actor,CancellationToken.None);
        Assert.True(accounts.Success,accounts.UserMessage);
        var visible = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(accounts.Data)["items"]);
        Assert.Equal("الإدارة العامة - founderOptional",
            visible[0]!["fullName"]!.GetValue<string>());
        Assert.Null(visible[0]!["personId"]);
    }

    [Fact]
    public async Task PermanentIDs_CannotBeReassignedAndHistoricalPersonsCannotBeDeleted()
    {
        var setup = await _service.ProvisionFirstAdministratorAsync(
            "immutableOwner", "test-password-123", CancellationToken.None);
        Assert.True(setup.Success, setup.UserMessage);
        Exec("""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,
                ActiveFlag,RowVersion)
            VALUES('PER-ORIGINAL','اسم قديم','اسم قديم','7777','7777',
                '2026-01-01','2026-01-01T00:00:00Z',1,1,1);
            """);
        Assert.Throws<SqliteException>(() => Exec(
            "UPDATE Persons SET PersonId='PER-REPLACEMENT' WHERE PersonId='PER-ORIGINAL';"));
        Assert.Throws<SqliteException>(() => Exec(
            "DELETE FROM Persons WHERE PersonId='PER-ORIGINAL';"));
        Assert.Throws<SqliteException>(() => Exec(
            "UPDATE Sites SET SiteId='SITE-CHANGED' WHERE SiteId='SITE-UNSPECIFIED';"));
        Assert.Throws<SqliteException>(() => Exec(
            "UPDATE AttendanceStatusDefinitions SET StatusId='STS-CHANGED' WHERE StatusId='STS-PRESENT';"));
        Assert.Throws<SqliteException>(() => Exec(
            "DELETE FROM AttendanceStatusDefinitions WHERE StatusId='STS-PRESENT';"));
        Assert.Throws<SqliteException>(() => Exec(
            "UPDATE Users SET UserId='USR-REPLACED' WHERE Role=1;"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM Persons WHERE PersonId='PER-ORIGINAL';"));
    }

    [Fact]
    public async Task FounderCannotBeLinkedToAnyRosterPerson_EvenWhenRealAndActive()
    {
        var setup=await _service.ProvisionFirstAdministratorAsync(
            "realOwner","test-password-123",CancellationToken.None,"صاحب الحساب المؤسس");
        Assert.True(setup.Success,setup.UserMessage);
        string userId=Assert.IsType<JsonObject>(setup.Data)["userId"]!.GetValue<string>();
        var actor=new BridgePrincipal(userId,"realOwner",UserRole.GeneralAdministration,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        Exec("""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,
                ActiveFlag,RowVersion)
            VALUES('PER-FOUNDER-TEST','اسم فرد مستقل','اسم فرد مستقل','5555','5555',
                '2026-01-01','2026-01-01T00:00:00Z',1,1,1);
            """);
        // The SQLCipher boundary must reject even a direct UPDATE performed
        // outside WebView2, regardless of account privilege or person's status.
        Assert.Throws<SqliteException>(()=>Exec(
            "UPDATE Users SET LinkedPersonId='PER-FOUNDER-TEST' WHERE Role=1;"));
        Assert.Throws<SqliteException>(()=>Exec(
            "UPDATE Users SET Role=1,LinkedPersonId='PER-FOUNDER-TEST' WHERE Role=1;"));
        Assert.Equal("",Scalar("SELECT LinkedPersonId FROM Users WHERE Role=1;"));
        Assert.Equal(userId,Scalar("SELECT UserId FROM Users WHERE Role=1;"));
        Assert.Equal("صاحب الحساب المؤسس",Scalar(
            "SELECT FoundingFullName FROM Users WHERE Role=1;"));

        var accounts=await new UserAccountManagementService(_db).ListAsync(
            new JsonObject(),actor,CancellationToken.None);
        Assert.True(accounts.Success,accounts.UserMessage);
        var items=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(accounts.Data)["items"]);
        Assert.Single(items);
        var listed=Assert.IsType<JsonObject>(items[0]);
        Assert.Null(listed["personId"]);
        Assert.Equal("",listed["serviceNumberA"]!.GetValue<string>());
        Assert.Equal("الإدارة العامة - realOwner (صاحب الحساب المؤسس)",
            listed["fullName"]!.GetValue<string>());
        Assert.DoesNotContain(ActionManifest.All,
            action=>action.Action=="LinkFoundingAdministrator");
        Assert.Equal("صاحب الحساب المؤسس",Scalar(
            "SELECT ActorRosterFullNameSnapshot FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED';"));
        Assert.Equal("0",Scalar(
            "SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='FOUNDING_ACCOUNT_LINKED';"));

        // The founder's lifecycle never depends on a roster person's lifecycle.
        Exec("UPDATE Persons SET ActiveFlag=0,OrganizationalStatus=3 WHERE PersonId='PER-FOUNDER-TEST';");
        Assert.Equal("1",Scalar("SELECT ActiveFlag FROM Users WHERE Role=1;"));
        Assert.Equal("",Scalar("SELECT LinkedPersonId FROM Users WHERE Role=1;"));
    }
}
