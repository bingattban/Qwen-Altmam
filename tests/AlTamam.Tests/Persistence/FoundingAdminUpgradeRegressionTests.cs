using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Persistence.Migrations;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>
/// 03.02–03.15 / 2026-10-25 owner override: the founder has an optional
/// free-standing name, never a personnel number or PersonId. This test exercises
/// an ACTUAL encrypted V008-to-V009 upgrade of an older linked installation.
/// </summary>
public sealed class FoundingAdminUpgradeRegressionTests : IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private static readonly SchemaMigration[] PreDecisionMigrations =
        AlTamamDbContext.CanonicalMigrations.Where(m => m.Version <= 8).ToArray();

    private static string Scalar(AlTamamDbContext db, string sql)
    {
        using var c = db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = sql;
        return q.ExecuteScalar()?.ToString() ?? "";
    }

    private static void Execute(AlTamamDbContext db, string sql)
    {
        using var c = db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = sql;
        q.ExecuteNonQuery();
    }

    [Fact]
    public async Task V009_UpgradeUnlinksRealLegacyFounderButPreservesAccountAndHistoricalEvidence()
    {
        using var db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        using (var old = db.CreateOpenConnection())
        {
            await new SchemaMigrationRunner(PreDecisionMigrations).UpgradeAsync(old);
            Assert.Equal(8, SchemaMigrationRunner.GetCurrentVersion(old));
        }

        var first = await new BridgeSharedServices(db).ProvisionFirstAdministratorAsync(
            "legacyOwner", "test-password-123", CancellationToken.None, "اسم المؤسس الاختياري");
        Assert.True(first.Success, first.UserMessage);
        string userId = Assert.IsType<JsonObject>(first.Data)["userId"]!.GetValue<string>();
        Execute(db, """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('PER-V008-FOUND','فرد من الكشف','فرد من الكشف',
                '12999','12999','2026-01-01','2026-01-01T00:00:00Z',1,1,1);
            UPDATE Users SET LinkedPersonId='PER-V008-FOUND',
                RowVersion=RowVersion+1 WHERE Role=1;
            """);

        Assert.Equal("PER-V008-FOUND", Scalar(db,
            "SELECT LinkedPersonId FROM Users WHERE Role=1;"));
        string hashBefore = Scalar(db, "SELECT CredentialHash FROM Users WHERE Role=1;");
        string saltBefore = Scalar(db, "SELECT PasswordSalt FROM Users WHERE Role=1;");
        string versionBefore = Scalar(db, "SELECT RowVersion FROM Users WHERE Role=1;");
        string eventHash = Scalar(db,
            "SELECT EventHash FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED';");
        string originalAuditName = Scalar(db,
            "SELECT ActorRosterFullNameSnapshot FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED';");
        string auditCount = Scalar(db, "SELECT COUNT(*) FROM AuditEvents;");

        await db.InitializeAsync(); // production forward-only migration path

        Assert.Equal("9", Scalar(db, "SELECT MAX(SchemaVersion) FROM SchemaVersions;"));
        Assert.Equal(userId, Scalar(db, "SELECT UserId FROM Users WHERE Role=1;"));
        Assert.Equal("", Scalar(db, "SELECT LinkedPersonId FROM Users WHERE Role=1;"));
        Assert.Equal("1", Scalar(db, "SELECT ActiveFlag FROM Users WHERE Role=1;"));
        Assert.Equal("legacyOwner", Scalar(db, "SELECT Username FROM Users WHERE Role=1;"));
        Assert.Equal("اسم المؤسس الاختياري", Scalar(db,
            "SELECT FoundingFullName FROM Users WHERE Role=1;"));
        Assert.Equal(hashBefore, Scalar(db, "SELECT CredentialHash FROM Users WHERE Role=1;"));
        Assert.Equal(saltBefore, Scalar(db, "SELECT PasswordSalt FROM Users WHERE Role=1;"));
        Assert.Equal((long.Parse(versionBefore) + 1).ToString(),
            Scalar(db, "SELECT RowVersion FROM Users WHERE Role=1;"));
        Assert.Equal("1", Scalar(db,
            "SELECT COUNT(*) FROM Persons WHERE PersonId='PER-V008-FOUND';"));
        Assert.Equal("12999", Scalar(db,
            "SELECT CurrentServiceNumberA FROM Persons WHERE PersonId='PER-V008-FOUND';"));
        Assert.Equal(originalAuditName, Scalar(db,
            "SELECT ActorRosterFullNameSnapshot FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED';"));
        Assert.Equal(eventHash, Scalar(db,
            "SELECT EventHash FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED';"));
        Assert.Equal(auditCount, Scalar(db, "SELECT COUNT(*) FROM AuditEvents;"));
        Assert.True(db.HasValidIntegrity());

        Assert.Throws<SqliteException>(() => Execute(db,
            "UPDATE Users SET LinkedPersonId='PER-V008-FOUND' WHERE Role=1;"));
        Assert.Throws<SqliteException>(() => Execute(db,
            "UPDATE Users SET Role=1,LinkedPersonId='PER-V008-FOUND' WHERE Role=1;"));

        // Reopening / upgrading a second time must never change the account.
        await db.InitializeAsync();
        Assert.Equal((long.Parse(versionBefore) + 1).ToString(),
            Scalar(db, "SELECT RowVersion FROM Users WHERE Role=1;"));
        Assert.Equal(eventHash, Scalar(db,
            "SELECT EventHash FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED';"));
    }

    [Fact]
    public async Task FreshV009_RejectsDirectFounderPersonLinkInsertBeforeAnyFounderExists()
    {
        using var db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await db.InitializeAsync();
        Execute(db, """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('PER-REAL','فرد حقيقي','فرد حقيقي',
                '22334','22334','2026-01-01','2026-01-01T00:00:00Z',1,1,1);
            """);
        Assert.Throws<SqliteException>(() => Execute(db, """
            INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
                CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,
                FailedLoginCount,CreatedByUserId,CreatedByDisplaySnapshot,
                CreatedAtUtc,RowVersion)
            VALUES('USR-FAKE','PER-REAL','badFounder','badfounder',1,
                'placeholder','salt',0,1,0,'USR-SYSTEM','النظام',
                '2026-01-01T00:00:00Z',1);
            """));
        Assert.Equal("0", Scalar(db, "SELECT COUNT(*) FROM Users;"));
        var setup = await new BridgeSharedServices(db).ProvisionFirstAdministratorAsync(
            "independentOwner", "test-password-123", CancellationToken.None);
        Assert.True(setup.Success, setup.UserMessage);
        Assert.Equal("", Scalar(db, "SELECT LinkedPersonId FROM Users WHERE Role=1;"));
        Assert.Equal("", Scalar(db, "SELECT FoundingFullName FROM Users WHERE Role=1;"));
    }

    public void Dispose() => SyntheticFixtures.DeleteTempDatabase(_path);
}
