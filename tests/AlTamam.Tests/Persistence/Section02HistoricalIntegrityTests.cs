using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Persistence.Migrations;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>Section 02: encrypted schema, temporal evidence and authoritative actor snapshots.</summary>
public sealed class Section02HistoricalIntegrityTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }
    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }
    public void Dispose() => SyntheticFixtures.DeleteTempDatabase(_path);

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
    private static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }
    private static void SeedPerson(SqliteConnection c, string firstDate = "2025-01-01") => Exec(c, $"""
        INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
            NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
            OrganizationalStatus,ActiveFlag,RowVersion)
        VALUES('TST-SECTION02-PERSON','منفذ تجريبي','منفذ تجريبي','12345',
            '12345','{firstDate}','2025-01-01T00:00:00Z',1,1,1);
        """);

    [Fact]
    public void V004_IdentityIntervals_AreNonOverlappingAndHistoricallyImmutable()
    {
        using var c = _db.CreateOpenConnection();
        Assert.Equal(AlTamamDbContext.CurrentSchemaVersion, SchemaMigrationRunner.GetCurrentVersion(c));
        SeedPerson(c);
        Exec(c, """
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-SECTION02-PERSON','منفذ تجريبي','12345',
                '2025-01-01',NULL,'TST-USER','2025-01-01T00:00:00Z','إنشاء تجريبي');
            """);
        Assert.Throws<SqliteException>(() => Exec(c, """
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-SECTION02-PERSON','اسم متداخل','12346',
                '2025-06-01',NULL,'TST-USER','2025-06-01T00:00:00Z','تعارض تجريبي');
            """));
        Exec(c, """
            UPDATE PersonIdentityHistory SET ValidToLocalDate='2025-06-01'
            WHERE PersonId='TST-SECTION02-PERSON' AND ValidToLocalDate IS NULL;
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-SECTION02-PERSON','اسم جديد','12346',
                '2025-06-01',NULL,'TST-USER','2025-06-01T00:00:00Z','تغيير تجريبي');
            """);
        Assert.Throws<SqliteException>(() => Exec(c,
            "UPDATE PersonIdentityHistory SET NameSnapshot='تغيير التاريخ' WHERE ValidFromLocalDate='2025-01-01';"));
        Assert.Throws<SqliteException>(() => Exec(c,
            "DELETE FROM PersonIdentityHistory WHERE ValidFromLocalDate='2025-01-01';"));
        Assert.Equal("منفذ تجريبي", Scalar(c,
            "SELECT NameSnapshot FROM PersonIdentityHistory WHERE ValidFromLocalDate='2025-01-01';"));
    }

    [Fact]
    public void V004_ProtectedSite_AndHistoricalLabelsRejectMutation()
    {
        using var c = _db.CreateOpenConnection();
        Assert.Throws<SqliteException>(() => Exec(c,
            "UPDATE Sites SET VisibleName='موقع غير صحيح' WHERE SiteId='SITE-UNSPECIFIED';"));
        Assert.Throws<SqliteException>(() => Exec(c,
            "DELETE FROM Sites WHERE SiteId='SITE-UNSPECIFIED';"));
        Assert.Throws<SqliteException>(() => Exec(c, """
            INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,
                ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
            VALUES('SITE-UNSPECIFIED','متداخل','2025-01-01',NULL,'TST-USER','2025-01-01T00:00:00Z');
            """));
        Assert.Throws<SqliteException>(() => Exec(c, """
            INSERT INTO AttendanceStatusNameHistory(StatusId,VisibleNameSnapshot,ShortSymbolSnapshot,
                ColorSnapshot,ValidFromLocalDate,ValidToLocalDate,RecordedAtUtc)
            VALUES('STS-PRESENT','متداخل','م','#38BDF8','2025-01-01',NULL,'2025-01-01T00:00:00Z');
            """));
    }

    [Fact]
    public void V004_AttendanceMustMatchAnEligibleHistoricalAssignment()
    {
        using var c = _db.CreateOpenConnection();
        SeedPerson(c, "2025-01-15");
        Exec(c, """
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-SECTION02-ASSIGN','TST-SECTION02-PERSON','SITE-UNSPECIFIED',
                '2025-01-15',NULL,NULL,'2025-01-01T00:00:00Z');
            """);
        static string Insert(string day, string key) => $"""
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES('TST-SECTION02-PERSON','{day}','STS-PRESENT','موجود','م','#38BDF8',
                'TST-SECTION02-ASSIGN','SITE-UNSPECIFIED','غير محدد','manual',
                'TST-USER','2025-01-15T00:00:00Z',1,'{key}');
            """;
        Assert.Throws<SqliteException>(() => Exec(c, Insert("2025-01-14", "TST-BEFORE")));
        Exec(c, Insert("2025-01-15", "TST-VALID"));
        Assert.Throws<SqliteException>(() => Exec(c, Insert("2025-01-15", "TST-DUPLICATE")));
        Assert.Equal("1", Scalar(c, "SELECT COUNT(*) FROM AttendanceRecords;"));
    }

    [Fact]
    public async Task AuditWriter_UsesAccountOwnerAtEventTime_NotTargetOrCallerName()
    {
        using (var c = _db.CreateOpenConnection())
        {
            SeedPerson(c);
            Exec(c, """
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES('TST-SECTION02-PERSON','الاسم التاريخي للمنفذ','12345',
                    '2025-01-01',NULL,'TST-USER','2025-01-01T00:00:00Z','إنشاء تجريبي');
                INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
                    CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,FailedLoginCount,
                    CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,RowVersion)
                VALUES('TST-SECTION02-ACCOUNT','TST-SECTION02-PERSON','operator-a','operator-a',2,
                    'synthetic-test-only','synthetic-salt',0,1,0,'USR-SYSTEM','النظام',
                    '2025-01-01T00:00:00Z',1);
                """);
        }
        await _db.ExecuteInWriteTransactionAsync(async (c, tx) =>
        {
            await BridgeAuditWriter.WriteAsync(c, tx, TimeProvider.System,
                "TST_ACTION", "إجراء تجريبي", "TST-SECTION02-ACCOUNT",
                "spoofed-username", "اسم الفرد المستهدف غير الصحيح", "Person",
                "TST-TARGET", "مستهدف تجريبي", null, null,
                "إجراء اختبار", null, "2025-01-01", "TST-CORRELATION", CancellationToken.None);
            return true;
        });
        using (var c = _db.CreateOpenConnection())
        {
            Assert.Equal("operator-a", Scalar(c,
                "SELECT ActorUsernameSnapshot FROM AuditEvents WHERE OperationCode='TST_ACTION';"));
            Assert.Equal("الاسم التاريخي للمنفذ", Scalar(c,
                "SELECT ActorRosterFullNameSnapshot FROM AuditEvents WHERE OperationCode='TST_ACTION';"));
            Assert.Equal("TST-SECTION02-PERSON", Scalar(c,
                "SELECT ActorPersonId FROM AuditEvents WHERE OperationCode='TST_ACTION';"));
            Assert.Throws<SqliteException>(() => Exec(c,
                "UPDATE AuditEvents SET ActorUsernameSnapshot='fake' WHERE OperationCode='TST_ACTION';"));
            Assert.Throws<SqliteException>(() => Exec(c,
                "DELETE FROM AuditEvents WHERE OperationCode='TST_ACTION';"));
            Exec(c, "UPDATE Persons SET CurrentVisibleName='الاسم الحالي المعدل' WHERE PersonId='TST-SECTION02-PERSON';");
            Assert.Equal("الاسم التاريخي للمنفذ", Scalar(c,
                "SELECT ActorRosterFullNameSnapshot FROM AuditEvents WHERE OperationCode='TST_ACTION';"));
        }
    }


    // These tests deliberately bypass the C# service to exercise real SQLCipher
    // table-boundary validation. The application layer already canonicalizes
    // Arabic-Indic and Persian input before storing ASCII personnel numbers.
    [Theory]
    [InlineData("123")]
    [InlineData("123٤")]
    [InlineData("۱۲۳۴")]
    [InlineData("12 34")]
    [InlineData("12-34")]
    [InlineData("1234A")]
    public void V010_PersonInsert_RejectsNoncanonicalOrTooShortNumber(string number)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,
                ActiveFlag,RowVersion)
            VALUES($id,'شخص اختبار','شخص اختبار',$num,$num,
                '2025-01-01','2025-01-01T00:00:00Z',1,1,1);
            """;
        cmd.Parameters.AddWithValue("$id", "TST-INVALID-" + Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$num", number);
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void V010_PersonInsert_PreservesLeadingZerosAndSeventyDigitMaximum()
    {
        using var c = _db.CreateOpenConnection();
        string number = "00" + new string('1', 68);
        using var insert = c.CreateCommand();
        insert.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,
                ActiveFlag,RowVersion)
            VALUES('TST-VALID-70','شخص اختبار','شخص اختبار',$num,$num,
                '2025-01-01','2025-01-01T00:00:00Z',1,1,1);
            """;
        insert.Parameters.AddWithValue("$num", number);
        insert.ExecuteNonQuery();
        Assert.Equal(number, Scalar(c,
            "SELECT CurrentServiceNumberA FROM Persons WHERE PersonId='TST-VALID-70';"));
        using var bad = c.CreateCommand();
        bad.CommandText = """
            UPDATE Persons SET CurrentServiceNumberA=$num,
                NormalizedServiceNumberA=$num WHERE PersonId='TST-VALID-70';
            """;
        bad.Parameters.AddWithValue("$num", "00" + new string('1', 69));
        Assert.Throws<SqliteException>(() => bad.ExecuteNonQuery());
        Assert.Equal(number, Scalar(c,
            "SELECT CurrentServiceNumberA FROM Persons WHERE PersonId='TST-VALID-70';"));
    }

    [Fact]
    public void V010_MismatchedCanonicalColumnsAndInvalidIdentitySnapshot_FailAtStorageBoundary()
    {
        using var c = _db.CreateOpenConnection();
        SeedPerson(c);
        Assert.Throws<SqliteException>(() => Exec(c, """
            UPDATE Persons SET CurrentServiceNumberA='12346'
            WHERE PersonId='TST-SECTION02-PERSON';
            """));
        Assert.Throws<SqliteException>(() => Exec(c, """
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-SECTION02-PERSON','نسخة تاريخية','١٢٣٤٥',
                '2025-01-01',NULL,'TST-USER','2025-01-01T00:00:00Z','تجربة');
            """));
        Assert.Equal("12345", Scalar(c,
            "SELECT CurrentServiceNumberA FROM Persons WHERE PersonId='TST-SECTION02-PERSON';"));
        Assert.Equal("0", Scalar(c,
            "SELECT COUNT(*) FROM PersonIdentityHistory WHERE PersonId='TST-SECTION02-PERSON';"));
    }

    [Fact]
    public async Task V004_ForwardMigration_PreservesExistingV003Personnel()
    {
        string upgradePath = SyntheticFixtures.CreateTempDatabasePath();
        try
        {
            using var db = new AlTamamDbContext(upgradePath, SyntheticFixtures.TestKeyHex);
            using (var old = db.CreateOpenConnection())
            {
                var legacyRunner = new SchemaMigrationRunner(AlTamamDbContext.CanonicalMigrations.Take(3).ToArray());
                await legacyRunner.UpgradeAsync(old);
                Assert.Equal(3, SchemaMigrationRunner.GetCurrentVersion(old));
                SeedPerson(old);
            }
            await db.InitializeAsync();
            using var upgraded = db.CreateOpenConnection();
            Assert.Equal(AlTamamDbContext.CurrentSchemaVersion, SchemaMigrationRunner.GetCurrentVersion(upgraded));
            Assert.Equal("منفذ تجريبي", Scalar(upgraded,
                "SELECT CurrentVisibleName FROM Persons WHERE PersonId='TST-SECTION02-PERSON';"));
            Assert.Equal("ok", Scalar(upgraded, "PRAGMA integrity_check;"));
        }
        finally { SyntheticFixtures.DeleteTempDatabase(upgradePath); }
    }
}
