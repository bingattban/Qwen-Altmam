using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Offline, read-only monthly export of effective-dated SQLCipher projections.
/// Produces native Office Open XML, not a CSV renamed to .xlsx. The renderer
/// writes inline string cells (never formulas), with bounded/paged database reads.
/// No user-supplied filesystem path or existing file is overwritten.
/// </summary>
public sealed class MonthlyExcelExportService
{
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private readonly DailyMonthlyReadService _read;
    private readonly AlTamamDbContext _db;

    public MonthlyExcelExportService(AlTamamDbContext db) { _db=db; _read = new DailyMonthlyReadService(db); }

    private static bool ValidPeriod(JsonObject? input, out int year, out int month,
        out string siteId, out string term)
    {
        year = 0; month = 0; siteId = ""; term = "";
        try
        {
            year = input?["year"]?.GetValue<int>() ?? 0;
            month = input?["month"]?.GetValue<int>() ?? 0;
            siteId = input?["siteId"]?.GetValue<string>()?.Trim() ?? "";
            term = input?["term"]?.GetValue<string>()?.Trim() ?? "";
        }
        catch { return false; }
        return year is >= 1900 and <= 2199 && month is >= 1 and <= 12
            && siteId.Length <= 128 && !siteId.Any(char.IsControl)
            && term.Length <= 100 && !term.Any(char.IsControl);
    }

