using System.Security.Cryptography;
using System.Text;
using AlTamam.Domain.Policies;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.IntegrationTests;

/// <summary>
/// Phase 02: independent real SQLCipher integration tests on synthetic data only.
/// All resources live under a unique temporary directory and never use AppData.
/// The existing SQLitePCLRaw.bundle_e_sqlcipher provider is deliberately unchanged.
/// </summary>
public sealed class Phase02EncryptedTemporalIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "AlTamam-Phase02-Synthetic-" + Guid.NewGuid().ToString("N"));
    private readonly string _key = Convert.ToHexString(
        SHA256.HashData(Encoding.ASCII.GetBytes("PHASE02-SYNTHETIC-KEY-NOT-A-SECRET")));
    private AlTamamDbContext _database = null!;
    private string DatabasePath => Path.Combine(_directory, "phase02.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new AlTamamDbContext(DatabasePath, _key, useConnectionPooling: false);
        await _database.InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _database?.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* synthetic test directory; best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* Windows may still release file handles */ }
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Value(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    private static long Count(SqliteConnection connection, string sql)
        => long.Parse(Value(connection, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static void Person(SqliteConnection connection, string id, string number,
        string name = "فرد تجريبي", string admission = "2025-01-01",
        int organizationalStatus = 1, bool active = true)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
              CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
              FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES($id,$name,$name,$number,$number,$from,
              '2025-01-01T00:00:00Z',$status,$active,1);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$from", admission);
        command.Parameters.AddWithValue("$status", organizationalStatus);
        command.Parameters.AddWithValue("$active", active ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void Assignment(SqliteConnection connection, string assignment,
        string person, string site, string start, string? end,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
              ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES($assignment,$person,$site,$start,$end,NULL,'2025-01-01T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$assignment", assignment);
        command.Parameters.AddWithValue("$person", person);
        command.Parameters.AddWithValue("$site", site);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", (object?)end ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void CipherHeader_IsEncrypted_AndWrongKeyCannotReadOrReplaceHistory()
    {
        using (var valid = _database.CreateOpenConnection())
        {
            Assert.Equal("11", Value(valid, "SELECT MAX(SchemaVersion) FROM SchemaVersions;"));
            Assert.NotEmpty(Value(valid, "PRAGMA cipher_version;"));
        }
        byte[] header = File.ReadAllBytes(DatabasePath).Take(16).ToArray();
        Assert.False(header.SequenceEqual("SQLite format 3\0"u8.ToArray()));
        Assert.Equal(16, header.Length);
        byte[] originalHeader = header.ToArray();

        string wrongKey = Convert.ToHexString(
            SHA256.HashData(Encoding.ASCII.GetBytes("A-DIFFERENT-SYNTHETIC-KEY")));
        var wrongAccess = Record.Exception(() =>
        {
            using var wrong = new AlTamamDbContext(DatabasePath, wrongKey,
                useConnectionPooling: false);
            using var connection = wrong.CreateOpenConnection();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM Persons;";
            read.ExecuteScalar();
        });
        Assert.NotNull(wrongAccess);
        Assert.Equal(originalHeader, File.ReadAllBytes(DatabasePath).Take(16).ToArray());
        using var reopened = new AlTamamDbContext(DatabasePath, _key,
            useConnectionPooling: false);
        Assert.True(reopened.HasValidIntegrity());
    }

    [Fact]
    public void PlaintextDatabase_IsRefusedByKeyedSqlCipherWithoutConvertingIt()
    {
        string plaintext = Path.Combine(_directory, "plain.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = plaintext, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE UnencryptedSynthetic(Id INTEGER PRIMARY KEY);");
        }
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(
            File.ReadAllBytes(plaintext).Take(16).ToArray()));

        var failure = Record.Exception(() =>
        {
            using var keyed = new AlTamamDbContext(plaintext, _key,
                useConnectionPooling: false);
            using var opened = keyed.CreateOpenConnection();
            using var command = opened.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM UnencryptedSynthetic;";
            command.ExecuteScalar();
        });
        Assert.NotNull(failure);
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(
            File.ReadAllBytes(plaintext).Take(16).ToArray()));
    }

    [Theory]
    [InlineData("٠۰٠۰١۲٣۴", "00001234")]
    [InlineData("۰۰٠٠٠١٢٣٤", "000001234")]
    [InlineData("00001234", "00001234")]
    public void PersonnelNumber_MixedDigitSystems_PreserveLeadingZeros(string input, string expected)
        => Assert.Equal(expected, ArabicTextNormalizer.NormalizeDigitsOnly(input));

    [Fact]
    public void EffectiveDateSnapshots_KeepOldPersonSiteStatusAndMonthlyAttendance()
    {
        using var connection = _database.CreateOpenConnection();
        Person(connection, "TST-HIST-001", "00001234", "اسم سابق", "2025-01-15");
        Execute(connection, """
            INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,
              CreatedAtUtc,RowVersion)
            VALUES('TST-SITE-OLD','موقع سابق','موقع سابق',0,1,'2025-01-01T00:00:00Z',1),
                  ('TST-SITE-NEW','موقع جديد','موقع جديد',0,1,'2025-01-01T00:00:00Z',1);
            INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,
              ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
            VALUES('TST-SITE-OLD','موقع سابق','2025-01-01','2025-02-01',
              'TST-SYSTEM','2025-01-01T00:00:00Z');
            INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,
              ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
            VALUES('TST-SITE-OLD','موقع أعيدت تسميته','2025-02-01',NULL,
              'TST-SYSTEM','2025-02-01T00:00:00Z');
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
              ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-HIST-001','اسم سابق','00001234','2025-01-15','2025-02-01',
              'TST-SYSTEM','2025-01-15T00:00:00Z','إنشاء اصطناعي'),
                  ('TST-HIST-001','اسم حديث','00005678','2025-02-01',NULL,
              'TST-SYSTEM','2025-02-01T00:00:00Z','تعديل اصطناعي');
            """);
        Assignment(connection, "TST-ASG-OLD", "TST-HIST-001",
            "TST-SITE-OLD", "2025-01-15", "2025-02-01");
        Assignment(connection, "TST-ASG-NEW", "TST-HIST-001",
            "TST-SITE-NEW", "2025-02-01", null);

        Execute(connection, """
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
              StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
              AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
              Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES('TST-HIST-001','2025-01-20','STS-PRESENT','موجود','م','#38BDF8',
              'TST-ASG-OLD','TST-SITE-OLD','موقع سابق','manual',
              'TST-SYSTEM','2025-01-20T10:00:00Z',1,'TST-HISTORY-ATT-001');
            UPDATE Persons SET CurrentVisibleName='اسم حديث',NormalizedName='اسم حديث',
              CurrentServiceNumberA='00005678',NormalizedServiceNumberA='00005678',
              RowVersion=2 WHERE PersonId='TST-HIST-001';
            UPDATE Sites SET VisibleName='موقع أعيدت تسميته',NormalizedName='موقع أعيدت تسميته'
              WHERE SiteId='TST-SITE-OLD';
            UPDATE AttendanceStatusDefinitions SET VisibleName='موجود بعد التعديل',
              NormalizedName='موجود بعد التعديل'
              WHERE StatusId='STS-PRESENT';
            """);

        Assert.Equal("اسم سابق", Value(connection, """
            SELECT NameSnapshot FROM PersonIdentityHistory
              WHERE PersonId='TST-HIST-001' AND '2025-01-20'>=ValidFromLocalDate
                AND ('2025-01-20'<ValidToLocalDate OR ValidToLocalDate IS NULL);
            """));
        Assert.Equal("00001234", Value(connection, """
            SELECT ServiceNumberASnapshot FROM PersonIdentityHistory
              WHERE PersonId='TST-HIST-001' AND ValidFromLocalDate='2025-01-15';
            """));
        Assert.Equal("موقع سابق", Value(connection, """
            SELECT VisibleNameSnapshot FROM SiteNameHistory
              WHERE SiteId='TST-SITE-OLD' AND ValidFromLocalDate='2025-01-01';
            """));
        Assert.Equal("موجود", Value(connection, """
            SELECT StatusNameSnapshot FROM AttendanceRecords
              WHERE PersonId='TST-HIST-001' AND AttendanceLocalDate='2025-01-20';
            """));
        Assert.Equal("موقع سابق", Value(connection, """
            SELECT SiteNameSnapshot FROM AttendanceRecords
              WHERE PersonId='TST-HIST-001' AND AttendanceLocalDate='2025-01-20';
            """));
        Assert.Equal("TST-SITE-NEW", Value(connection, """
            SELECT SiteId FROM PersonAssignments WHERE PersonId='TST-HIST-001'
              AND '2025-02-01'>=ValidFromLocalDate
              AND (ValidToLocalDate IS NULL OR '2025-02-01'<ValidToLocalDate);
            """));
        Assert.Equal(0L, Count(connection, """
            SELECT COUNT(*) FROM AttendanceRecords WHERE PersonId='TST-HIST-001'
              AND AttendanceLocalDate<'2025-01-15';
            """));
        Assert.Throws<SqliteException>(() => Execute(connection, """
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
              StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
              AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
              Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES('TST-HIST-001','2025-01-14','STS-PRESENT','موجود','م','#38BDF8',
              'TST-ASG-OLD','TST-SITE-OLD','موقع سابق','manual',
              'TST-SYSTEM','2025-01-14T10:00:00Z',1,'TST-HISTORY-INVALID');
            """));
    }

    [Fact]
    public void DismissedNumberReuse_UsesNewPersonIdAndCannotInheritOlderMonths()
    {
        using var connection = _database.CreateOpenConnection();
        Person(connection, "TST-DISMISSED", "00009999", "شخص سابق",
            organizationalStatus: 3, active: false);
        Assignment(connection, "TST-OLD-ASG", "TST-DISMISSED",
            "SITE-UNSPECIFIED", "2025-01-01", "2025-02-01");
        Execute(connection, """
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
              ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-DISMISSED','شخص سابق','00009999','2025-01-01',NULL,
              'TST-SYSTEM','2025-01-01T00:00:00Z','هوية تاريخية');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
              StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
              AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
              Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES('TST-DISMISSED','2025-01-20','STS-PRESENT','موجود','م','#38BDF8',
              'TST-OLD-ASG','SITE-UNSPECIFIED','غير محدد','manual',
              'TST-SYSTEM','2025-01-20T00:00:00Z',1,'TST-DISMISSED-ATT');
            """);
        Person(connection, "TST-NEW-PERSON", "00009999", "شخص مستقل",
            admission: "2025-03-01");
        Assignment(connection, "TST-NEW-ASG", "TST-NEW-PERSON",
            "SITE-UNSPECIFIED", "2025-03-01", null);
        Assert.Throws<SqliteException>(() => Person(connection, "TST-THIRD",
            "00009999", "duplicate active"));
        Assert.Equal("TST-DISMISSED", Value(connection,
            "SELECT PersonId FROM AttendanceRecords WHERE AttendanceLocalDate='2025-01-20';"));
        Assert.Equal(0L, Count(connection, """
            SELECT COUNT(*) FROM AttendanceRecords
              WHERE PersonId='TST-NEW-PERSON' AND AttendanceLocalDate<'2025-03-01';
            """));
        Assert.Equal(2L, Count(connection,
            "SELECT COUNT(*) FROM Persons WHERE NormalizedServiceNumberA='00009999';"));
    }

    [Fact]
    public async Task ConcurrentWriters_NeverCommitOverlappingAssignments()
    {
        using (var setup = _database.CreateOpenConnection())
            Person(setup, "TST-CONCURRENT", "00004567");
        using var start = new ManualResetEventSlim(false);
        Task<bool> Attempt(string assignment) => Task.Run(() =>
        {
            start.Wait();
            try
            {
                using var database = new AlTamamDbContext(DatabasePath, _key,
                    useConnectionPooling: false);
                using var connection = database.CreateOpenConnection();
                using var transaction = connection.BeginTransaction();
                Assignment(connection, assignment, "TST-CONCURRENT",
                    "SITE-UNSPECIFIED", "2025-01-01", null, transaction);
                transaction.Commit();
                return true;
            }
            catch (SqliteException) { return false; }
        });
        var first = Attempt("TST-RACE-1");
        var second = Attempt("TST-RACE-2");
        start.Set();
        var outcomes = await Task.WhenAll(first, second);
        Assert.Single(outcomes, value => value);
        using var inspect = _database.CreateOpenConnection();
        Assert.Equal(1L, Count(inspect,
            "SELECT COUNT(*) FROM PersonAssignments WHERE PersonId='TST-CONCURRENT';"));
    }

    [Fact]
    public async Task FailedTransaction_RollsBackAuditAndOverlayTogether()
    {
        const string audit = """
            INSERT INTO AuditEvents(AuditEventId,OperationCode,OperationArabicName,
              ActorUserId,ActorUsernameSnapshot,TargetEntityType,TargetEntityId,
              TargetDisplaySnapshot,DefaultArabicDescription,EffectiveLocalDate,
              ExecutedAtUtc,CorrelationId,EventHash,PriorEventHash)
            VALUES('TST-AUDIT-001','TST-OP','اختبار','TST-SYSTEM','نظام اختبار',
              'Person','TST-ENTITY','فرد اصطناعي','إجراء اصطناعي',
              '2025-01-01','2025-01-01T00:00:00Z','TST-CORR','TST-HASH','GENESIS');
            """;
        const string overlay = """
            INSERT INTO AuditLogPresentationEdits(AuditEventId,OverlayVersion,IsTombstoned,
              CorrectedDescription,Reason,EditedByUserId,EditedByDisplaySnapshot,EditedAtUtc)
            VALUES('TST-AUDIT-001',1,0,'عرض معدل','سبب اصطناعي',
              'TST-SYSTEM','نظام اختبار','2025-01-02T00:00:00Z');
            """;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _database.ExecuteInWriteTransactionAsync<bool>(async (connection, transaction) =>
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = audit + overlay;
                await insert.ExecuteNonQueryAsync();
                return await Task.FromException<bool>(
                    new InvalidOperationException("Simulated failure before commit"));
            }));
        using var inspect = _database.CreateOpenConnection();
        Assert.Equal(0L, Count(inspect, "SELECT COUNT(*) FROM AuditEvents;"));
        Assert.Equal(0L, Count(inspect, "SELECT COUNT(*) FROM AuditLogPresentationEdits;"));

        Execute(inspect, audit + overlay);
        Assert.Throws<SqliteException>(() => Execute(inspect,
            "UPDATE AuditEvents SET DefaultArabicDescription='عبث' WHERE AuditEventId='TST-AUDIT-001';"));
        Assert.Throws<SqliteException>(() => Execute(inspect,
            "DELETE FROM AuditEvents WHERE AuditEventId='TST-AUDIT-001';"));
        Assert.Throws<SqliteException>(() => Execute(inspect,
            "DELETE FROM AuditLogPresentationEdits WHERE AuditEventId='TST-AUDIT-001';"));
    }

    [Fact]
    public void Schema_HasRequiredTemporalAndOperationalIndexes()
    {
        using var connection = _database.CreateOpenConnection();
        foreach (string index in new[]
        {
            "IX_Persons_Active_ServiceNumberA",
            "IX_PersonIdentityHistory_Person_Dates",
            "IX_SiteNameHistory_Site_Dates",
            "IX_PersonAssignments_Person_Dates",
            "IX_PersonAssignments_Site_Dates",
            "IX_AttendanceRecords_Date_Site_Status",
            "IX_AttendanceRecords_Person_Date",
            "IX_PersonnelMovements_Person_Date",
            "IX_AuditLogPresentationEdits_Event"
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$name;";
            command.Parameters.AddWithValue("$name", index);
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        }
        Assert.Equal(1L, Count(connection, "PRAGMA foreign_keys;"));
    }
}
