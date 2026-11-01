using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class ExcelImportOutcomeIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _database = SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _output = Path.Combine(Path.GetTempPath(),"altamam-import-output-"+Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db = null!;
    private const string Actor = "TST-IMPORT-HISTORY-OWNER";
    private readonly List<string> _reports = new();

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_database, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        Directory.CreateDirectory(_output);
        using var connection=_db.CreateOpenConnection();
        using var cmd=connection.CreateCommand();
        cmd.CommandText="UPDATE AppSettings SET SettingValue=$folder WHERE SettingKey='PreferredBackupDirectory';";
        cmd.Parameters.AddWithValue("$folder",_output);
        await cmd.ExecuteNonQueryAsync();
    }

    private static JsonObject Workbook(params (string name, string number)[] rows)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string value)
            {
                using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                stream.Write(bytes);
            }
            Add("[Content_Types].xml", "<Types/>");
            Add("xl/workbook.xml", "<workbook/>");
            var xml = new StringBuilder("""
                <?xml version="1.0" encoding="utf-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>الاسم</t></is></c>
                <c r="B1" t="inlineStr"><is><t>الرقم (ع)</t></is></c></row>
                """);
            for (int i = 0; i < rows.Length; i++)
            {
                int row = i + 2;
                xml.Append($"""<row r="{row}"><c r="A{row}" t="inlineStr"><is><t>{System.Security.SecurityElement.Escape(rows[i].name)}</t></is></c>""");
                xml.Append($"""<c r="B{row}" t="inlineStr"><is><t>{System.Security.SecurityElement.Escape(rows[i].number)}</t></is></c></row>""");
            }
            xml.Append("</sheetData></worksheet>");
            Add("xl/worksheets/sheet1.xml", xml.ToString());
        }
        return new JsonObject { ["fileName"] = "دفعة_الاستيراد.xlsx",
            ["base64"] = Convert.ToBase64String(buffer.ToArray()) };
    }

    private async Task<JsonObject> Commit(params (string name, string number)[] rows)
    {
        var service = new ExcelPersonnelImportService(_db);
        var select = await service.SelectAsync(Workbook(rows), Actor, CancellationToken.None);
        Assert.True(select.Success, select.UserMessage);
        string ticket = ((JsonObject)select.Data!)["workbookToken"]!.GetValue<string>();
        var preview = await service.PreviewAsync(new JsonObject { ["workbookToken"] = ticket },
            Actor, CancellationToken.None);
        Assert.True(preview.Success, preview.UserMessage);
        string token = ((JsonObject)preview.Data!)["previewToken"]!.GetValue<string>();
        var commit = await service.CommitAsync(new JsonObject { ["previewToken"] = token },
            Actor, "import-owner", CancellationToken.None);
        Assert.True(commit.Success, commit.UserMessage);
        return (JsonObject)commit.Data!;
    }

    private string Scalar(string sql)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    [Fact]
    public async Task CommitPersistsBatchAndIssues_OnlyOwnerCanReadOrExport()
    {
        await Commit(("خالد سعيد الحضرمي", "00001"));
        var saved = await Commit(("=HYPERLINK(\"https://example.invalid\")", "00001"),
            ("محمد صالح عبدالله", "00002"));
        Assert.Equal(1, saved["createdCount"]!.GetValue<int>());
        Assert.Equal(1, saved["rejectedCount"]!.GetValue<int>());
        string batchId = saved["importBatchId"]!.GetValue<string>();
        Assert.StartsWith("IMP-", batchId);

        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM ImportBatches WHERE Status='COMMITTED'"));
        Assert.Equal("1", Scalar($"SELECT RejectedRows FROM ImportBatches WHERE ImportBatchId='{batchId}'"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM ImportIssues"));
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM Persons"));
        Assert.Equal("خالد سعيد الحضرمي",
            Scalar("SELECT CurrentVisibleName FROM Persons WHERE CurrentServiceNumberA='00001'"));

        var outcome = new ExcelImportOutcomeService(_db);
        var history = await outcome.QueryAsync(new JsonObject(), Actor, CancellationToken.None);
        Assert.True(history.Success, history.UserMessage);
        var batches = (JsonArray)((JsonObject)history.Data!)["batches"]!;
        Assert.Equal(2, batches.Count);
        Assert.Equal(batchId, batches[0]!["importBatchId"]!.GetValue<string>());
        var details = await outcome.QueryAsync(new JsonObject { ["importBatchId"] = batchId },
            Actor, CancellationToken.None);
        Assert.True(details.Success, details.UserMessage);
        var issue = Assert.Single((JsonArray)((JsonObject)details.Data!)["issues"]!);
        Assert.Equal("00001", issue!["serviceNumberA"]!.GetValue<string>());
        Assert.Equal("=HYPERLINK(\"https://example.invalid\")",
            issue["name"]!.GetValue<string>());

        var foreign = await outcome.QueryAsync(new JsonObject { ["importBatchId"] = batchId },
            "TST-FOREIGN-USER", CancellationToken.None);
        Assert.False(foreign.Success);
        var foreignReport = await outcome.ExportIssuesAsync(
            new JsonObject { ["importBatchId"] = batchId },
            "TST-FOREIGN-USER", CancellationToken.None);
        Assert.False(foreignReport.Success);

        var export = await outcome.ExportIssuesAsync(
            new JsonObject { ["importBatchId"] = batchId }, Actor, CancellationToken.None);
        Assert.True(export.Success, export.UserMessage);
        var data = (JsonObject)export.Data!;
        Assert.Equal("xlsx", data["format"]!.GetValue<string>());
        Assert.Equal(1, data["issueCount"]!.GetValue<int>());
        string filename = data["filePath"]!.GetValue<string>();
        _reports.Add(filename);
        Assert.False(File.Exists(filename+".part"),"Verified error workbook must not leave .part file.");
        using var archive = ZipFile.OpenRead(filename);
        foreach (string part in new[] {
            "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
            "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml"
        }) Assert.NotNull(archive.GetEntry(part));
        using var stream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XDocument xml = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Empty(xml.Descendants(ns + "f"));
        Assert.Contains(xml.Descendants(ns + "t"),
            t => t.Value == "=HYPERLINK(\"https://example.invalid\")");
        Assert.Contains(xml.Descendants(ns + "t"), t => t.Value == "00001");
        Assert.Equal("1", xml.Descendants(ns + "sheetView").Single().Attribute("rightToLeft")?.Value);
    }

    [Fact]
    public async Task NoIssuesCannotExportAndRepeatedExportsNeverOverwriteEachOther()
    {
        var onlyNew = await Commit(("عمر محمد سالم", "00555"));
        var outcome = new ExcelImportOutcomeService(_db);
        var empty = await outcome.ExportIssuesAsync(new JsonObject {
            ["importBatchId"] = onlyNew["importBatchId"]!.GetValue<string>()
        }, Actor, CancellationToken.None);
        Assert.False(empty.Success);
        await Commit(("صالح سعيد أحمد", "00666"));
        var duplicate = await Commit(("محمد عبد الله", "00666"), ("سعيد أحمد بن سعد", "00777"));
        string batchId = duplicate["importBatchId"]!.GetValue<string>();
        var first = await outcome.ExportIssuesAsync(new JsonObject {
            ["importBatchId"] = batchId
        }, Actor, CancellationToken.None);
        var second = await outcome.ExportIssuesAsync(new JsonObject {
            ["importBatchId"] = batchId
        }, Actor, CancellationToken.None);
        Assert.True(first.Success, first.UserMessage);
        Assert.True(second.Success, second.UserMessage);
        string a = ((JsonObject)first.Data!)["filePath"]!.GetValue<string>();
        string b = ((JsonObject)second.Data!)["filePath"]!.GetValue<string>();
        _reports.Add(a); _reports.Add(b);
        Assert.NotEqual(a, b);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db?.Dispose();
        foreach (var file in _reports)
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        SyntheticFixtures.DeleteTempDatabase(_database);
        try{Directory.Delete(_output,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