    private static bool SafeOutputName(string? input,out string name)
    {
        name="";
        if(input is null)return true;
        string proposed=input.Trim();
        if(proposed.Length is <5 or >124 ||
           !proposed.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))return false;
        string stem=proposed[..^5];
        if(stem.Length is <1 or >119 || stem!=stem.Trim() ||
           !stem.All(c=>char.IsLetterOrDigit(c)||c is ' ' or '-' or '_' or '(' or ')'))
            return false;
        if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","LPT1","LPT2"}
            .Contains(stem,StringComparer.OrdinalIgnoreCase))return false;
        name=stem+".xlsx";
        return true;
    }

    public Task<BridgeResponse> PeriodsAsync(CancellationToken ct) => _read.ArchivePeriodsAsync(ct);

    public async Task<BridgeResponse> PreviewAsync(JsonObject? input, CancellationToken ct)
    {
        if (!ValidPeriod(input, out int year, out int month, out string site, out string term))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "الشهر أو السنة أو الموقع غير صالح للتصدير.");
        var page = await _read.MonthlyGridAsync(new JsonObject
        {
            ["year"] = year, ["month"] = month, ["siteId"] = site,
            ["term"] = term, ["archiveMode"] = true, ["page"] = 1, ["pageSize"] = 1
        }, ct).ConfigureAwait(false);
        if (!page.Success) return page;
        long total = ((JsonObject)page.Data!)["total"]!.GetValue<long>();
        if (total > 100000)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "عدد الأفراد يتجاوز حد التصدير الآمن (100000).");
        return BridgeHelpers.Ok(new JsonObject
        {
            ["year"] = year, ["month"] = month, ["siteId"] = site, ["term"] = term,
            ["personCount"] = total, ["daysInMonth"] = DateTime.DaysInMonth(year, month),
            ["format"] = "xlsx"
        });
    }

    public async Task<BridgeResponse> ExportAsync(JsonObject? input, CancellationToken ct,
        Action<int,int>? progress=null)
    {
        // All Sites produces one consolidated workbook plus a separate workbook
        // for every historically represented site. Single-site exports are unchanged.
        if(input is not null && (input["siteId"]?.ToString()??"").Length==0 &&
           input["consolidatedOnly"]?.ToString()!="true")
            return await ExportAllSitesAsync(input,ct,progress).ConfigureAwait(false);
        var preview = await PreviewAsync(input, ct).ConfigureAwait(false);
        if (!preview.Success) return preview;
        var previewData = (JsonObject)preview.Data!;
        long total = previewData["personCount"]!.GetValue<long>();
        if (total == 0)
            return BridgeHelpers.Error(BridgeErrorCodes.MissingData, "لا توجد أسماء في الشهر والموقع المحددين للتصدير.");
        int year = previewData["year"]!.GetValue<int>();
        int month = previewData["month"]!.GetValue<int>();
        int days = previewData["daysInMonth"]!.GetValue<int>();
        string site = previewData["siteId"]!.GetValue<string>();
        string term = previewData["term"]!.GetValue<string>();
        // The output directory is explicitly selected locally and validated again on
        // the C# side; never allow filenames to supply path separators.
        string selected;
        string? requestedName;
        try
        {
            selected=input?["directory"]?.GetValue<string>() ??
                await new OutputDirectorySettings(_db).GetAsync(ct).ConfigureAwait(false);
            requestedName=input?["fileName"]?.GetValue<string>();
        }
        catch(InvalidOperationException)
        {
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "مجلد أو اسم ملف التصدير غير صالح.");
        }
        if(!SafeOutputName(requestedName,out string chosenName))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "اسم الملف يجب أن يكون نصاً محلياً آمناً وينتهي بالامتداد xlsx.");
        if (!OutputDirectorySettings.IsSafeDirectory(selected,_db.DatabasePath,out string directory))
            return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed,
                "مجلد النسخ والتصدير غير متاح؛ غيّره من الإعدادات.");
        string basename = requestedName is null ?
            $"التمام الشهري_{year:D4}-{month:D2}_تصدير-{DateTime.Now:yyyy-MM-dd}_{Guid.NewGuid():N}.xlsx" :
            chosenName;
        string destination = Path.Combine(directory, basename);
        string partial = destination + ".part";
        progress?.Invoke(0,checked((int)total));
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                AddPart(archive, "[Content_Types].xml", w =>
                {
                    w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                    ContentType(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                    ContentType(w, "xml", "application/xml");
                    Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                    Override(w, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                    w.WriteEndElement();
                });
                AddPart(archive, "_rels/.rels", w => WriteRelationships(w,
                    ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml")));
                AddPart(archive, "xl/_rels/workbook.xml.rels", w => WriteRelationships(w,
                    ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml")));
                AddPart(archive, "xl/workbook.xml", w =>
                {
                    w.WriteStartElement("workbook", SpreadsheetNs);
                    w.WriteAttributeString("xmlns", "r", null, OfficeRelNs);
                    w.WriteStartElement("sheets", SpreadsheetNs);
                    w.WriteStartElement("sheet", SpreadsheetNs);
                    w.WriteAttributeString("name", "التمام الشهري");
                    w.WriteAttributeString("sheetId", "1");
                    w.WriteAttributeString("r", "id", OfficeRelNs, "rId1");
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteEndElement();
                });
                // Excel sheet is streamed, one bounded 100-person SQLCipher page at a time.
                var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
                using var target = entry.Open();
                using var writer = XmlWriter.Create(target, new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false), CloseOutput = false, Async = false
                });
                writer.WriteStartDocument();
                writer.WriteStartElement("worksheet", SpreadsheetNs);
                writer.WriteStartElement("sheetViews", SpreadsheetNs);
                writer.WriteStartElement("sheetView", SpreadsheetNs);
                writer.WriteAttributeString("workbookViewId", "0");
                writer.WriteAttributeString("rightToLeft", "1");
                writer.WriteStartElement("pane", SpreadsheetNs);
                writer.WriteAttributeString("xSplit", "4");
                writer.WriteAttributeString("ySplit", "3");
                writer.WriteAttributeString("topLeftCell", "E4");
                writer.WriteAttributeString("activePane", "bottomRight");
                writer.WriteAttributeString("state", "frozen");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteStartElement("sheetData", SpreadsheetNs);
                WriteRow(writer, 1, new [] { "التمام الشهري — كشف محفوظ" });
                WriteRow(writer, 2, new [] { $"الشهر: {year:D4}-{month:D2}", "الموقع:",
                    site.Length == 0 ? "جميع المواقع" : site, "البحث:",
                    term.Length == 0 ? "جميع الأفراد" : term });
                var headings = new List<string> { "م", "الرقم (ع)", "الاسم", "الموقع" };
                for (int d = 1; d <= days; d++) headings.Add(d.ToString("00", CultureInfo.InvariantCulture));
                WriteRow(writer, 3, headings);
                long rowNumber = 0;
                for (int pageNumber = 1; rowNumber < total; pageNumber++)
                {
                    ct.ThrowIfCancellationRequested();
                    var data = await _read.MonthlyGridAsync(new JsonObject
                    {
                        ["year"] = year, ["month"] = month, ["siteId"] = site,
                        ["term"] = term, ["archiveMode"] = true, ["page"] = pageNumber, ["pageSize"] = 100
                    }, ct).ConfigureAwait(false);
                    if (!data.Success) throw new IOException("تعذر قراءة الصفحة المطلوبة من سجل التمام.");
                    var items = (JsonArray)((JsonObject)data.Data!)["items"]!;
                    if (items.Count == 0) throw new IOException("تغير مصدر البيانات أثناء إنشاء الكشف.");
                    foreach (JsonNode? item in items)
                    {
                        var person = (JsonObject)item!;
                        rowNumber++;
                        var cols = new List<string>
                        {
                            rowNumber.ToString(CultureInfo.InvariantCulture),
                            person["serviceNumberA"]?.GetValue<string>() ?? "",
                            person["name"]?.GetValue<string>() ?? "",
                            person["siteName"]?.GetValue<string>() ?? ""
                        };
                        var entries = (JsonObject)person["days"]!;
                        for (int d = 1; d <= days; d++)
                        {
                            string date = $"{year:D4}-{month:D2}-{d:D2}";
                            cols.Add((entries[date] as JsonObject)?["name"]?.GetValue<string>() ?? "");
                        }
                        WriteRow(writer, checked((int)rowNumber + 3), cols);
                    }
                    progress?.Invoke(checked((int)rowNumber),checked((int)total));
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndDocument();
                writer.Flush();
            }
            // Never overwrite a previous export. Atomic same-directory rename avoids partial .xlsx.
            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: false);
            return BridgeHelpers.Ok(new JsonObject
            {
                ["filePath"] = destination, ["fileName"] = basename,
                ["personCount"] = total, ["year"] = year, ["month"] = month, ["term"] = term,
                ["siteId"] = site, ["format"] = "xlsx"
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed, "تعذر كتابة ملف Excel؛ تحقق من مساحة التخزين وصلاحيات المجلد."); }
        catch (UnauthorizedAccessException) { return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed, "لا توجد صلاحية كتابة في مجلد التقارير."); }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }


    private async Task<BridgeResponse> ExportAllSitesAsync(JsonObject input,
        CancellationToken ct,Action<int,int>? progress)
    {
        var consolidated=await PreviewAsync(input,ct).ConfigureAwait(false);
        if(!consolidated.Success)return consolidated;
        var data=(JsonObject)consolidated.Data!;
        long combined=data["personCount"]!.GetValue<long>();
        if(combined==0)
            return BridgeHelpers.Error(BridgeErrorCodes.MissingData,
                "لا توجد أسماء لهذا الشهر لتصدير كشف موحد ومواقع منفصلة.");
        var sitePlans=new List<(string SiteId,long Count)>();
        using(var connection=_db.CreateOpenConnection())
        using(var cmd=connection.CreateCommand())
        {
            // Include retired sites if they still have historical membership.
            cmd.CommandText="SELECT SiteId FROM Sites ORDER BY IsSystemSite DESC,VisibleName,SiteId;";
            var ids=new List<string>();
            using(var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while(await reader.ReadAsync(ct).ConfigureAwait(false))
                    ids.Add(reader.GetString(0));
            foreach(string siteId in ids)
            {
                ct.ThrowIfCancellationRequested();
                var candidate=(JsonObject)input.DeepClone();
                candidate["siteId"]=siteId;
                var preview=await PreviewAsync(candidate,ct).ConfigureAwait(false);
                if(!preview.Success)return preview;
                long count=((JsonObject)preview.Data!)["personCount"]!.GetValue<long>();
                if(count>0)sitePlans.Add((siteId,count));
            }
        }
        long sum=combined+sitePlans.Sum(x=>x.Count);
        if(sum>int.MaxValue)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "تجاوز عدد صفوف التصدير الجماعي الحد الآمن.");
        var created=new List<string>();
        var files=new JsonArray();
        int completed=0;
        bool success=false;
        try
        {
            async Task<BridgeResponse> OneAsync(string siteId,int siteOrdinal)
            {
                var selected=(JsonObject)input.DeepClone();
                selected["siteId"]=siteId;
                if(siteOrdinal>0 && selected["fileName"] is not null)
                {
                    string requested=selected["fileName"]!.GetValue<string>();
                    if(!SafeOutputName(requested,out string safe))
                        return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                            "اسم ملف التصدير المقترح غير صالح.");
                    selected["fileName"]=safe[..^5]+"-موقع-"+siteOrdinal.ToString("D2",CultureInfo.InvariantCulture)+".xlsx";
                }
                selected["consolidatedOnly"]=true;
                int before=completed;
                return await ExportAsync(selected,ct,(done,total)=>
                    progress?.Invoke(checked(before+done),checked((int)sum))).ConfigureAwait(false);
            }
            var all=await OneAsync("",0).ConfigureAwait(false);
            if(!all.Success)return all;
            var combinedData=(JsonObject)all.Data!;
            string combinedPath=combinedData["filePath"]!.GetValue<string>();
            created.Add(combinedPath);
            files.Add(new JsonObject{["siteId"]="",
                ["filePath"]=combinedPath,["personCount"]=combined});
            completed=checked((int)combined);
            for(int siteOrdinal=0;siteOrdinal<sitePlans.Count;siteOrdinal++)
            {
                ct.ThrowIfCancellationRequested();
                var plan=sitePlans[siteOrdinal];
                var individual=await OneAsync(plan.SiteId,siteOrdinal+1).ConfigureAwait(false);
                if(!individual.Success)return individual;
                var entry=(JsonObject)individual.Data!;
                string file=entry["filePath"]!.GetValue<string>();
                created.Add(file);
                files.Add(new JsonObject{["siteId"]=plan.SiteId,
                    ["filePath"]=file,["personCount"]=plan.Count});
                completed=checked(completed+(int)plan.Count);
            }
            progress?.Invoke(completed,checked((int)sum));
            success=true;
            return BridgeHelpers.Ok(new JsonObject{
                ["filePath"]=combinedPath,["format"]="xlsx",["personCount"]=combined,
                ["files"]=files,["fileCount"]=files.Count,
                ["year"]=data["year"]!.GetValue<int>(),["month"]=data["month"]!.GetValue<int>()
            });
        }
        finally
        {
            // An incomplete set must never be presented as successfully exported.
            // Only remove new files created by THIS attempt, never existing exports.
            if(!success)foreach(var file in created)
            {
                try{if(File.Exists(file))File.Delete(file);}
                catch(IOException){}
                catch(UnauthorizedAccessException){}
            }
        }
    }

    private static void WriteRow(XmlWriter w, int index, IEnumerable<string> values)
    {
        w.WriteStartElement("row", SpreadsheetNs);
        w.WriteAttributeString("r", index.ToString(CultureInfo.InvariantCulture));
        int column = 0;
        foreach (string value in values)
        {
            column++;
            w.WriteStartElement("c", SpreadsheetNs);
            w.WriteAttributeString("r", Column(column) + index.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("t", "inlineStr");
            w.WriteStartElement("is", SpreadsheetNs);
            w.WriteStartElement("t", SpreadsheetNs);
            w.WriteString(value);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static string Column(int number)
    {
        var s = new StringBuilder();
        while (number > 0)
        {
            number--; s.Insert(0, (char)('A' + number % 26)); number /= 26;
        }
        return s.ToString();
    }

    private static void AddPart(ZipArchive zip, string path, Action<XmlWriter> action)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var target = entry.Open();
        using var writer = XmlWriter.Create(target, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        writer.WriteStartDocument();
        action(writer);
        writer.WriteEndDocument();
    }

    private static void ContentType(XmlWriter w, string extension, string contentType)
    {
        w.WriteStartElement("Default");
        w.WriteAttributeString("Extension", extension);
        w.WriteAttributeString("ContentType", contentType);
        w.WriteEndElement();
    }

    private static void Override(XmlWriter w, string name, string contentType)
    {
        w.WriteStartElement("Override");
        w.WriteAttributeString("PartName", name);
        w.WriteAttributeString("ContentType", contentType);
        w.WriteEndElement();
    }

    private static void WriteRelationships(XmlWriter w, params (string id, string type, string target)[] relations)
    {
        w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        foreach (var (id, type, target) in relations)
        {
            w.WriteStartElement("Relationship");
            w.WriteAttributeString("Id", id);
            w.WriteAttributeString("Type", type);
            w.WriteAttributeString("Target", target);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }
}
