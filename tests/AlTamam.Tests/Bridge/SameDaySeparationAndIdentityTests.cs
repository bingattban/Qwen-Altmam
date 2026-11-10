using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Real-operator first-day regressions. All data is synthetic and stored in
/// an isolated SQLCipher database; no real roster or database keys are used.
/// </summary>
public sealed class SameDaySeparationAndIdentityTests : IAsyncLifetime, IDisposable
{
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now =
            new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static readonly BridgePrincipal Actor = new(
        "TST-SAME-DAY-ADMIN", "tester",
        UserRole.GeneralAdministration, "ab12ab12ab12ab12ab12ab12ab12ab12");
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private readonly TestClock _clock = new();
    private AlTamamDbContext _db = null!;
    private string Today => _clock.GetLocalNow().ToString("yyyy-MM-dd");

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    private async Task<(string Id, string Assignment)> AddToday(string number)
    {
        var created = await new PersonRosterService(_db, _clock).CreatePersonAsync(
            Actor.UserId, Actor.Username, new JsonObject
            {
                ["visibleName"] = "فرد تجريبي أضيف اليوم",
                ["serviceNumberA"] = number,
                ["siteId"] = "SITE-UNSPECIFIED",
                ["firstRecordedLocalDate"] = Today
            }, CancellationToken.None);
        Assert.True(created.Success, created.UserMessage);
        var id = Assert.IsType<JsonObject>(created.Data)["personId"]!.GetValue<string>();
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT AssignmentId FROM PersonAssignments
            WHERE PersonId=$id AND ValidToLocalDate IS NULL;
            """;
        q.Parameters.AddWithValue("$id", id);
        return (id, Assert.IsType<string>(await q.ExecuteScalarAsync()));
    }

    private async Task Prepare(string personId, string assignment)
    {
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,
                StatusId,StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES($id,$day,'STS-PRESENT','موجود','م','#38BDF8',$assignment,
                'SITE-UNSPECIFIED','غير محدد','manual',$actor,$utc,1,$key);
            """;
        q.Parameters.AddWithValue("$id", personId);
        q.Parameters.AddWithValue("$day", Today);
        q.Parameters.AddWithValue("$assignment", assignment);
        q.Parameters.AddWithValue("$actor", Actor.UserId);
        q.Parameters.AddWithValue("$utc", _clock.GetUtcNow().ToString("O"));
        q.Parameters.AddWithValue("$key", Guid.NewGuid().ToString("N"));
        await q.ExecuteNonQueryAsync();
    }

