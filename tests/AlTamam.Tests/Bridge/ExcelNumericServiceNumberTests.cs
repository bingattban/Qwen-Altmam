using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Numeric Excel cells for the visible service number are accepted as numbers.
/// The SQLCipher identifier remains a canonical string: no floating-point
/// precision loss, no formula evaluation, and no implicit leading-zero invention.
/// </summary>
public sealed class ExcelNumericServiceNumberTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private const string Actor = "TST-IMPORT-NUMERIC";

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    private static JsonObject Workbook(params (string person, string raw, string kind, string formula)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            static void Part(ZipArchive z, string name, string content)
            {
                using var entry = z.CreateEntry(name, CompressionLevel.Optimal).Open();
                byte[] data = Encoding.UTF8.GetBytes(content);
                entry.Write(data);
            }
            Part(zip, "[Content_Types].xml", "<Types/>");
            Part(zip, "xl/workbook.xml", "<workbook/>");
            var xml = new StringBuilder("""
                <?xml version="1.0" encoding="utf-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>الاسم</t></is></c>
                <c r="B1" t="inlineStr"><is><t>الرقم (ع)</t></is></c></row>
                """);
            for (int i = 0; i < entries.Length; i++)
            {
                int row = i + 2;
                string name = System.Security.SecurityElement.Escape(entries[i].person)!;
                string value = System.Security.SecurityElement.Escape(entries[i].raw)!;
                string type = entries[i].kind.Length == 0 ? "" : $" t=\"{entries[i].kind}\"";
                string body = entries[i].kind == "inlineStr"
                    ? $"<is><t>{value}</t></is>"
                    : (entries[i].formula.Length > 0
                        ? $"<f>{System.Security.SecurityElement.Escape(entries[i].formula)}</f>"
                        : "") + $"<v>{value}</v>";
                xml.Append($"""<row r="{row}"><c r="A{row}" t="inlineStr"><is><t>{name}</t></is></c>""");
                xml.Append($"""<c r="B{row}"{type}>{body}</c></row>""");
            }
            xml.Append("</sheetData></worksheet>");
            Part(zip, "xl/worksheets/sheet1.xml", xml.ToString());
        }
        return new JsonObject {
            ["fileName"] = "ارقام_عادية.xlsx", ["base64"] = Convert.ToBase64String(buffer.ToArray())
        };
    }

    [Fact]
    public async Task PlainNumericCells_AreAcceptedAndCommittedWithoutChangingIdentifiers()
    {
        var workbook = Workbook(
            ("عبدالله سالم بن عمر", "12345", "n", ""),
            ("محمد أحمد باصالح", "1.23E+5", "", ""),
            ("سعيد صالح محمد", "00007", "inlineStr", ""),
            ("علي حسن عبدالله", "123456789012345", "n", ""));
        var service = new ExcelPersonnelImportService(_db);
        var selected = await service.SelectAsync(workbook, Actor, CancellationToken.None);
        Assert.True(selected.Success, selected.UserMessage);
        var token = ((JsonObject)selected.Data!)["workbookToken"]!.GetValue<string>();
        var analyzed = await service.AnalyzeAsync(new JsonObject { ["workbookToken"] = token },
            Actor, CancellationToken.None);
        Assert.True(analyzed.Success, analyzed.UserMessage);
        Assert.Equal(4, ((JsonObject)analyzed.Data!)["newCount"]!.GetValue<int>());
        var preview = await service.PreviewAsync(new JsonObject { ["workbookToken"] = token },
            Actor, CancellationToken.None);
        Assert.True(preview.Success, preview.UserMessage);
        var key = ((JsonObject)preview.Data!)["previewToken"]!.GetValue<string>();
        var committed = await service.CommitAsync(new JsonObject { ["previewToken"] = key },
            Actor, "numeric-import-test", CancellationToken.None);
        Assert.True(committed.Success, committed.UserMessage);
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CurrentServiceNumberA FROM Persons ORDER BY CurrentServiceNumberA;";
        using var reader = await command.ExecuteReaderAsync();
        var numbers = new List<string>();
        while (await reader.ReadAsync()) numbers.Add(reader.GetString(0));
        Assert.Equal(new[] { "00007", "123000", "12345", "123456789012345" }, numbers);
    }

    [Fact]
    public async Task NumericAndTextCellsWithSameNumber_AreDetectedAsDuplicates()
    {
        var service = new ExcelPersonnelImportService(_db);
        var selected = await service.SelectAsync(Workbook(
            ("سالم علي سعيد", "45678", "n", ""),
            ("محمد صالح عبدالله", "45678", "inlineStr", "")), Actor, CancellationToken.None);
        Assert.True(selected.Success, selected.UserMessage);
        var ticket = ((JsonObject)selected.Data!)["workbookToken"]!.GetValue<string>();
        var result = await service.AnalyzeAsync(new JsonObject { ["workbookToken"] = ticket },
            Actor, CancellationToken.None);
        Assert.True(result.Success, result.UserMessage);
        var summary = (JsonObject)result.Data!;
        Assert.Equal(1, summary["newCount"]!.GetValue<int>());
        Assert.Equal(1, summary["issueCount"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("12.5", "n", "")]
    [InlineData("-3", "n", "")]
    [InlineData("1234567890123456", "n", "")]
    [InlineData("1E+16", "", "")]
    [InlineData("2", "n", "1+1")]
    [InlineData("1", "b", "")]
    public async Task UnsafeExcelNumberTypes_AreRejected(string raw, string kind, string formula)
    {
        var service = new ExcelPersonnelImportService(_db);
        var selected = await service.SelectAsync(Workbook(
            ("محمد خالد سالم", raw, kind, formula)), Actor, CancellationToken.None);
        Assert.False(selected.Success);
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Persons;";
        Assert.Equal(0L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
