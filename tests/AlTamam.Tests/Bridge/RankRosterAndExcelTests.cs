using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Rank is a single persisted PERSON value, presented immediately after service
/// number in both interactive rosters and the actual generated Excel workbooks.
/// A rank must never be duplicated as a trailing optional roster column.
/// </summary>
public sealed class RankRosterAndExcelTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "altamam-rank-tests-" + Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new("TST-RANK-ADMIN",
        "rank-test-admin", UserRole.GeneralAdministration,
        "0123456789abcdef0123456789abcdef");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _db = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
              CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
              FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-RANK-PERSON','فرد الرتبة','فرد الرتبة','98451','98451',
              '2026-10-01','2026-10-01T08:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
              ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-RANK-PERSON','فرد الرتبة','98451','2026-10-01',NULL,
              'TST-RANK-ADMIN','2026-10-01T08:00:00Z','test');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
              ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-RANK-ASSIGN','TST-RANK-PERSON','SITE-UNSPECIFIED',
              '2026-10-01',NULL,NULL,'2026-10-01T08:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
              StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
              AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
              Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
            VALUES('TST-RANK-PERSON','2026-10-19','STS-PRESENT','موجود','م','#22c55e',
              'TST-RANK-ASSIGN','SITE-UNSPECIFIED','غير محدد','manual',
              'TST-RANK-ADMIN','2026-10-19T08:00:00Z','TST-RANK-ATT');
            INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
            VALUES('PreferredBackupDirectory',$folder,'2026-10-19T08:00:00Z')
            ON CONFLICT(SettingKey) DO UPDATE SET SettingValue=excluded.SettingValue;
            """;
        command.Parameters.AddWithValue("$folder", _directory);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> RankColumnAsync()
    {
        using var conn = _db.CreateOpenConnection();
        using var q = conn.CreateCommand();
        q.CommandText = """
            SELECT FieldDefinitionId FROM CustomFieldDefinitions
            WHERE ArabicCaption='الرتبة' AND ActiveFlag=1 AND VisibleFlag=1;
            """;
        return Assert.IsType<string>(await q.ExecuteScalarAsync());
    }

    private static string[] ExcelRow(string file, int sheet, int row)
    {
        using var zip = ZipFile.OpenRead(file);
        using var stream = zip.GetEntry($"xl/worksheets/sheet{sheet}.xml")!.Open();
        var xml = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var element = xml.Descendants(ns + "row").ElementAt(row);
        return element.Elements(ns + "c").Select(c =>
            c.Element(ns + "is")?.Element(ns + "t")?.Value ??
            c.Element(ns + "v")?.Value ?? "").ToArray();
    }

    [Fact]
    public async Task Rank_IsSeededEditableAndProjectedToAllPersonnelReads()
    {
        string field = await RankColumnAsync();
        var change = await new RosterCustomFieldValueService(_db).UpdateAsync(new JsonObject
        {
            ["personId"] = "TST-RANK-PERSON", ["expectedRevision"] = 1L,
            ["values"] = new JsonObject { [field] = "رقيب" },
            ["operationId"] = Guid.NewGuid().ToString("N")
        }, Actor, default);
        Assert.True(change.Success, change.UserMessage);

        var roster = await new PersonRosterService(_db).QueryPersonsAsync(
            new JsonObject { ["term"] = "98451" }, default);
        var rosterData = Assert.IsType<JsonObject>(roster.Data);
        var person = Assert.IsType<JsonObject>(
            Assert.Single(Assert.IsType<JsonArray>(rosterData["items"])));
        Assert.Equal("رقيب", person["rank"]!.GetValue<string>());

        var read = new DailyMonthlyReadService(_db);
        var daily = await read.DailyRosterAsync(new JsonObject
        {
            ["businessDate"] = "2026-10-19", ["term"] = "98451"
        }, default);
        Assert.True(daily.Success, daily.UserMessage);
        Assert.Equal("رقيب", Assert.IsType<JsonObject>(
            Assert.Single(Assert.IsType<JsonArray>(((JsonObject)daily.Data!)["items"])))["rank"]!.GetValue<string>());

        var monthly = await read.MonthlyGridAsync(new JsonObject
        {
            ["year"] = 2026, ["month"] = 10, ["term"] = "98451",
            ["archiveMode"] = true
        }, default);
        Assert.True(monthly.Success, monthly.UserMessage);
        Assert.Equal("رقيب", Assert.IsType<JsonObject>(
            Assert.Single(Assert.IsType<JsonArray>(((JsonObject)monthly.Data!)["items"])))["rank"]!.GetValue<string>());

        var report = await new PersonnelReportService(_db).ReportAsync(new JsonObject
        {
            ["personId"] = "TST-RANK-PERSON"
        }, default);
        Assert.True(report.Success, report.UserMessage);
        Assert.Equal("رقيب", ((JsonObject)((JsonObject)report.Data!)["person"]!)["rank"]!.GetValue<string>());
    }

    [Fact]
    public async Task MonthlyExcel_RankOccupiesThirdColumn_AndAppearsOnlyOnce()
    {
        string field = await RankColumnAsync();
        using (var conn = _db.CreateOpenConnection())
        using (var command = conn.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO CustomFieldValues(PersonId,FieldDefinitionId,FieldValueText,UpdatedAtUtc)
                VALUES('TST-RANK-PERSON',$field,'ملازم','2026-10-19T08:00:00Z');
                """;
            command.Parameters.AddWithValue("$field", field);
            await command.ExecuteNonQueryAsync();
        }
        var result = await new MonthlyExcelExportService(_db).ExportAsync(new JsonObject
        {
            ["year"] = 2026, ["month"] = 10,
            ["siteId"] = "SITE-UNSPECIFIED", ["consolidatedOnly"] = true
        }, default);
        Assert.True(result.Success, result.UserMessage);
        string file = ((JsonObject)result.Data!)["filePath"]!.GetValue<string>();
        string[] headers = ExcelRow(file, 1, 2);
        Assert.Equal(new[] { "الرقم", "الرقم (ع)", "الرتبة", "الاسم", "الموقع" },
            headers.Take(5));
        Assert.Single(headers, h => h == "الرتبة");
        string[] person = ExcelRow(file, 1, 3);
        Assert.Equal("98451", person[1]);
        Assert.Equal("ملازم", person[2]);
        Assert.Equal("فرد الرتبة", person[3]);
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_dbPath);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
