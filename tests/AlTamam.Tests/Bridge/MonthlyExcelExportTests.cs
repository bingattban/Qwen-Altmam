using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Exports are genuine offline .xlsx ZIP packages, safe against Excel formulas and file clobbering.</summary>
public sealed class MonthlyExcelExportTests : IAsyncLifetime, IDisposable
{
    private readonly string _file = SyntheticFixtures.CreateTempDatabasePath();
    private readonly List<string> _exports = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(),"altamam-monthly-output-"+Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_file, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        Directory.CreateDirectory(_output);
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Persons (PersonId, CurrentVisibleName, NormalizedName,
                CurrentServiceNumberA, NormalizedServiceNumberA, FirstRecordedLocalDate,
                FirstRecordedAtUtc, OrganizationalStatus, ActiveFlag, RowVersion)
            VALUES ('TST-EX-0','=HYPERLINK("https://example.invalid")',
                'اسم اختبار','EX000','EX000','2026-10-01','2026-10-01T07:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-EX-0','=HYPERLINK("https://example.invalid")','EX000',
                '2026-10-01',NULL,'TST-U','2026-10-01T07:00:00Z','سجل تجريبي');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-EX-A0','TST-EX-0','SITE-UNSPECIFIED',
                '2026-10-01',NULL,NULL,'2026-10-01T07:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
            VALUES('TST-EX-0','2026-10-19','STS-ABSENT','غياب','غ','#EF4444',
                'TST-EX-A0','SITE-UNSPECIFIED','غير محدد','manual','TST-U',
                '2026-10-19T07:00:00Z','TST-EX-ATT');
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText="UPDATE AppSettings SET SettingValue=$folder WHERE SettingKey='PreferredBackupDirectory';";
        command.Parameters.AddWithValue("$folder",_output);
        await command.ExecuteNonQueryAsync();
        // 101 synthetic entries exercise the real 100-row SQLCipher paging boundary.
        for (int i = 1; i <= 101; i++)
        {
            using var c = connection.CreateCommand();
            c.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($id,$name,$name,$number,$number,'2026-10-01','2026-10-01T07:00:00Z',1,1,1);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES($id,$name,$number,'2026-10-01',NULL,'TST-U',
                    '2026-10-01T07:00:00Z','سجل تجريبي');
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES($assignment,$id,'SITE-UNSPECIFIED','2026-10-01',NULL,NULL,'2026-10-01T07:00:00Z');
                """;
            c.Parameters.AddWithValue("$id", $"TST-EX-{i}");
            c.Parameters.AddWithValue("$name", $"فرد {i}");
            c.Parameters.AddWithValue("$number", $"EX{i:000}");
            c.Parameters.AddWithValue("$assignment", $"TST-EX-A{i}");
            await c.ExecuteNonQueryAsync();
        }
    }

    private static JsonObject Period(string site = "") => new()
    {
        ["year"] = 2026, ["month"] = 10, ["siteId"] = site
    };

    [Fact]
    public async Task ExcelPreview_HasRealTotalsAndRejectsInvalidOrEmptyPeriods()
    {
        var excel = new MonthlyExcelExportService(_db);
        var preview = await excel.PreviewAsync(Period("SITE-UNSPECIFIED"), CancellationToken.None);
        Assert.True(preview.Success, preview.UserMessage);
        Assert.Equal(102L, Assert.IsType<JsonObject>(preview.Data)["personCount"]!.GetValue<long>());
        var invalid = await excel.PreviewAsync(new JsonObject { ["year"] = 0, ["month"] = 13 },
            CancellationToken.None);
        Assert.False(invalid.Success);
        var empty = await excel.ExportAsync(Period("SITE-NOT-FOUND"), CancellationToken.None);
        Assert.False(empty.Success);
        var months = await excel.PeriodsAsync(CancellationToken.None);
        Assert.Contains(Assert.IsType<JsonArray>(months.Data), x => x!.GetValue<string>() == "2026-10");
    }

    [Fact]
    public async Task ExcelArchive_IsReadableNativeXlsx_AllRowsEscapedAndHistoricalDaysRetained()
    {
        var excel = new MonthlyExcelExportService(_db);
        var result = await excel.ExportAsync(Period(), CancellationToken.None);
        Assert.True(result.Success, result.UserMessage);
        var metadata = Assert.IsType<JsonObject>(result.Data);
        var name = metadata["filePath"]!.GetValue<string>();
        _exports.Add(name);
        Assert.Equal("xlsx", metadata["format"]!.GetValue<string>());
        Assert.EndsWith(".xlsx", name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(102L, metadata["personCount"]!.GetValue<long>());
        using (var archive = ZipFile.OpenRead(name))
        {
            foreach (var path in new[] {
                "[Content_Types].xml", "_rels/.rels",
                "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml"
            }) Assert.NotNull(archive.GetEntry(path));
            using var data = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var xml = XDocument.Load(data);
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows = xml.Descendants(main + "row").ToArray();
            Assert.Equal(105, rows.Length); // 102 persons and three informational/header rows
            var values = xml.Descendants(main + "t").Select(t => t.Value).ToArray();
            Assert.Contains("غياب", values);
            Assert.Contains("EX000", values);
            Assert.Contains("EX101", values);
            Assert.Contains(@"=HYPERLINK(""https://example.invalid"")", values);
            Assert.Empty(xml.Descendants(main + "f")); // formula injection prohibited
            Assert.Equal("1", xml.Descendants(main + "sheetView").Single()
                .Attribute("rightToLeft")?.Value);
        }
        var again = await excel.ExportAsync(Period(), CancellationToken.None);
        Assert.True(again.Success, again.UserMessage);
        string second = ((JsonObject)again.Data!)["filePath"]!.GetValue<string>();
        _exports.Add(second);
        Assert.NotEqual(name, second);
        Assert.True(File.Exists(name));
        Assert.True(File.Exists(second));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db?.Dispose();
        foreach (var path in _exports)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        SyntheticFixtures.DeleteTempDatabase(_file);
        try{Directory.Delete(_output,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
