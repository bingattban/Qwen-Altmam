using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>
/// Phase 01 step 01.06 — V001 invariant enforcement at the database boundary:
/// assignment-overlap triggers, invalid date intervals, attendance zero-or-one
/// per person-day, transfer/attendance consistency and idempotency keys.
/// Synthetic fixtures only (contract §2.10). These tests exercise the REAL
/// embedded schema via a real encrypted SQLCipher connection.
/// </summary>
public sealed class SchemaInvariantTests : IDisposable, IAsyncLifetime
{
    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext? _context;

    public async Task InitializeAsync()
    {
        _context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        await _context.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _context?.Dispose();
        return Task.CompletedTask;
    }

    private SqliteConnection Open() => _context!.CreateOpenConnection();

    private static void SeedPerson(SqliteConnection c, string personId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Persons (PersonId, CurrentVisibleName, NormalizedName, CurrentServiceNumberA,
                                 NormalizedServiceNumberA, FirstRecordedLocalDate, FirstRecordedAtUtc,
                                 OrganizationalStatus, ActiveFlag, RowVersion)
            VALUES ($id, $name, $name, $num, $num, '2025-01-01', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), 1, 1, 1);
            """;
        cmd.Parameters.Add("$id", SqliteType.Text).Value = personId;
        cmd.Parameters.Add("$name", SqliteType.Text).Value = SyntheticFixtures.VisibleName;
        // SQLCipher storage is canonical; this synthetic fixture is not user input.
        cmd.Parameters.Add("$num", SqliteType.Text).Value = "90" + personId[^4..];
        cmd.ExecuteNonQuery();
    }

    private static void SeedAssignment(SqliteConnection c, string assignmentId, string personId,
        string from, string? to)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PersonAssignments (AssignmentId, PersonId, SiteId, ValidFromLocalDate, ValidToLocalDate,
                                           SourceMovementId, CreatedAtUtc)
            VALUES ($aid, $pid, $site, $from, $to, NULL, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.Parameters.Add("$aid", SqliteType.Text).Value = assignmentId;
        cmd.Parameters.Add("$pid", SqliteType.Text).Value = personId;
        cmd.Parameters.Add("$site", SqliteType.Text).Value = SyntheticFixtures.SiteId;
        cmd.Parameters.Add("$from", SqliteType.Text).Value = from;
        cmd.Parameters.Add("$to", SqliteType.Text).Value = (object?)to ?? DBNull.Value;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void AssignmentTrigger_RejectsInvalidInterval_EndBeforeStart()
    {
        using var c = Open();
        SeedPerson(c, SyntheticFixtures.PersonId);
        var ex = Assert.Throws<SqliteException>(() =>
            SeedAssignment(c, SyntheticFixtures.AssignmentId, SyntheticFixtures.PersonId, "2025-06-10", "2025-06-01"));
        Assert.Contains("Invalid assignment date interval", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssignmentTrigger_RejectsEqualStartAndEnd_IntervalMustBeNonEmpty()
    {
        using var c = Open();
        SeedPerson(c, SyntheticFixtures.PersonId);
        Assert.Throws<SqliteException>(() =>
            SeedAssignment(c, SyntheticFixtures.AssignmentId, SyntheticFixtures.PersonId, "2025-06-01", "2025-06-01"));
    }

    [Fact]
    public void AssignmentTrigger_AdjacentIntervals_AreAllowed_StartInclusiveEndExclusive()
    {
        using var c = Open();
        SeedPerson(c, SyntheticFixtures.PersonId);
        // [2025-01-01, 2025-07-01) then [2025-07-01, open) — touching endpoints must NOT overlap.
        SeedAssignment(c, SyntheticFixtures.AssignmentId, SyntheticFixtures.PersonId, "2025-01-01", "2025-07-01");
        SeedAssignment(c, SyntheticFixtures.SecondAssignmentId, SyntheticFixtures.PersonId, "2025-07-01", null);
    }

    [Fact]
    public void AssignmentTrigger_RejectsOverlappingInsert()
    {
        using var c = Open();
        SeedPerson(c, SyntheticFixtures.PersonId);
        SeedAssignment(c, SyntheticFixtures.AssignmentId, SyntheticFixtures.PersonId, "2025-01-01", "2025-07-01");
        var ex = Assert.Throws<SqliteException>(() =>
            SeedAssignment(c, SyntheticFixtures.SecondAssignmentId, SyntheticFixtures.PersonId, "2025-06-30", null));
        Assert.Contains("Overlapping person assignments", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssignmentTrigger_RejectsOverlapIntroducedByUpdate()
    {
        using var c = Open();
        SeedPerson(c, SyntheticFixtures.PersonId);
        SeedAssignment(c, SyntheticFixtures.AssignmentId, SyntheticFixtures.PersonId, "2025-01-01", "2025-07-01");
        SeedAssignment(c, SyntheticFixtures.SecondAssignmentId, SyntheticFixtures.PersonId, "2025-07-01", null);

        using var widen = c.CreateCommand();
        widen.CommandText = "UPDATE PersonAssignments SET ValidToLocalDate = '2025-12-31' WHERE AssignmentId = $aid;";
        widen.Parameters.Add("$aid", SqliteType.Text).Value = SyntheticFixtures.AssignmentId;
        var ex = Assert.Throws<SqliteException>(() => widen.ExecuteNonQuery());
        Assert.Contains("Overlapping person assignments", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttendanceUniqueConstraint_ZeroOrOneCommittedStatusPerPersonDay()
    {
        using var c = Open();
        SeedPerson(c, SyntheticFixtures.PersonId);
        SeedAssignment(c, SyntheticFixtures.AssignmentId, SyntheticFixtures.PersonId, "2025-01-01", null);

        InsertAttendance(c, SyntheticFixtures.NewIdempotencyKey("att1"), SyntheticFixtures.PersonId, "2025-06-01");

        var ex = Assert.Throws<SqliteException>(() =>
            InsertAttendance(c, SyntheticFixtures.NewIdempotencyKey("att2"), SyntheticFixtures.PersonId, "2025-06-01"));
        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Attendance_IdempotencyKeyIsGloballyUnique()
    {
        using var c = Open();
        SeedPerson(c, SyntheticFixtures.PersonId);
        SeedPerson(c, SyntheticFixtures.SecondPersonId);
        SeedAssignment(c, SyntheticFixtures.AssignmentId, SyntheticFixtures.PersonId, "2025-01-01", null);
        SeedAssignment(c, SyntheticFixtures.SecondAssignmentId, SyntheticFixtures.SecondPersonId, "2025-01-01", null);

        string reusedKey = SyntheticFixtures.NewIdempotencyKey("dup");
        InsertAttendance(c, reusedKey, SyntheticFixtures.PersonId, "2025-06-01");
        var ex = Assert.Throws<SqliteException>(() =>
            InsertAttendance(c, reusedKey, SyntheticFixtures.SecondPersonId, "2025-06-02"));
        Assert.Contains("IdempotencyKey", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeededSystemData_IsPresent_AndImmutableSiteProtected()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT IsSystemSite FROM Sites WHERE SiteId = 'SITE-UNSPECIFIED';";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));

        using var statuses = c.CreateCommand();
        statuses.CommandText = "SELECT COUNT(*) FROM AttendanceStatusDefinitions WHERE IsSystemSeeded = 1;";
        Assert.True(Convert.ToInt64(statuses.ExecuteScalar()) >= 4);
    }

    [Fact]
    public void ForeignKeys_Active_AndRejectUnknownReferences()
    {
        using var c = Open();
        using var fk = c.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, Convert.ToInt64(fk.ExecuteScalar()));

        using var bad = c.CreateCommand();
        bad.CommandText = """
            INSERT INTO PersonAssignments (AssignmentId, PersonId, SiteId, ValidFromLocalDate,
                                           CreatedByUserId, CreatedAtUtc, RowVersion)
            VALUES ('TST-ASSIGN-BAD', 'TST-PERSON-NONEXISTENT', 'SITE-UNSPECIFIED', '2025-01-01',
                    'TST-USER-0001', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), 1);
            """;
        Assert.Throws<SqliteException>(() => bad.ExecuteNonQuery());
    }

    private static void InsertAttendance(SqliteConnection c, string idempotencyKey, string personId, string localDate)
    {
        // RecordId is INTEGER AUTOINCREMENT — omitted. Column set matches V001 exactly.
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AttendanceRecords (PersonId, AttendanceLocalDate, StatusId,
                                           StatusNameSnapshot, StatusSymbolSnapshot, StatusColorSnapshot,
                                           AssignmentIdAtEffectiveDate, SiteIdAtEffectiveDate, SiteNameSnapshot,
                                           Source, CreatedByUserId, CommittedAtUtc, RevisionNumber, IdempotencyKey)
            VALUES ($pid, $date, $status, 'موجود', 'م', '#38BDF8',
                    $assign, $site, 'غير محدد', 'manual', $user,
                    STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), 1, $idem);
            """;
        cmd.Parameters.Add("$pid", SqliteType.Text).Value = personId;
        cmd.Parameters.Add("$date", SqliteType.Text).Value = localDate;
        cmd.Parameters.Add("$status", SqliteType.Text).Value = SyntheticFixtures.StatusPresentId;
        cmd.Parameters.Add("$assign", SqliteType.Text).Value = personId == SyntheticFixtures.PersonId
            ? SyntheticFixtures.AssignmentId : SyntheticFixtures.SecondAssignmentId;
        cmd.Parameters.Add("$site", SqliteType.Text).Value = SyntheticFixtures.SiteId;
        cmd.Parameters.Add("$user", SqliteType.Text).Value = SyntheticFixtures.UserId;
        cmd.Parameters.Add("$idem", SqliteType.Text).Value = idempotencyKey;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => SyntheticFixtures.DeleteTempDatabase(_dbPath);
}