    private long CountFor(string table, string personId)
    {
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM " + table + " WHERE PersonId=$id;";
        q.Parameters.AddWithValue("$id", personId);
        return Convert.ToInt64(q.ExecuteScalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstDaySeparation_AfterOptionalPreparation_KeepsEvidence(bool prepared)
    {
        var (id, assignment) = await AddToday(prepared ? "997701" : "997702");
        if (prepared) await Prepare(id, assignment);

        // A fabricated direct close without an immutable movement is still prohibited.
        using (var c = _db.CreateOpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                UPDATE PersonAssignments SET ValidToLocalDate=$today
                WHERE AssignmentId=$assignment;
                """;
            q.Parameters.AddWithValue("$today", Today);
            q.Parameters.AddWithValue("$assignment", assignment);
            Assert.Throws<SqliteException>(() => q.ExecuteNonQuery());
        }

        var svc = new PersonnelSeparationService(_db, _clock);
        var preview = await svc.PreviewAsync(new JsonObject
        {
            ["personId"] = id, ["businessDate"] = Today,
            ["reason"] = "سبب فصل تجريبي موثق"
        }, Actor, default);
        Assert.True(preview.Success, preview.UserMessage);
        string token = Assert.IsType<JsonObject>(preview.Data)["previewToken"]!.GetValue<string>();
        string operationId = "TST-SEP-" + Guid.NewGuid().ToString("N");
        var commit = new JsonObject { ["previewToken"] = token, ["operationId"] = operationId };

        // A five-second confirmation delay is enforced by the server.
        Assert.False((await svc.CommitAsync(commit, Actor, default)).Success);
        Assert.Equal(0L, CountFor("PersonnelMovements", id));
        _clock.Advance(TimeSpan.FromSeconds(6));
        var saved = await svc.CommitAsync(commit, Actor, default);
        Assert.True(saved.Success, saved.UserMessage);
        Assert.True((await svc.CommitAsync(commit, Actor, default)).Success);
        Assert.Equal(1L, CountFor("PersonnelMovements", id));
        Assert.Equal(prepared ? 1L : 0L, CountFor("AttendanceRecords", id));

        using (var c = _db.CreateOpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT ValidFromLocalDate, ValidToLocalDate FROM PersonAssignments
                WHERE AssignmentId=$assignment;
                """;
            q.Parameters.AddWithValue("$assignment", assignment);
            using (var rd = await q.ExecuteReaderAsync())
            {
                Assert.True(await rd.ReadAsync());
                Assert.Equal(Today, rd.GetString(0));
                Assert.Equal(Today, rd.GetString(1));
            }
            q.Parameters.Clear();
            q.CommandText = """
                SELECT ActiveFlag,OrganizationalStatus FROM Persons WHERE PersonId=$id;
                """;
            q.Parameters.AddWithValue("$id", id);
            using var person = await q.ExecuteReaderAsync();
            Assert.True(await person.ReadAsync());
            Assert.Equal(0, person.GetInt32(0));
            Assert.Equal(3, person.GetInt32(1));
        }

        var day = DateOnly.Parse(Today);
        var month = await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject
        {
            ["year"] = day.Year, ["month"] = day.Month,
            ["siteId"] = "SITE-UNSPECIFIED",
            ["archiveMode"] = true, ["page"] = 1, ["pageSize"] = 50
        }, default);
        Assert.True(month.Success, month.UserMessage);
        var rows = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(month.Data)["items"]);
        var member = Assert.Single(rows.OfType<JsonObject>(),
            x => x["personId"]!.GetValue<string>() == id);
        var cell = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(member["days"])[Today]);
        if (prepared)
        {
            Assert.Equal("م", cell["symbol"]!.GetValue<string>());
            Assert.Equal("separation", cell["movementKind"]!.GetValue<string>());
        }
        else Assert.Equal("ف", cell["symbol"]!.GetValue<string>());

