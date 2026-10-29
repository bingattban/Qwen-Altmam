using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>2026-10-26 exception: manual copy of yesterday for 1–100 unprepared people.</summary>
public sealed class ManualCarryForwardIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private readonly SyntheticDaytimeClock _clock = new(12, 0);
    private static readonly BridgePrincipal Actor = new("TST-CARRY-ACTOR","carry-admin",
        UserRole.OrdinaryAdministration,"1234567890abcdef1234567890abcdef");
    private string Today => _clock.GetLocalNow().ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private string Yesterday => DateOnly.ParseExact(Today,"yyyy-MM-dd",CultureInfo.InvariantCulture)
        .AddDays(-1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var connection = _db.CreateOpenConnection();
        for(int i=1;i<=3;i++)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                    NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES ($id,$name,$name,$number,$number,$date,'2026-01-01T00:00:00Z',1,1,1);
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES($assignment,$id,'SITE-UNSPECIFIED',$date,NULL,NULL,'2026-01-01T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$id","TST-CARRY-P"+i);
            cmd.Parameters.AddWithValue("$assignment","TST-CARRY-A"+i);
            cmd.Parameters.AddWithValue("$name","فرد اختبار "+i);
            cmd.Parameters.AddWithValue("$number","92"+i.ToString("D4"));
            cmd.Parameters.AddWithValue("$date",Yesterday);
            await cmd.ExecuteNonQueryAsync();
        }
        using var existing = connection.CreateCommand();
        existing.CommandText = """
            INSERT INTO AttendanceRecords
              (PersonId,AttendanceLocalDate,StatusId,StatusNameSnapshot,
               StatusSymbolSnapshot,StatusColorSnapshot,AssignmentIdAtEffectiveDate,
               SiteIdAtEffectiveDate,SiteNameSnapshot,Source,CreatedByUserId,
               CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES ('TST-CARRY-P1',$yesterday,'STS-ABSENT','غياب','غ','#EF4444',
                    'TST-CARRY-A1','SITE-UNSPECIFIED','غير محدد','manual',
                    'TST-USER','2026-01-01T00:00:00Z',1,'TST-CARRY-YESTERDAY'),
                   ('TST-CARRY-P3',$today,'STS-PRESENT','موجود','م','#38BDF8',
                    'TST-CARRY-A3','SITE-UNSPECIFIED','غير محدد','manual',
                    'TST-USER','2026-01-01T00:00:00Z',1,'TST-CARRY-MANUAL');
            """;
        existing.Parameters.AddWithValue("$yesterday",Yesterday);
        existing.Parameters.AddWithValue("$today",Today);
        await existing.ExecuteNonQueryAsync();
    }

    private JsonObject Selection(params string[] ids) => new()
    {
        ["businessDate"] = Today, ["mode"] = "carry", ["siteId"] = "SITE-UNSPECIFIED",
        ["personIds"] = new JsonArray(ids.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray())
    };
    private static JsonObject Commit(JsonObject preview) => new()
    {
        ["previewToken"] = preview["previewToken"]!.GetValue<string>(),
        ["mode"] = "carry"
    };
    private long Count(string table)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM "+table;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private async Task ConfigureDefaultAsync()
    {
        var result = await new AutoAttendancePolicyService(_db,_clock).SetAsync(
            new JsonObject {["statusId"]="STS-PERMISSION",["operationId"]="CARRY-SET-DEFAULT-0001"},
            Actor,default);
        Assert.True(result.Success,result.UserMessage);
    }

    [Fact]
    public async Task PreviewWithoutFallbackRejectsWholeBatchAndWritesNothing()
    {
        var service = new ManualCarryForwardService(_db,_clock);
        var preview = await service.PreviewAsync(Selection("TST-CARRY-P1","TST-CARRY-P2"),Actor,default);
        Assert.False(preview.Success);
        Assert.Contains("920002",preview.UserMessage);
        Assert.Equal(2,Count("AttendanceRecords"));
    }

    [Fact]
    public async Task MixedPerPersonStatusesAreAtomicAuditedAndNeverOverrideManual()
    {
        await ConfigureDefaultAsync();
        var service = new ManualCarryForwardService(_db,_clock);
        var preview = await service.PreviewAsync(Selection("TST-CARRY-P1","TST-CARRY-P2"),Actor,default);
        Assert.True(preview.Success,preview.UserMessage);
        var summary = Assert.IsType<JsonObject>(preview.Data);
        var people = Assert.IsType<JsonArray>(summary["persons"]);
        Assert.Equal("STS-ABSENT",people[0]!["proposedStatusId"]!.GetValue<string>());
        Assert.Equal("STS-PERMISSION",people[1]!["proposedStatusId"]!.GetValue<string>());
        Assert.True(people[1]!["usedDefault"]!.GetValue<bool>());
        Assert.Equal(2L,Count("AttendanceRecords")); // preview only
        var committed = await service.CommitAsync(Commit(summary),Actor,default);
        Assert.True(committed.Success,committed.UserMessage);
        Assert.Equal(4L,Count("AttendanceRecords"));
        Assert.Equal(3L,Count("AuditEvents")); // default selection + two commits
        using var c = _db.CreateOpenConnection();
        using var rows = c.CreateCommand();
        rows.CommandText = "SELECT PersonId,StatusId FROM AttendanceRecords WHERE AttendanceLocalDate=$today ORDER BY PersonId;";
        rows.Parameters.AddWithValue("$today",Today);
        using var reader = rows.ExecuteReader();
        var actual = new List<(string, string)>();
        while(reader.Read())actual.Add((reader.GetString(0),reader.GetString(1)));
        Assert.Equal(new[]
        {
            ("TST-CARRY-P1","STS-ABSENT"),
            ("TST-CARRY-P2","STS-PERMISSION"),
            ("TST-CARRY-P3","STS-PRESENT")
        },actual);
        var replay = await service.CommitAsync(Commit(summary),Actor,default);
        Assert.False(replay.Success);
        Assert.Equal(4L,Count("AttendanceRecords"));
    }


    [Fact]
    public async Task ManualCarry_LostResponseRetry_DoesNotDuplicateAttendanceOrAudit()
    {
        await ConfigureDefaultAsync();
        var service=new ManualCarryForwardService(_db,_clock);
        var preview=await service.PreviewAsync(
            Selection("TST-CARRY-P1","TST-CARRY-P2"),Actor,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        var payload=Commit((JsonObject)preview.Data!);
        payload["operationId"]="section02-manual-carry-batch-one";
        var first=await service.CommitAsync(payload,Actor,CancellationToken.None);
        Assert.True(first.Success,first.UserMessage);
        var retry=await service.CommitAsync((JsonObject)payload.DeepClone(),
            Actor,CancellationToken.None);
        Assert.True(retry.Success,retry.UserMessage);
        Assert.Equal(((JsonObject)first.Data!).ToJsonString(),
            ((JsonObject)retry.Data!).ToJsonString());
        Assert.Equal(4L,Count("AttendanceRecords")); // two historical + two committed
        Assert.Equal(3L,Count("AuditEvents")); // policy + two approvals
        Assert.Equal(2L,Count("MutationReceipts")); // policy + one batch
        var changed=(JsonObject)payload.DeepClone();
        changed["previewToken"]=new string('f',64);
        Assert.False((await service.CommitAsync(changed,Actor,CancellationToken.None)).Success);
    }

    [Fact]
    public async Task AlreadyPreparedOrChangedAfterPreviewBlocksEntireBatch()
    {
        await ConfigureDefaultAsync();
        var service=new ManualCarryForwardService(_db,_clock);
        var already = await service.PreviewAsync(Selection("TST-CARRY-P1","TST-CARRY-P3"),Actor,default);
        Assert.False(already.Success);
        Assert.Contains("فرد اختبار 3",already.UserMessage);
        Assert.Contains("920003",already.UserMessage);
        var preview=await service.PreviewAsync(Selection("TST-CARRY-P1","TST-CARRY-P2"),Actor,default);
        Assert.True(preview.Success,preview.UserMessage);
        using (var c=_db.CreateOpenConnection())
        using (var mutate=c.CreateCommand())
        {
            mutate.CommandText="UPDATE AttendanceRecords SET StatusNameSnapshot='غياب معدل' WHERE PersonId='TST-CARRY-P1' AND AttendanceLocalDate=$day;";
            mutate.Parameters.AddWithValue("$day",Yesterday);
            await mutate.ExecuteNonQueryAsync();
        }
        var denied=await service.CommitAsync(Commit((JsonObject)preview.Data!),Actor,default);
        Assert.False(denied.Success);
        Assert.Contains("فرد اختبار 1",denied.UserMessage);
        Assert.Equal(2L,Count("AttendanceRecords"));
    }

    [Fact]
    public async Task TwentyHourCutoffRejectsOldPreviewAndNewSelection()
    {
        await ConfigureDefaultAsync();
        var service=new ManualCarryForwardService(_db,_clock);
        var preview=await service.PreviewAsync(Selection("TST-CARRY-P1"),Actor,default);
        Assert.True(preview.Success,preview.UserMessage);
        _clock.SetLocalTime(20,0);
        var expired=await service.CommitAsync(Commit((JsonObject)preview.Data!),Actor,default);
        Assert.False(expired.Success);
        var blocked=await service.PreviewAsync(Selection("TST-CARRY-P2"),Actor,default);
        Assert.False(blocked.Success);
        Assert.Equal(2L,Count("AttendanceRecords"));
    }
    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose(){_db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);}
}
