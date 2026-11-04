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
/// A column created in roster layout must reach every PERSON roster projection
/// and the real offline Excel exports. Never join by the reusable service number.
/// </summary>
public sealed class RosterCustomColumnPropagationTests : IAsyncLifetime, IDisposable
{
    private readonly string _database = SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _output = Path.Combine(Path.GetTempPath(),
        "altamam-global-roster-columns-" + Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new("TST-GLOBAL-COLUMN-OWNER",
        "global-columns-owner", UserRole.GeneralAdministration,
        "0123456789abcdef0123456789abcdef");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_output);
        _db = new AlTamamDbContext(_database, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,
                OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-GLOBAL-PERSON','فرد الاعمدة','فرد الاعمدة','8471','8471',
                '2026-10-01','2026-10-01T08:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-GLOBAL-PERSON','فرد الاعمدة','8471','2026-10-01',NULL,
                'TST-OWNER','2026-10-01T08:00:00Z','test');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-GLOBAL-ASSIGN','TST-GLOBAL-PERSON','SITE-UNSPECIFIED',
                '2026-10-01',NULL,NULL,'2026-10-01T08:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
            VALUES('TST-GLOBAL-PERSON','2026-10-19','STS-PRESENT','موجود','م','#22c55e',
                'TST-GLOBAL-ASSIGN','SITE-UNSPECIFIED','غير محدد','manual',
                'TST-OWNER','2026-10-19T08:00:00Z','TST-GLOBAL-DAY');
            INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
            VALUES('PreferredBackupDirectory',$folder,'2026-10-19T08:00:00Z')
            ON CONFLICT(SettingKey) DO UPDATE SET SettingValue=excluded.SettingValue;
            """;
        cmd.Parameters.AddWithValue("$folder", _output);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<JsonObject> AddWithValueAsync(string label, string value)
    {
        var svc = new RosterColumnStructureService(_db);
        var created = await svc.CreateAsync(new JsonObject
        {
            ["caption"] = label, ["fieldType"] = 1,
            ["operationId"] = Guid.NewGuid().ToString("N")
        }, Actor, default);
        Assert.True(created.Success, created.UserMessage);
        var column = Assert.IsType<JsonObject>(created.Data);
        using var connection = _db.CreateOpenConnection();
        using var query = connection.CreateCommand();
        query.CommandText = """
            INSERT INTO CustomFieldValues(PersonId,FieldDefinitionId,FieldValueText,UpdatedAtUtc)
            VALUES('TST-GLOBAL-PERSON',$column,$value,'2026-10-19T08:00:00Z');
            """;
        query.Parameters.AddWithValue("$column", column["columnId"]!.GetValue<string>());
        query.Parameters.AddWithValue("$value", value);
        await query.ExecuteNonQueryAsync();
        return column;
    }

    private static JsonObject Find(JsonObject data) =>
        Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(data["items"])));

    private static void VerifyVisible(JsonObject data, string columnId, string heading, string value)
    {
        var cols = Assert.IsType<JsonArray>(data["customColumns"]);
        Assert.Contains(cols, c => c!["columnId"]!.GetValue<string>() == columnId &&
            c["caption"]!.GetValue<string>() == heading);
        var person = Find(data);
        Assert.Equal(value, person["customFields"]![columnId]!.GetValue<string>());
    }

    private static string[] WorksheetText(string file, int sheet, int row)
    {
        using var zip = ZipFile.OpenRead(file);
        using var stream = zip.GetEntry($"xl/worksheets/sheet{sheet}.xml")!.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var xmlRow = document.Descendants(ns + "row").ElementAt(row);
        return xmlRow.Elements(ns + "c").Select(c =>
            c.Element(ns + "is")?.Element(ns + "t")?.Value ??
            c.Element(ns + "v")?.Value ?? "").ToArray();
    }

    [Fact]
    public async Task NewPersonColumn_AppearsInPersonDailyMonthlyAndReportAndBothExcels()
    {
        var col = await AddWithValueAsync("المؤهل العلمي", "بكالوريوس");
        string id = col["columnId"]!.GetValue<string>();
        var roster = await new PersonRosterService(_db).QueryPersonsAsync(new JsonObject
        {
            ["term"] = "8471", ["page"] = 1, ["pageSize"] = 20
        }, default);
        Assert.True(roster.Success, roster.UserMessage);
        VerifyVisible(Assert.IsType<JsonObject>(roster.Data), id, "المؤهل العلمي", "بكالوريوس");

        var reads = new DailyMonthlyReadService(_db);
        var daily = await reads.DailyRosterAsync(new JsonObject
        {
            ["businessDate"] = "2026-10-19", ["term"] = "8471",
            ["page"] = 1, ["pageSize"] = 20
        }, default);
        Assert.True(daily.Success, daily.UserMessage);
        VerifyVisible(Assert.IsType<JsonObject>(daily.Data), id, "المؤهل العلمي", "بكالوريوس");

        var monthly = await reads.MonthlyGridAsync(new JsonObject
        {
            ["year"] = 2026, ["month"] = 10, ["term"] = "8471",
            ["archiveMode"] = true, ["page"] = 1, ["pageSize"] = 20
        }, default);
        Assert.True(monthly.Success, monthly.UserMessage);
        VerifyVisible(Assert.IsType<JsonObject>(monthly.Data), id, "المؤهل العلمي", "بكالوريوس");

        var report = await new PersonnelReportService(_db).ReportAsync(new JsonObject
        {
            ["personId"] = "TST-GLOBAL-PERSON"
        }, default);
        Assert.True(report.Success, report.UserMessage);
        var person = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(report.Data)["person"]);
        Assert.Equal("بكالوريوس", person["customFields"]![id]!.GetValue<string>());

        var monthlyExport = await new MonthlyExcelExportService(_db).ExportAsync(new JsonObject
        {
            ["year"] = 2026, ["month"] = 10, ["siteId"] = "SITE-UNSPECIFIED",
            ["consolidatedOnly"] = true
        }, default);
        Assert.True(monthlyExport.Success, monthlyExport.UserMessage);
        var monthlyFile = ((JsonObject)monthlyExport.Data!)["filePath"]!.GetValue<string>();
        Assert.Contains("المؤهل العلمي", WorksheetText(monthlyFile, 1, 2));
        Assert.Contains("بكالوريوس", WorksheetText(monthlyFile, 1, 3));

        var reportExcel = await new PersonnelExcelExportService(_db).ExportAsync(new JsonObject
        {
            ["mode"] = "report", ["exportId"] = Guid.NewGuid().ToString("N"),
            ["personId"] = "TST-GLOBAL-PERSON"
        }, Actor, default);
        Assert.True(reportExcel.Success, reportExcel.UserMessage);
        var reportFile = ((JsonObject)reportExcel.Data!)["filePath"]!.GetValue<string>();
        Assert.Contains("المؤهل العلمي", WorksheetText(reportFile, 1, 0).Last());
        Assert.Equal("بكالوريوس", WorksheetText(reportFile, 1, 1).Last());
    }

    [Fact]
    public async Task HiddenColumn_IsAbsentFromEveryLiveRosterAndPreservesItsStoredValue()
    {
        var col = await AddWithValueAsync("عمود سيخفى", "قيمة محفوظة");
        var svc = new RosterColumnStructureService(_db);
        string id = col["columnId"]!.GetValue<string>();
        var hidden = await svc.UpdateAsync(new JsonObject
        {
            ["columnId"] = id, ["expectedSnapshot"] = col["snapshot"]!.GetValue<string>(),
            ["caption"] = "عمود سيخفى", ["fieldType"] = 1,
            ["displayOrder"] = col["displayOrder"]!.GetValue<int>(),
            ["visible"] = false, ["operationId"] = Guid.NewGuid().ToString("N")
        }, Actor, default);
        Assert.True(hidden.Success, hidden.UserMessage);
        var roster = await new PersonRosterService(_db).QueryPersonsAsync(new JsonObject
        {
            ["term"] = "8471"
        }, default);
        Assert.True(roster.Success);
        var data = Assert.IsType<JsonObject>(roster.Data);
        Assert.DoesNotContain(Assert.IsType<JsonArray>(data["customColumns"]),
            x => x!["columnId"]!.GetValue<string>() == id);
        Assert.Null(Find(data)["customFields"]![id]);
        using var conn = _db.CreateOpenConnection();
        using var query = conn.CreateCommand();
        query.CommandText = "SELECT FieldValueText FROM CustomFieldValues WHERE FieldDefinitionId=$id;";
        query.Parameters.AddWithValue("$id", id);
        Assert.Equal("قيمة محفوظة", await query.ExecuteScalarAsync());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_database);
        try { if (Directory.Exists(_output)) Directory.Delete(_output, recursive:true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
