using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Application.Scheduling;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Duration status must be a durable, date-bound SQLCipher instruction,
/// never a fabricated UI-only status or an unbounded yesterday carry-forward.</summary>
public sealed class AttendanceDurationIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new("TST-PERIOD-ADMIN", "test-period",
        UserRole.GeneralAdministration, "aaaabbbbccccddddeeeeffff00001111");
    private static DateOnly Today => DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().DateTime);
    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c = _db.CreateOpenConnection();
        for (var i = 1; i <= 2; i++)
        {
            using var seed = c.CreateCommand();
            seed.CommandText = """
                INSERT INTO Persons(PersonId, CurrentVisibleName, NormalizedName,
                    CurrentServiceNumberA, NormalizedServiceNumberA, FirstRecordedLocalDate,
                    FirstRecordedAtUtc, OrganizationalStatus, ActiveFlag, RowVersion)
                VALUES($p,$n,$n,$no,$no,$day,$now,1,1,1);
                INSERT INTO PersonAssignments(AssignmentId, PersonId, SiteId,
                    ValidFromLocalDate, ValidToLocalDate, SourceMovementId, CreatedAtUtc)
                VALUES($a,$p,'SITE-UNSPECIFIED',$day,NULL,NULL,$now);
                """;
            seed.Parameters.AddWithValue("$p", $"TST-PERIOD-P{i}");
            seed.Parameters.AddWithValue("$a", $"TST-PERIOD-A{i}");
            seed.Parameters.AddWithValue("$n", $"فرد تجربة المدة {i}");
            seed.Parameters.AddWithValue("$no", $"94100{i}");
            seed.Parameters.AddWithValue("$day", Iso(Today.AddDays(-1)));
            seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await seed.ExecuteNonQueryAsync();
        }
    }
    private static JsonObject Request(string status, string mode = "initial", int length = 2,
        int startDays = 0, params string[] people)
    {
        var start = Today.AddDays(startDays);
        return new JsonObject
        {
            ["businessDate"] = Iso(Today),
            ["statusId"] = status,
            ["mode"] = mode,
            ["siteId"] = "SITE-UNSPECIFIED",
            ["periodFrom"] = Iso(start),
            ["periodTo"] = Iso(start.AddDays(length)),
            ["personIds"] = new JsonArray((people.Length == 0
                ? ["TST-PERIOD-P1"] : people).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray())
        };
    }
    private static string Token(BridgeResponse response)
    {
        Assert.True(response.Success, response.UserMessage);
        return Assert.IsType<JsonObject>(response.Data)["previewToken"]!.GetValue<string>();
    }
    private async Task<BridgeResponse> Commit(JsonObject payload)
    {
        var service = new AttendanceCommitService(_db, new SyntheticDaytimeClock());
        var token = Token(await service.PreviewAsync(payload, Actor, default));
        return await service.CommitAsync(new JsonObject
        {
            ["previewToken"] = token,
            ["operationId"] = "duration-" + Guid.NewGuid().ToString("N")
        }, Actor, payload["mode"]!.GetValue<string>() == "revise", default);
    }
    private long Count(string table)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private string? Status(string personId, DateOnly date)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT StatusId FROM AttendanceRecords WHERE PersonId=$p AND AttendanceLocalDate=$d;";
        cmd.Parameters.AddWithValue("$p", personId);
        cmd.Parameters.AddWithValue("$d", Iso(date));
        return cmd.ExecuteScalar() as string;
    }

    [Theory]
    [InlineData("STS-LEAVE")]
    [InlineData("STS-PERMISSION")]
    public async Task RequiredDates_AreServerValidated_ForTheTwoCanonicalStatusesOnly(string status)
    {
        var service = new AttendanceCommitService(_db, new SyntheticDaytimeClock());
        var missing = Request(status);
        missing.Remove("periodFrom");
        Assert.Equal(BridgeErrorCodes.ValidationError,
            (await service.PreviewAsync(missing, Actor, default)).ErrorCode);
        missing = Request(status);
        missing.Remove("periodTo");
        Assert.Equal(BridgeErrorCodes.ValidationError,
            (await service.PreviewAsync(missing, Actor, default)).ErrorCode);
        var backwards = Request(status);
        backwards["periodTo"] = Iso(Today.AddDays(-1));
        Assert.Equal(BridgeErrorCodes.ValidationError,
            (await service.PreviewAsync(backwards, Actor, default)).ErrorCode);
        var past = Request(status);
        past["periodFrom"] = Iso(Today.AddDays(-1));
        Assert.Equal(BridgeErrorCodes.ValidationError,
            (await service.PreviewAsync(past, Actor, default)).ErrorCode);
        var other = Request("STS-PRESENT");
        Assert.Equal(BridgeErrorCodes.ValidationError,
            (await service.PreviewAsync(other, Actor, default)).ErrorCode);
        other.Remove("periodFrom"); other.Remove("periodTo");
        Assert.True((await service.PreviewAsync(other, Actor, default)).Success);
    }

    [Theory]
    [InlineData("STS-LEAVE")]
    [InlineData("STS-PERMISSION")]
    public async Task RealSqlCipherRange_OverridesYesterdayAndStopsAfterEnd(string status)
    {
        using (var c = _db.CreateOpenConnection())
        using (var setting = c.CreateCommand())
        {
            setting.CommandText = """
                INSERT INTO AppSettings(SettingKey, SettingValue, UpdatedAtUtc)
                VALUES('AutoAttendanceDefaultStatusId','STS-PRESENT',$now);
                """;
            setting.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await setting.ExecuteNonQueryAsync();
        }
        var saved = await Commit(Request(status, "initial", 2, 0, "TST-PERIOD-P1","TST-PERIOD-P2"));
        Assert.True(saved.Success, saved.UserMessage);
        Assert.Equal(2L, Count("AttendanceStatusPeriods"));
        Assert.Equal(status, Status("TST-PERIOD-P1", Today));
        var schedule = new AutoAttendanceService(_db, new SyntheticDaytimeClock());
        for (int i = 1; i <= 2; i++)
        {
            var result = await schedule.RunForDateAsync(Today.AddDays(i), false);
            Assert.True(result.WasCommitted);
            Assert.Equal(status, Status("TST-PERIOD-P1", Today.AddDays(i)));
            Assert.Equal(status, Status("TST-PERIOD-P2", Today.AddDays(i)));
        }
        var expiry = await schedule.RunForDateAsync(Today.AddDays(3), false);
        Assert.True(expiry.WasCommitted);
        Assert.Equal("STS-PRESENT", Status("TST-PERIOD-P1", Today.AddDays(3)));
        Assert.Equal("STS-PRESENT", Status("TST-PERIOD-P2", Today.AddDays(3)));
    }

    [Fact]
    public async Task ScheduledTomorrow_DoesNotRewriteTodaysAttendance()
    {
        var stored = await Commit(Request("STS-LEAVE", "initial", 1, 1));
        Assert.True(stored.Success, stored.UserMessage);
        Assert.Equal(1L, Count("AttendanceStatusPeriods"));
        Assert.Null(Status("TST-PERIOD-P1", Today));
        await new AutoAttendanceService(_db, new SyntheticDaytimeClock())
            .RunForDateAsync(Today.AddDays(1), false);
        Assert.Equal("STS-LEAVE", Status("TST-PERIOD-P1", Today.AddDays(1)));
    }

    [Fact]
    public async Task ManualRevisionAwayFromLeave_StopsNextDayPeriod()
    {
        Assert.True((await Commit(Request("STS-LEAVE"))).Success);
        var present = Request("STS-PRESENT","revise");
        present.Remove("periodFrom"); present.Remove("periodTo");
        Assert.True((await Commit(present)).Success);
        using (var c = _db.CreateOpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT CancelledFromLocalDate FROM AttendanceStatusPeriods WHERE PersonId='TST-PERIOD-P1';";
            Assert.Equal(Iso(Today), q.ExecuteScalar() as string);
        }
        await new AutoAttendanceService(_db, new SyntheticDaytimeClock())
            .RunForDateAsync(Today.AddDays(1), false);
        Assert.Equal("STS-PRESENT", Status("TST-PERIOD-P1", Today.AddDays(1)));
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