        // Re-separation is prohibited; historical person and attendance remain.
        Assert.False((await svc.PreviewAsync(new JsonObject
        {
            ["personId"] = id, ["businessDate"] = Today,
            ["reason"] = "سبب فصل ثان غير صحيح"
        }, Actor, default)).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstDayIdentityCorrections_AreAudited_AndKeepAttendance(bool prepared)
    {
        var (id, assignment) = await AddToday(prepared ? "997801" : "997802");
        if (prepared) await Prepare(id, assignment);
        var svc = new PersonRosterService(_db, _clock);

        // A direct zero-length identity-history close, without audit evidence,
        // must fail at the SQLCipher boundary.
        using (var c = _db.CreateOpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                UPDATE PersonIdentityHistory SET ValidToLocalDate=$day
                WHERE PersonId=$id AND ValidToLocalDate IS NULL;
                """;
            q.Parameters.AddWithValue("$day", Today);
            q.Parameters.AddWithValue("$id", id);
            Assert.Throws<SqliteException>(() => q.ExecuteNonQuery());
        }

        var changes = new (string Name, string Number)[]
        {
            ("اسم مصحح أول للفرد", prepared ? "997801" : "997802"),
            ("اسم مصحح نهائي للفرد", prepared ? "997811" : "997812")
        };
        for (int i = 0; i < changes.Length; i++)
        {
            var edit = new JsonObject
            {
                ["personId"] = id, ["visibleName"] = changes[i].Name,
                ["serviceNumberA"] = changes[i].Number,
                ["effectiveLocalDate"] = Today,
                ["baseRevision"] = i + 1, ["expectedRevision"] = i + 1,
                ["operationId"] = "TST-ID-" + Guid.NewGuid().ToString("N"),
                ["reason"] = "تصحيح بيانات إدخال الفرد في يوم إضافته"
            };
            var preview = await svc.PreviewPersonIdentityUpdateAsync(edit, default);
            Assert.True(preview.Success, preview.UserMessage);
            var applied = await svc.UpdatePersonIdentityAsync(
                Actor.UserId, Actor.Username, edit, default);
            Assert.True(applied.Success, applied.UserMessage);
            var replay = await svc.UpdatePersonIdentityAsync(
                Actor.UserId, Actor.Username, edit, default);
            Assert.True(replay.Success, replay.UserMessage);
        }

        Assert.Equal(prepared ? 1L : 0L, CountFor("AttendanceRecords", id));
        Assert.Equal(3L, CountFor("PersonIdentityHistory", id));
        using var connection = _db.CreateOpenConnection();
        using var q2 = connection.CreateCommand();
        q2.CommandText = """
            SELECT CurrentVisibleName,CurrentServiceNumberA,RowVersion
            FROM Persons WHERE PersonId=$id;
            """;
        q2.Parameters.AddWithValue("$id", id);
        using (var row = await q2.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.Equal(changes[1].Name, row.GetString(0));
            Assert.Equal(changes[1].Number, row.GetString(1));
            Assert.Equal(3L, row.GetInt64(2));
        }
        q2.Parameters.Clear();
        q2.CommandText = """
            SELECT NameSnapshot,ValidFromLocalDate,ValidToLocalDate
            FROM PersonIdentityHistory WHERE PersonId=$id ORDER BY HistoryId;
            """;
        q2.Parameters.AddWithValue("$id", id);
        using (var history = await q2.ExecuteReaderAsync())
        {
            Assert.True(await history.ReadAsync());
            Assert.Equal("فرد تجريبي أضيف اليوم", history.GetString(0));
            Assert.Equal(Today, history.GetString(1));
            Assert.Equal(Today, history.GetString(2));
            Assert.True(await history.ReadAsync());
            Assert.Equal(changes[0].Name, history.GetString(0));
            Assert.Equal(Today, history.GetString(2));
            Assert.True(await history.ReadAsync());
            Assert.Equal(changes[1].Name, history.GetString(0));
            Assert.True(history.IsDBNull(2));
            Assert.False(await history.ReadAsync());
        }
        q2.Parameters.Clear();
        q2.CommandText = """
            SELECT COUNT(*) FROM AuditEvents
            WHERE TargetEntityId=$id AND OperationCode='PERSON_IDENTITY_UPDATE';
            """;
        q2.Parameters.AddWithValue("$id", id);
        Assert.Equal(2L, Convert.ToInt64(await q2.ExecuteScalarAsync()));

        var day = DateOnly.Parse(Today);
        var monthly = await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject
        {
            ["year"] = day.Year, ["month"] = day.Month, ["archiveMode"] = true,
            ["siteId"] = "SITE-UNSPECIFIED", ["page"] = 1, ["pageSize"] = 50
        }, default);
        Assert.True(monthly.Success, monthly.UserMessage);
        var items = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(monthly.Data)["items"]);
        var member = Assert.Single(items.OfType<JsonObject>(),
            x => x["personId"]!.GetValue<string>() == id);
        Assert.Equal(changes[1].Name, member["name"]!.GetValue<string>());
        if (prepared)
            Assert.Equal("م", Assert.IsType<JsonObject>(
                Assert.IsType<JsonObject>(member["days"])[Today])["symbol"]!.GetValue<string>());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
