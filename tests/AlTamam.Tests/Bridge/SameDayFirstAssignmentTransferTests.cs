using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Regression for a real Windows operator scenario: a person was added and
/// optionally prepared today, then transferred today. All saved evidence stays.
/// </summary>
public sealed class SameDayFirstAssignmentTransferTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new(
        "TST-SAMEDAY-ACTOR", "tester", UserRole.GeneralAdministration,
        "aabbccddeeff00112233445566778899");
    private static string Today() => TimeProvider.System.GetLocalNow().ToString("yyyy-MM-dd");

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,
                ActiveFlag,CreatedAtUtc,RowVersion)
            VALUES('SITE-TST-DEST','موقع الاختبار المستقبِل','موقع الاختبار المستقبِل',
                0,1,'2026-01-01T00:00:00Z',1);
            """;
        await q.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FirstDayTransfer_IsAtomicAndPreservesAttendance(
        bool external, bool prepared)
    {
        string date = Today();
        var create = await new PersonRosterService(_db).CreatePersonAsync(
            Actor.UserId, Actor.Username, new JsonObject
            {
                ["visibleName"] = "شخص تجريبي مضاف اليوم",
                ["serviceNumberA"] = external ? "990002" : "990001",
                ["siteId"] = "SITE-UNSPECIFIED"
            }, CancellationToken.None);
        Assert.True(create.Success, create.UserMessage);
        string personId = Assert.IsType<JsonObject>(create.Data)["personId"]!.GetValue<string>();

        string firstAssignment;
        using (var c = _db.CreateOpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT AssignmentId FROM PersonAssignments
                WHERE PersonId=$person AND ValidToLocalDate IS NULL;
                """;
            q.Parameters.AddWithValue("$person", personId);
            firstAssignment = Assert.IsType<string>(await q.ExecuteScalarAsync());
        }

        // SQLCipher must not accept a zero-day assignment without a committed
        // transfer movement in the SAME transaction.
        using (var c = _db.CreateOpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                UPDATE PersonAssignments SET ValidToLocalDate=$today
                WHERE AssignmentId=$assignment;
                """;
            q.Parameters.AddWithValue("$today", date);
            q.Parameters.AddWithValue("$assignment", firstAssignment);
            Assert.Throws<SqliteException>(() => q.ExecuteNonQuery());
        }

        if (prepared)
        {
            using var c = _db.CreateOpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,
                    StatusId,StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                    AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                    Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES($person,$date,'STS-PRESENT','موجود','م','#38BDF8',
                    $assignment,'SITE-UNSPECIFIED','غير محدد','manual',
                    $actor,$now,1,$key);
                """;
            q.Parameters.AddWithValue("$person", personId);
            q.Parameters.AddWithValue("$date", date);
            q.Parameters.AddWithValue("$assignment", firstAssignment);
            q.Parameters.AddWithValue("$actor", Actor.UserId);
            q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            q.Parameters.AddWithValue("$key", "TST-SAME-DAY-" + Guid.NewGuid().ToString("N"));
            await q.ExecuteNonQueryAsync();
        }

        var movements = new PersonnelMovementService(_db);
        var request = new JsonObject
        {
            ["businessDate"] = date,
            ["sourceSiteId"] = "SITE-UNSPECIFIED",
            ["destinationSiteId"] = external ? null : "SITE-TST-DEST",
            ["destinationExternalName"] = external ? "جهة خارجية تجريبية" : null,
            ["personIds"] = new JsonArray(personId)
        };
        var preview = await movements.PreviewAsync(request, Actor, external, default);
        Assert.True(preview.Success, preview.UserMessage);
        var token = Assert.IsType<JsonObject>(preview.Data)["previewToken"]!.GetValue<string>();
        var commitRequest = new JsonObject
        {
            ["previewToken"] = token,
            ["operationId"] = "TST-SAME-DAY-" + Guid.NewGuid().ToString("N")
        };
        var confirmed = await movements.CommitAsync(commitRequest, Actor, external, default);
        Assert.True(confirmed.Success, confirmed.UserMessage);
        Assert.Equal(1, Assert.IsType<JsonObject>(confirmed.Data)["count"]!.GetValue<int>());

        // Retry of the identical commit returns the stored result, not another movement.
        var replay = await movements.CommitAsync(commitRequest, Actor, external, default);
        Assert.True(replay.Success, replay.UserMessage);

        using (var c = _db.CreateOpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM PersonnelMovements WHERE PersonId=$person;";
            q.Parameters.AddWithValue("$person", personId);
            Assert.Equal(1L, Convert.ToInt64(await q.ExecuteScalarAsync()));

            q.CommandText = """
                SELECT ValidFromLocalDate,ValidToLocalDate FROM PersonAssignments
                WHERE AssignmentId=$assignment;
                """;
            q.Parameters.AddWithValue("$assignment", firstAssignment);
            using (var reader = await q.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(date, reader.GetString(0));
                Assert.Equal(date, reader.GetString(1));
            }

            q.CommandText = "SELECT COUNT(*) FROM AttendanceRecords WHERE PersonId=$person;";
            Assert.Equal(prepared ? 1L : 0L, Convert.ToInt64(await q.ExecuteScalarAsync()));
            if (!external)
            {
                q.CommandText = """
                    SELECT COUNT(*) FROM PersonAssignments
                    WHERE PersonId=$person AND SiteId='SITE-TST-DEST'
                      AND ValidFromLocalDate=$day AND ValidToLocalDate IS NULL;
                    """;
                q.Parameters.AddWithValue("$day", date);
                Assert.Equal(1L, Convert.ToInt64(await q.ExecuteScalarAsync()));
            }
            else
            {
                q.CommandText = """
                    SELECT COUNT(*) FROM Persons WHERE PersonId=$person
                        AND OrganizationalStatus=2 AND ActiveFlag=0;
                    """;
                Assert.Equal(1L, Convert.ToInt64(await q.ExecuteScalarAsync()));
            }
        }

        // The original attendance is still visible in its original site's
        // historical month, while the movement is independently annotated.
        var day = DateOnly.Parse(date);
        var monthly = await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject
        {
            ["year"] = day.Year, ["month"] = day.Month,
            ["siteId"] = "SITE-UNSPECIFIED", ["archiveMode"] = true,
            ["page"] = 1, ["pageSize"] = 50
        }, default);
        Assert.True(monthly.Success, monthly.UserMessage);
        var items = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(monthly.Data)["items"]);
        var row = Assert.Single(items.OfType<JsonObject>(), p =>
            p["personId"]!.GetValue<string>() == personId);
        var cell = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(row["days"])[date]);
        if (prepared)
        {
            Assert.Equal("م", cell["symbol"]!.GetValue<string>());
            Assert.Equal(external ? "external-transfer" : "internal-transfer",
                cell["movementKind"]!.GetValue<string>());
        }
        else
            Assert.Equal(external ? "نقل (خ)" : "نقل (د)",
                cell["symbol"]!.GetValue<string>());

        if (!external)
        {
            var duplicate = new JsonObject
            {
                ["businessDate"] = date,
                ["sourceSiteId"] = "SITE-TST-DEST",
                ["destinationSiteId"] = "SITE-UNSPECIFIED",
                ["personIds"] = new JsonArray(personId)
            };
            Assert.False((await movements.PreviewAsync(
                duplicate, Actor, false, default)).Success);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
