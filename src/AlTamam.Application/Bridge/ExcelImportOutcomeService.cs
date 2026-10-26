using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Read-only, owner-scoped history of committed XLSX imports and their rejected rows.
/// Exports a real XLSX worksheet of saved evidence; never exports from transient UI data.
/// No caller-selected paths, SQL fragments, spreadsheet formulas, or existing-file overwrites.
/// </summary>
public sealed class ExcelImportOutcomeService
{
    private const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private readonly AlTamamDbContext _db;

    public ExcelImportOutcomeService(AlTamamDbContext db) => _db = db;

    private static string Read(JsonObject? obj, string name)
    {
        try { return obj?[name]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static JsonObject Batch(SqliteDataReader r) => new()
    {
        ["importBatchId"] = r.GetString(0),
        ["fileName"] = r.GetString(1),
        ["totalRows"] = r.GetInt32(2), ["acceptedRows"] = r.GetInt32(3),
        ["rejectedRows"] = r.GetInt32(4), ["status"] = r.GetString(5),
        ["createdAtUtc"] = r.GetString(6),
        ["committedAtUtc"] = r.IsDBNull(7) ? null : r.GetString(7)
    };

    private static JsonObject Issue(SqliteDataReader r) => new()
    {
        ["row"] = r.GetInt32(0), ["serviceNumberA"] = r.IsDBNull(1) ? "" : r.GetString(1),
        ["name"] = r.IsDBNull(2) ? "" : r.GetString(2),
        ["siteName"] = r.IsDBNull(3) ? "" : r.GetString(3),
        ["reason"] = r.GetString(4)
    };

    private static async Task<JsonObject?> OwnedBatchAsync(SqliteConnection connection,
        string batchId, string actor, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ImportBatchId,SourceFileName,TotalRows,AcceptedRows,RejectedRows,
                   Status,CreatedAtUtc,CommittedAtUtc
            FROM ImportBatches
            WHERE ImportBatchId=$batch AND CreatedByUserId=$actor AND Status='COMMITTED';
            """;
        cmd.Parameters.AddWithValue("$batch", batchId);
        cmd.Parameters.AddWithValue("$actor", actor);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Batch(reader) : null;
    }

    private static async Task<JsonArray> IssuesAsync(SqliteConnection connection,
        string batchId, CancellationToken ct)
    {
        var items = new JsonArray();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT RowNumber,RawServiceNumberA,RawPersonName,RawSiteName,IssueReasonArabic
            FROM ImportIssues WHERE ImportBatchId=$batch
            ORDER BY RowNumber,IssueId LIMIT 501;
            """;
        cmd.Parameters.AddWithValue("$batch", batchId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(Issue(reader));
        return items;
    }

    public async Task<BridgeResponse> QueryAsync(JsonObject? payload, string actor, CancellationToken ct)
    {
        string batchId = Read(payload, "importBatchId");
        if (batchId.Length > 128) return Invalid();
        using var connection = _db.CreateOpenConnection();
        if (batchId.Length > 0)
        {
            var batch = await OwnedBatchAsync(connection, batchId, actor, ct).ConfigureAwait(false);
            if (batch is null) return Missing();
            var issues = await IssuesAsync(connection, batchId, ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(new JsonObject { ["batch"] = batch, ["issues"] = issues });
        }

        var recent = new JsonArray();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ImportBatchId,SourceFileName,TotalRows,AcceptedRows,RejectedRows,
                   Status,CreatedAtUtc,CommittedAtUtc
            FROM ImportBatches
            WHERE CreatedByUserId=$actor AND Status='COMMITTED'
            ORDER BY CommittedAtUtc DESC,ImportBatchId DESC LIMIT 30;
            """;
        cmd.Parameters.AddWithValue("$actor", actor);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) recent.Add(Batch(reader));
        return BridgeHelpers.Ok(new JsonObject { ["batches"] = recent });
    }

    public async Task<BridgeResponse> ExportIssuesAsync(JsonObject? payload, string actor, CancellationToken ct)
    {
        string batchId = Read(payload, "importBatchId");
        if (batchId.Length is < 1 or > 128) return Invalid();

        JsonObject batch;
        JsonArray issues;
        using (var connection = _db.CreateOpenConnection())
        {
            batch = await OwnedBatchAsync(connection, batchId, actor, ct).ConfigureAwait(false)
                ?? new JsonObject();
            if (batch.Count == 0) return Missing();
            issues = await IssuesAsync(connection, batchId, ct).ConfigureAwait(false);
        }
        if (issues.Count == 0)
            return BridgeHelpers.Error(BridgeErrorCodes.MissingData, "هذه الدفعة ليس فيها صفوف مستبعدة لتصديرها.");
        if (issues.Count > 500)
            return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed, "عدد الصفوف المستبعدة خارج الحد المسموح.");

        string selected = await new OutputDirectorySettings(_db).GetAsync(ct).ConfigureAwait(false);
        if (!OutputDirectorySettings.IsSafeDirectory(selected,_db.DatabasePath,out string directory))
            return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed,
                "مجلد النسخ والتصدير غير متاح؛ غيّره من الإعدادات.");
        string filename = "أخطاء الاستيراد_" + DateTime.Now.ToString("yyyy-MM-dd",
            CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".xlsx";
        string destination = Path.Combine(directory, filename);
        string partial = destination + ".part";
        try
        {
            Directory.CreateDirectory(directory);
            ct.ThrowIfCancellationRequested();
            using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                Part(zip, "[Content_Types].xml", writer =>
                {
                    writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                    Default(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                    Default(writer, "xml", "application/xml");
                    writer.WriteStartElement("Override");
                    writer.WriteAttributeString("PartName", "/xl/workbook.xml");
                    writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                    writer.WriteEndElement();
                    writer.WriteStartElement("Override");
                    writer.WriteAttributeString("PartName", "/xl/worksheets/sheet1.xml");
                    writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                });
                Part(zip, "_rels/.rels", w => Relationships(w,
                    ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml")));
                Part(zip, "xl/_rels/workbook.xml.rels", w => Relationships(w,
                    ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml")));
                Part(zip, "xl/workbook.xml", w =>
                {
                    w.WriteStartElement("workbook", SheetNs);
                    w.WriteAttributeString("xmlns", "r", null, RelNs);
                    w.WriteStartElement("sheets", SheetNs);
                    w.WriteStartElement("sheet", SheetNs);
                    w.WriteAttributeString("name", "أخطاء الاستيراد");
                    w.WriteAttributeString("sheetId", "1");
                    w.WriteAttributeString("r", "id", RelNs, "rId1");
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteEndElement();
                });
                Part(zip, "xl/worksheets/sheet1.xml", w =>
                {
                    w.WriteStartElement("worksheet", SheetNs);
                    w.WriteStartElement("sheetViews", SheetNs);
                    w.WriteStartElement("sheetView", SheetNs);
                    w.WriteAttributeString("workbookViewId", "0");
                    w.WriteAttributeString("rightToLeft", "1");
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteStartElement("sheetData", SheetNs);
                    Row(w, 1, new[] { "تقرير صفوف استيراد Excel المستبعدة" });
                    Row(w, 2, new[] { "الدفعة", batchId,
                        "الملف", Read(batch, "fileName") });
                    Row(w, 3, new[] { "رقم الصف", "الرقم (ع)", "الاسم", "الموقع", "سبب الاستبعاد" });
                    int index = 3;
                    foreach (var node in issues)
                    {
                        ct.ThrowIfCancellationRequested();
                        var item = (JsonObject)node!;
                        Row(w, ++index, new[] {
                            item["row"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture),
                            Read(item, "serviceNumberA"), Read(item, "name"),
                            Read(item, "siteName"), Read(item, "reason") });
                    }
                    w.WriteEndElement();
                    w.WriteEndElement();
                });
            }
            File.Move(partial, destination, overwrite: false);
            return BridgeHelpers.Ok(new JsonObject {
                ["filePath"] = destination, ["fileName"] = filename,
                ["importBatchId"] = batchId, ["issueCount"] = issues.Count,
                ["format"] = "xlsx" });
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException)
        { return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed, "تعذر حفظ التقرير؛ تحقق من مساحة القرص."); }
        catch (UnauthorizedAccessException)
        { return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed, "لا توجد صلاحية لحفظ التقرير."); }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static BridgeResponse Missing() =>
        BridgeHelpers.Error(BridgeErrorCodes.MissingData, "لم تُعثر على دفعة استيراد مسجلة لهذا الحساب.");
    private static BridgeResponse Invalid() =>
        BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "معرف الدفعة غير صالح.");

    private static void Part(ZipArchive archive, string path, Action<XmlWriter> compose)
    {
        using var output = archive.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = XmlWriter.Create(output, new XmlWriterSettings
        { Encoding = new UTF8Encoding(false), CloseOutput = false });
        writer.WriteStartDocument();
        compose(writer);
        writer.WriteEndDocument();
    }

    private static void Default(XmlWriter writer, string extension, string kind)
    {
        writer.WriteStartElement("Default");
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", kind);
        writer.WriteEndElement();
    }

    private static void Relationships(XmlWriter writer,
        params (string id, string type, string target)[] links)
    {
        writer.WriteStartElement("Relationships",
            "http://schemas.openxmlformats.org/package/2006/relationships");
        foreach (var (id, type, target) in links)
        {
            writer.WriteStartElement("Relationship");
            writer.WriteAttributeString("Id", id);
            writer.WriteAttributeString("Type", type);
            writer.WriteAttributeString("Target", target);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    // Inline strings are escaped by XmlWriter and never interpreted as formulas.
    private static void Row(XmlWriter writer, int index, IEnumerable<string> values)
    {
        writer.WriteStartElement("row", SheetNs);
        writer.WriteAttributeString("r", index.ToString(CultureInfo.InvariantCulture));
        int column = 0;
        foreach (var value in values)
        {
            writer.WriteStartElement("c", SheetNs);
            writer.WriteAttributeString("r", Column(++column) + index.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is", SheetNs);
            writer.WriteStartElement("t", SheetNs);
            writer.WriteString(value);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static string Column(int index)
    {
        var s = new StringBuilder();
        while (index > 0)
        {
            index--; s.Insert(0, (char)('A' + index % 26)); index /= 26;
        }
        return s.ToString();
    }
}
