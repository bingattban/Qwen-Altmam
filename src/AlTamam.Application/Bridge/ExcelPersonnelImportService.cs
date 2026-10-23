using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Domain.Policies;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Safe additive import of new personnel from XLSX. No existing names, assignments,
/// attendance, or historical records can be overwritten by a workbook.
/// Uploaded file bytes never become a trusted file path or SQL fragment.
/// The 15-minute user-bound preview is consumed atomically with the inserts and audit.
/// </summary>
public sealed class ExcelPersonnelImportService
{
    private const int MaxWorkbookBytes = 1_048_576;
    private const int MaxRecords = 500;
    private const string WorkbookOperation = "PERSONNEL-IMPORT-WORKBOOK";
    private const string PlanOperation = "PERSONNEL-IMPORT-PLAN";
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public ExcelPersonnelImportService(AlTamamDbContext db, TimeProvider? time = null)
    { _db = db; _time = time ?? TimeProvider.System; }

    private static string Read(JsonObject? payload, string key)
    {
        try { return payload?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private sealed record PersonInput(int Row, string Name, string Number, string NormalizedName, string NormalizedNumber);

    public async Task<BridgeResponse> SelectAsync(JsonObject? payload, string actor, CancellationToken ct)
    {
        string base64 = Read(payload, "base64");
        string fileName = Read(payload, "fileName");
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            fileName.Length > 180 || base64.Length is < 10 or > 1_450_000)
            return Invalid("اختر ملف Excel بصيغة xlsx وبحجم لا يتجاوز 1 ميجابايت.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return Invalid("محتوى ملف Excel غير صالح."); }
        try
        {
            if (bytes.Length > MaxWorkbookBytes) return Invalid("ملف Excel كبير جداً (الحد 1 ميجابايت).");
            var rows = ParseWorkbook(bytes);
            if (rows.Count == 0) return Invalid("الكشف لا يحتوي على صفوف أفراد صالحة للمعالجة.");
            var data = new JsonArray();
            foreach (var row in rows)
                data.Add(new JsonObject
                {
                    ["row"] = row.Row, ["name"] = row.Name, ["number"] = row.Number,
                    ["normalizedName"] = row.NormalizedName, ["normalizedNumber"] = row.NormalizedNumber
                });
            string token = BridgePreviewTokens.Issue(_db, _time, WorkbookOperation, actor,
                new JsonObject { ["rows"] = data }, 0, ct);
            return BridgeHelpers.Ok(new JsonObject { ["workbookToken"] = token,
                ["rowCount"] = rows.Count, ["fileName"] = fileName,
                ["maxRows"] = MaxRecords });
        }
        catch (InvalidDataException e) { return Invalid(e.Message); }
        catch (XmlException) { return Invalid("تعذر قراءة بنية ملف Excel."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private static JsonObject? ParseToken(JsonObject? payload, string property) =>
        payload is not null && Read(payload, property).Length == 64 ? payload : null;

    private async Task<JsonObject?> ReadStagedAsync(string token, string operation,
        string actor, SqliteConnection connection, SqliteTransaction? tx, CancellationToken ct)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT PayloadCanonicalJson FROM PreviewTokens
            WHERE Token=$token AND Operation=$op AND ActorUserId=$actor
              AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
            """;
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$op", operation);
        cmd.Parameters.AddWithValue("$actor", actor);
        cmd.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (value is null) return null;
        try { return JsonNode.Parse(value) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static List<PersonInput> Rows(JsonObject staged)
    {
        var items = staged["rows"] as JsonArray ?? throw new InvalidDataException("غياب بيانات المعاينة.");
        if (items.Count is < 1 or > MaxRecords) throw new InvalidDataException("عدد صفوف الاستيراد غير آمن.");
        return items.Select(n =>
        {
            var obj = n as JsonObject ?? throw new InvalidDataException("صف غير صالح.");
            return new PersonInput(obj["row"]!.GetValue<int>(),
                obj["name"]!.GetValue<string>(), obj["number"]!.GetValue<string>(),
                obj["normalizedName"]!.GetValue<string>(), obj["normalizedNumber"]!.GetValue<string>());
        }).ToList();
    }

    private static async Task<(JsonArray accepted, JsonArray issues)> InspectAsync(
        SqliteConnection connection, SqliteTransaction? tx, List<PersonInput> rows, CancellationToken ct)
    {
        var accepted = new JsonArray();
        var issues = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var exists = connection.CreateCommand();
        exists.Transaction = tx;
        exists.CommandText = """
            SELECT CurrentVisibleName FROM Persons
            WHERE NormalizedServiceNumberA=$number AND ActiveFlag=1 LIMIT 1;
            """;
        var param = exists.Parameters.Add("$number", SqliteType.Text);
        foreach (var item in rows)
        {
            ct.ThrowIfCancellationRequested();
            string? reason = null;
            if (item.Name.Length is < 3 or > 180 || item.Number.Length is < 1 or > 70 ||
                item.Name.Any(char.IsControl) || item.Number.Any(char.IsControl) ||
                item.NormalizedName != ArabicTextNormalizer.NormalizeForSearch(item.Name) ||
                item.NormalizedNumber != ArabicTextNormalizer.NormalizeForSearch(item.Number) ||
                item.NormalizedName.Length == 0 || item.NormalizedNumber.Length == 0)
                reason = "بيانات اسم أو رقم غير صالحة.";
            else if (!seen.Add(item.NormalizedNumber))
                reason = "«الرقم (ع)» مكرر داخل الملف.";
            else
            {
                param.Value = item.NormalizedNumber;
                if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is string)
                    reason = "«الرقم (ع)» موجود مسبقاً في القوة؛ لن يتم استبداله.";
            }
            if (reason is not null)
                issues.Add(new JsonObject { ["row"] = item.Row, ["name"] = item.Name,
                    ["serviceNumberA"] = item.Number, ["reason"] = reason });
            else
                accepted.Add(new JsonObject { ["row"] = item.Row, ["name"] = item.Name,
                    ["serviceNumberA"] = item.Number, ["normalizedName"] = item.NormalizedName,
                    ["normalizedNumber"] = item.NormalizedNumber });
        }
        return (accepted, issues);
    }

    public async Task<BridgeResponse> AnalyzeAsync(JsonObject? payload, string actor, CancellationToken ct)
    {
        string ticket = Read(payload, "workbookToken");
        using var connection = _db.CreateOpenConnection();
        var staged = await ReadStagedAsync(ticket, WorkbookOperation, actor, connection, null, ct).ConfigureAwait(false);
        if (staged is null) return Stale();
        var (accepted, issues) = await InspectAsync(connection, null, Rows(staged), ct).ConfigureAwait(false);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["totalRows"] = accepted.Count + issues.Count, ["newCount"] = accepted.Count,
            ["issueCount"] = issues.Count, ["issues"] = issues,
            ["acceptedPreview"] = PreviewNames(accepted)
        });
    }

    public async Task<BridgeResponse> PreviewAsync(JsonObject? payload, string actor, CancellationToken ct)
    {
        string ticket = Read(payload, "workbookToken");
        using var connection = _db.CreateOpenConnection();
        var staged = await ReadStagedAsync(ticket, WorkbookOperation, actor, connection, null, ct).ConfigureAwait(false);
        if (staged is null) return Stale();
        var (accepted, issues) = await InspectAsync(connection, null, Rows(staged), ct).ConfigureAwait(false);
        if (accepted.Count == 0)
            return BridgeHelpers.Error(BridgeErrorCodes.InvalidWorkbook, "لا توجد أسماء جديدة صالحة للاستيراد.");
        // Only approved, conflict-free new rows are carried forward to the signed, one-use plan.
        string token = BridgePreviewTokens.Issue(_db, _time, PlanOperation, actor,
            new JsonObject { ["rows"] = new JsonArray(accepted.Select(node =>
            {
                var p = (JsonObject)node!;
                return (JsonNode?)new JsonObject {
                    ["row"] = p["row"]!.GetValue<int>(), ["name"] = p["name"]!.GetValue<string>(),
                    ["number"] = p["serviceNumberA"]!.GetValue<string>(),
                    ["normalizedName"] = p["normalizedName"]!.GetValue<string>(),
                    ["normalizedNumber"] = p["normalizedNumber"]!.GetValue<string>()
                };
            }).ToArray()) }, accepted.Count, ct);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["previewToken"] = token, ["newCount"] = accepted.Count,
            ["issueCount"] = issues.Count, ["issues"] = issues,
            ["acceptedPreview"] = PreviewNames(accepted),
            ["requiresExplicitConfirmation"] = true
        });
    }

    public async Task<BridgeResponse> CommitAsync(JsonObject? payload,
        string actor, string username, CancellationToken ct)
    {
        string token = Read(payload, "previewToken");
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            var plan = await ReadStagedAsync(token, PlanOperation, actor, connection, tx, ct).ConfigureAwait(false);
            if (plan is null) return Stale();
            var rows = Rows(plan);
            var (accepted, issues) = await InspectAsync(connection, tx, rows, ct).ConfigureAwait(false);
            if (issues.Count > 0 || accepted.Count != rows.Count)
                return Stale();
            long? consumed = await BridgePreviewTokens.ConsumeAsync(_db, _time, connection,
                tx, token, PlanOperation, actor, rows.Count, ct).ConfigureAwait(false);
            if (consumed != rows.Count) return Stale();

            string now = _time.GetUtcNow().ToString("O");
            string today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var createdIds = new List<string>(rows.Count);
            foreach (var item in rows)
            {
                ct.ThrowIfCancellationRequested();
                string personId = "PRN-" + Guid.NewGuid().ToString("N");
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                        CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                        FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                    VALUES($id,$name,$normName,$number,$normNum,$today,$now,1,1,1);
                    INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                        ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                    VALUES($id,$name,$number,$today,NULL,$actor,$now,'استيراد Excel معتمد');
                    INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                        ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                    VALUES($assignment,$id,'SITE-UNSPECIFIED',$today,NULL,NULL,$now);
                    """;
                insert.Parameters.AddWithValue("$id", personId);
                insert.Parameters.AddWithValue("$name", item.Name);
                insert.Parameters.AddWithValue("$normName", item.NormalizedName);
                insert.Parameters.AddWithValue("$number", item.Number);
                insert.Parameters.AddWithValue("$normNum", item.NormalizedNumber);
                insert.Parameters.AddWithValue("$today", today);
                insert.Parameters.AddWithValue("$now", now);
                insert.Parameters.AddWithValue("$actor", actor);
                insert.Parameters.AddWithValue("$assignment", "ASG-" + Guid.NewGuid().ToString("N"));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                createdIds.Add(personId);
            }
            await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                "PERSONNEL_IMPORT", "استيراد أفراد من ملف Excel",
                actor, username, username, "PersonImport", token, "دفعة استيراد",
                null, new JsonObject { ["createdCount"] = rows.Count,
                    ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)JsonValue.Create(r.Number)).ToArray()) }.ToJsonString(),
                $"إنشاء {rows.Count} سجل فرد جديد فقط دون تعديل أي تحضير أو هوية موجودة.",
                "اعتماد كشف Excel بعد المعاينة", today, ct.CorrelationId(), ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(new JsonObject { ["createdCount"] = rows.Count,
                ["committedAtUtc"] = now, ["importToken"] = token },
                events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonIdentityUpdated,
                    today, createdIds));
        }, ct).ConfigureAwait(false);
    }

    private static JsonArray PreviewNames(JsonArray accepted)
    {
        var preview = new JsonArray();
        foreach (var row in accepted.Take(20))
        {
            var x = (JsonObject)row!;
            preview.Add(new JsonObject { ["name"] = x["name"]!.GetValue<string>(),
                ["serviceNumberA"] = x["serviceNumberA"]!.GetValue<string>() });
        }
        return preview;
    }

    private static BridgeResponse Stale() => BridgeHelpers.Error(
        BridgeErrorCodes.ImportPlanStale, "انتهت صلاحية المعاينة أو تغيرت البيانات؛ أعد استيراد الملف.");
    private static BridgeResponse Invalid(string text) => BridgeHelpers.Error(BridgeErrorCodes.InvalidWorkbook, text);

    private static List<PersonInput> ParseWorkbook(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
            if (zip.Entries.Count is < 3 or > 150) throw new InvalidDataException("أجزاء ملف Excel غير متوقعة.");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Contains("..", StringComparison.Ordinal) ||
                    entry.FullName.StartsWith("/", StringComparison.Ordinal) ||
                    entry.Length > 6_000_000 ||
                    (total += entry.Length) > 10_000_000)
                    throw new InvalidDataException("ملف Excel غير آمن أو مضغوط بصورة غير متوقعة.");
            }
            if (zip.GetEntry("[Content_Types].xml") is null ||
                zip.GetEntry("xl/workbook.xml") is null)
                throw new InvalidDataException("هذا ليس ملف Excel بصيغة xlsx.");
            var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")
                ?? throw new InvalidDataException("الورقة الأولى غير متاحة (sheet1.xml).");
            string[] strings = [];
            var shared = zip.GetEntry("xl/sharedStrings.xml");
            if (shared is not null)
            {
                XDocument list = SafeXml(shared);
                strings = list.Descendants(Ns + "si")
                    .Select(si => string.Concat(si.Descendants(Ns + "t").Select(n => n.Value))).ToArray();
                if (strings.Length > 50_000) throw new InvalidDataException("عدد النصوص داخل Excel كبير جداً.");
            }
            XDocument document = SafeXml(sheet);
            var rows = document.Descendants(Ns + "sheetData").Elements(Ns + "row").ToList();
            if (rows.Count is < 2 or > MaxRecords + 6)
                throw new InvalidDataException("يجب أن يحتوي الكشف على عناوين وصفوف أفراد (الحد 500 فرد).");
            string Value(XElement cell)
            {
                string raw = cell.Element(Ns + "v")?.Value ?? "";
                string kind = (string?)cell.Attribute("t") ?? "";
                if (kind == "s")
                {
                    if (!int.TryParse(raw, out int index) || index < 0 || index >= strings.Length)
                        throw new InvalidDataException("فهرس النصوص في Excel غير صالح.");
                    return strings[index].Trim();
                }
                if (kind == "inlineStr")
                    return string.Concat(cell.Descendants(Ns + "t").Select(x => x.Value)).Trim();
                return raw.Trim();
            }
            static int Col(XElement cell)
            {
                string address = (string?)cell.Attribute("r") ?? "";
                int idx = 0;
                foreach (char c in address)
                {
                    if (!char.IsLetter(c)) break;
                    idx = checked(idx * 26 + char.ToUpperInvariant(c) - 'A' + 1);
                }
                return idx;
            }
            // Some official monthly workbooks put a report title and period above the header.
            // Locate the first valid header within five rows, never interpret title rows as persons.
            int headerIndex = -1, nameCol = 0, numberCol = 0;
            for (int index = 0; index < Math.Min(rows.Count, 5); index++)
            {
                var headers = rows[index].Elements(Ns + "c")
                    .Select(cell => (column: Col(cell), value: Value(cell))).ToList();
                int name = headers.Where(x => x.value is "الاسم" or "الاسم الكامل" or "اسم الفرد")
                    .Select(x => x.column).FirstOrDefault();
                int number = headers.Where(x => x.value is "الرقم (ع)" or "الرقم ع" or "الرقم" or "الرقم(ع)")
                    .Select(x => x.column).FirstOrDefault();
                if (name > 0 && number > 0 && name != number)
                {
                    headerIndex = index; nameCol = name; numberCol = number;
                    break;
                }
            }
            if (headerIndex < 0)
                throw new InvalidDataException("تحتاج ورقة Excel إلى عمودي «الاسم» و«الرقم (ع)» ضمن الصفوف الخمسة الأولى.");
            var records = new List<PersonInput>();
            foreach (var row in rows.Skip(headerIndex + 1))
            {
                int rowIndex = int.TryParse((string?)row.Attribute("r"), out int n) ? n : records.Count + 2;
                var cells = row.Elements(Ns + "c").ToDictionary(Col);
                cells.TryGetValue(nameCol, out var nameCell);
                cells.TryGetValue(numberCol, out var numberCell);
                string name = nameCell is null ? "" : Value(nameCell);
                string number = numberCell is null ? "" : Value(numberCell);
                if (name.Length == 0 && number.Length == 0) continue;
                if (numberCell is not null && (string?)numberCell.Attribute("t") is not ("inlineStr" or "s" or "str"))
                    throw new InvalidDataException($"الرقم (ع) في الصف {rowIndex} يجب أن يكون نصاً للحفاظ على الأصفار.");
                records.Add(new PersonInput(rowIndex, name, number,
                    ArabicTextNormalizer.NormalizeForSearch(name),
                    ArabicTextNormalizer.NormalizeForSearch(number)));
                if (records.Count > MaxRecords) throw new InvalidDataException("تجاوز عدد الأفراد الحد الآمن.");
            }
            return records;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException or InvalidOperationException or XmlException)
        { throw new InvalidDataException("ملف Excel تالف أو لا يطابق نموذج الكشف المعتمد.", ex); }
    }

    private static XDocument SafeXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 6_000_000, MaxCharactersFromEntities = 0
        });
        return XDocument.Load(reader);
    }
}
