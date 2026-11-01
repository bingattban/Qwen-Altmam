using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using AlTamam.Application.Reports;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Excel.Reports;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Real, offline Open XML export for the ONE report screen and the separately
/// filtered movement/dismissal archives. Database facts are always fetched by
/// immutable PersonId; values are inline text so malicious names are not Excel
/// formulas. Export is an audited operation, never a client-side data dump.
/// </summary>
public sealed class PersonnelExcelExportService
{
    private readonly AlTamamDbContext _db;
    private const int MaxRowsPerSheet = 1_048_576;
    private const int MaxExportEvents = 150_000;
    public PersonnelExcelExportService(AlTamamDbContext db) => _db = db;

    private static string Read(JsonObject? node, string name)
    {
        try { return node?[name]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }
    private static string Val(JsonNode? node, string name)
    {
        try { return node?[name]?.GetValue<string>() ?? ""; }
        catch { return node?[name]?.ToString() ?? ""; }
    }
    private static long Number(JsonNode? node, string name)
    {
        try { return node?[name]?.GetValue<long>() ?? 0; }
        catch { try { return node?[name]?.GetValue<int>() ?? 0; } catch { return 0; } }
    }
    private static JsonArray? Array(JsonNode? node, string key) => node?[key] as JsonArray;

    private static bool TrySelected(JsonObject? input, out string[] ids)
    {
        ids = [];
        try
        {
            if(input?["personIds"] is JsonArray list)
                ids = list.Select(x => x?.GetValue<string>()?.Trim() ?? "").ToArray();
            else if(Read(input,"personId").Length>0)
                ids = [Read(input,"personId")];
            return ids.Length is >=1 and <=100 &&
                ids.All(x => x.Length is >=3 and <=128) &&
                ids.Distinct(StringComparer.Ordinal).Count()==ids.Length;
        }
        catch { return false; }
    }
    private static string FileName(string label, string id)
        => label + "_" + DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture) +
           "_" + id[..12] + ".xlsx";
    private static bool ExportId(JsonObject? payload, out string id)
    {
        string source = Read(payload,"exportId");
        if(source.Length==0){id=Guid.NewGuid().ToString("N");return true;}
        if(Guid.TryParse(source,out var parsed)){id=parsed.ToString("N");return true;}
        id="";return false;
    }
    private async Task<string?> FolderAsync(CancellationToken ct)
    {
        var candidate=await new OutputDirectorySettings(_db).GetAsync(ct).ConfigureAwait(false);
        return OutputDirectorySettings.IsSafeDirectory(candidate,_db.DatabasePath,out var folder)
            ? folder:null;
    }
    private static BridgeResponse Invalid(string message)
        => BridgeHelpers.Error(BridgeErrorCodes.ValidationError,message);
    private static BridgeResponse ExportFailure(string message)
        => BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed,message);

    public async Task<BridgeResponse> PreviewReportAsync(JsonObject? payload,CancellationToken ct)
    {
        if(!TrySelected(payload,out var ids))
            return Invalid("حدد فرداً أو مجموعة بأرقام هوية داخلية صحيحة.");
        if(!ExportId(payload,out var id))
            return Invalid("معرف ملف التصدير غير صالح.");
        var service=new PersonnelReportService(_db);
        var request=(JsonObject)(payload?.DeepClone() ?? new JsonObject());
        request.Remove("exportPreview");
        request.Remove("mode");
        request.Remove("exportId");
        BridgeResponse test;
        if(ids.Length>1 || payload?.ContainsKey("personIds")==true)
            test=await service.ReportAsync(request,ct).ConfigureAwait(false);
        else
        {
            request.Remove("personIds");
            request["personId"]=ids[0];
            test=await service.ReportAsync(request,ct).ConfigureAwait(false);
        }
        if(!test.Success)return test;
        var folder=await FolderAsync(ct).ConfigureAwait(false);
        if(folder is null)return ExportFailure("مجلد التقارير غير متاح؛ حدد مجلداً محلياً قابلاً للكتابة من الإعدادات.");
        var sheets=new JsonArray("ملخص التقرير","التفاصيل التاريخية","سجل التنقلات",
            "تغييرات العمليات","الهوية والمواقع","تصحيحات التحضير");
        if(ids.Length<=12)
            for(int i=0;i<ids.Length;i++) sheets.Add("فرد "+(i+1).ToString("D2",CultureInfo.InvariantCulture));
        if(ids.Length>1)sheets.Add("الإجمالي اليومي");
        var title=ids.Length==1?"تقرير فرد":"تقرير جماعي";
        return BridgeHelpers.Ok(new JsonObject
        {
            ["mode"]="report",["personCount"]=ids.Length,
            ["fileName"]=FileName(title,id),["exportId"]=id,
            ["directory"]=folder,["worksheets"]=sheets,
            ["dateFrom"]=Read(payload,"dateFrom"),["dateTo"]=Read(payload,"dateTo"),
            ["siteId"]=Read(payload,"siteId"),["statusId"]=Read(payload,"statusId")
        });
    }

    private sealed record Sheet(string Name,List<string[]> Rows)
    {
        public void Add(params string[] cells)
        {
            if(Rows.Count>=MaxRowsPerSheet)
                throw new InvalidDataException("عدد صفوف Excel يتجاوز حد الورقة؛ قلّص فترة التقرير.");
            Rows.Add(cells);
        }
    }
    private static Sheet Start(string name,params string[] header)
    {
        var sheet=new Sheet(name,new List<string[]>());
        sheet.Add(header);
        return sheet;
    }
    private static string[] Record(JsonNode? node,params string[] fields)
        => fields.Select(key => Val(node,key)).ToArray();

    private async Task<List<Sheet>> ReportSheetsAsync(JsonObject payload,
        string[] ids,CancellationToken ct)
    {
        var service=new PersonnelReportService(_db);
        var summary=Start("ملخص التقرير","رقم الفرد في التقرير","الرقم (ع)","الاسم","حالة القيد",
            "تاريخ الإدراج","عدد أيام التحضير","عدد الحركات","الفترة من","الفترة إلى",
            "حالات التحضير وأعداد الأيام");
        var daily=Start("التفاصيل التاريخية","رقم الفرد في التقرير","الرقم (ع) التاريخي",
            "الاسم التاريخي","تاريخ التحضير","الحالة","الموقع المسجل",
            "مصدر التحضير","رقم المراجعة","زمن الاعتماد");
        var movements=Start("سجل التنقلات","رقم الفرد في التقرير","الرقم (ع)","الاسم",
            "نوع الحركة","الموقع السابق","الجهة الجديدة","تاريخ الحركة",
            "السبب","منفذ الحركة","زمن الاعتماد");
        var audit=Start("تغييرات العمليات","رقم الفرد في التقرير","الاسم","الرقم (ع)",
            "العملية","منفذ العملية","وقت تنفيذ العملية UTC","الوصف","القيمة السابقة",
            "القيمة الجديدة","السبب","تاريخ النفاذ");
        var membership=Start("الهوية والمواقع","رقم الفرد في التقرير","نوع الفترة",
            "الاسم / الموقع","الرقم (ع)","من","إلى","السبب / المنفذ");
        var revisions=Start("تصحيحات التحضير","رقم الفرد في التقرير",
            "تاريخ التحضير","الحالة السابقة","الحالة الجديدة","السبب",
            "المنفذ (المستخدم - الاسم المحفوظ)","توقيت التصحيح","نوع الصلاحية");
        var sheets=new List<Sheet>{summary,daily,movements,audit,membership,revisions};
        int exportedEvents=0;
        for(int i=0;i<ids.Length;i++)
        {
            ct.ThrowIfCancellationRequested();
            string personId=ids[i];
            string sectionNumber=(i+1).ToString(CultureInfo.InvariantCulture);
             var per=ids.Length<=12?Start("فرد "+(i+1).ToString("D2",CultureInfo.InvariantCulture),
                "رقم الفرد في التقرير","الرقم (ع)","الاسم","نوع القيد","التاريخ","الحالة / العملية","الموقع","التفاصيل"):null;
            for(int page=1;;page++)
            {
                var request=(JsonObject)payload.DeepClone();
                request.Remove("mode");request.Remove("personIds");
                request.Remove("exportId");request.Remove("exportPreview");
                request["personId"]=personId;
                request["attendancePage"]=page;request["identityPage"]=page;
                request["sitePage"]=page;request["movementPage"]=page;
                request["auditPage"]=page;
                request["revisionPage"]=page;
                var response=await service.ReportAsync(request,ct).ConfigureAwait(false);
                if(!response.Success)throw new InvalidDataException(response.UserMessage ?? "تعذر قراءة التقرير.");
                var data=(JsonObject)response.Data!;
                 var person=(JsonObject)data["person"]!;
                string name=Val(person,"name"),number=Val(person,"serviceNumberA");
                if(page==1)
                    summary.Add(sectionNumber,number,name,
                        Val(person,"active")=="true"?"نشط":"مؤرشف",
                        Val(person,"firstRecordedDate"),Number(data,"attendanceTotal").ToString(CultureInfo.InvariantCulture),
                        Number(data,"movementTotal").ToString(CultureInfo.InvariantCulture),
                        Read(payload,"dateFrom"),Read(payload,"dateTo"),
                        string.Join("، ",(Array(data,"statusTotals")??new JsonArray())
                            .Select(st=>Val(st,"statusName")+" ("+
                                Number(st,"count").ToString(CultureInfo.InvariantCulture)+")")));
                foreach(var record in Array(data,"attendance")??new JsonArray())
                {
                    daily.Add(sectionNumber,Val(record,"serviceNumberA"),Val(record,"name"),
                        Val(record,"date"),Val(record,"statusName"),Val(record,"site"),
                        Val(record,"source"),Val(record,"revision"),Val(record,"committedAtUtc"));
                    per?.Add(sectionNumber,number,name,"تحضير",Val(record,"date"),
                        Val(record,"statusName"),Val(record,"site"),Val(record,"source"));
                    exportedEvents++;
                }
                foreach(var record in Array(data,"movements")??new JsonArray())
                {
                    movements.Add(sectionNumber,Val(record,"serviceNumberA"),Val(record,"name"),
                        Val(record,"type"),Val(record,"source"),Val(record,"destination"),
                        Val(record,"date"),Val(record,"reason"),Val(record,"executor"),
                        Val(record,"committedAtUtc"));
                    per?.Add(sectionNumber,number,name,"حركة",Val(record,"date"),
                        Val(record,"type"),Val(record,"source"),Val(record,"destination"));
                    exportedEvents++;
                }
                foreach(var record in Array(data,"audit")??new JsonArray())
                {
                    audit.Add(sectionNumber,name,number,Val(record,"operation"),
                        Val(record,"executor"),Val(record,"executedAtUtc"),
                        Val(record,"description"),Val(record,"oldValueJson"),
                        Val(record,"newValueJson"),Val(record,"reason"),
                        Val(record,"effectiveDate"));
                    per?.Add(sectionNumber,number,name,"تعديل / عملية",
                        Val(record,"executedAtUtc"),Val(record,"operation"),"",
                        Val(record,"description"));
                    exportedEvents++;
                }
                foreach(var record in Array(data,"revisions")??new JsonArray())
                {
                    revisions.Add(sectionNumber,Val(record,"date"),Val(record,"before"),
                        Val(record,"after"),Val(record,"reason"),Val(record,"executor"),
                        Val(record,"executedAtUtc"),Val(record,"authorizationMethod"));
                    per?.Add(sectionNumber,number,name,"تصحيح تحضير",
                        Val(record,"date"),Val(record,"before")+" ← "+Val(record,"after"),
                        "",Val(record,"reason"));
                    exportedEvents++;
                }
                foreach(var record in Array(data,"identityHistory")??new JsonArray())
                {
                    membership.Add(sectionNumber,"هوية",Val(record,"name"),
                        Val(record,"serviceNumberA"),Val(record,"from"),
                        Val(record,"to"),Val(record,"reason"));
                    per?.Add(sectionNumber,Val(record,"serviceNumberA"),Val(record,"name"),
                        "تغيير هوية",Val(record,"from"),"تاريخ الهوية","",
                        Val(record,"reason"));
                    exportedEvents++;
                }
                foreach(var record in Array(data,"siteHistory")??new JsonArray())
                {
                    membership.Add(sectionNumber,"موقع",Val(record,"siteName"),
                        "",Val(record,"from"),Val(record,"to"),Val(record,"createdAtUtc"));
                    per?.Add(sectionNumber,"",name,"انتساب موقع",Val(record,"from"),
                        Val(record,"siteName"),"",Val(record,"createdAtUtc"));
                    exportedEvents++;
                }
                if(exportedEvents>MaxExportEvents)
                    throw new InvalidDataException("التقرير كبير جدًا للتصدير الكامل دفعة واحدة؛ حدّد مجموعة أو فترة أصغر. لم تُحذف أي وقائع.");
                if(!data["attendanceHasNext"]!.GetValue<bool>() &&
                   !data["movementHasNext"]!.GetValue<bool>() &&
                   !data["auditHasNext"]!.GetValue<bool>() &&
                   !data["revisionHasNext"]!.GetValue<bool>() &&
                   !data["identityHasNext"]!.GetValue<bool>() &&
                   !data["siteHasNext"]!.GetValue<bool>())
                    break;
                if(page>=100000)
                    throw new InvalidDataException("تجاوز التصدير عدد الصفحات الآمن دون حذف بيانات.");
            }
            if(per is not null)
            {
                PersonReportChronology.Sort(per.Rows);
                sheets.Add(per);
            }
        }
        // The summary must show BOTH independent person rows and the combined
        // status tally; identities are still never joined by displayed number.
        if(ids.Length>1)
        {
            var group=(JsonObject)payload.DeepClone();
            group.Remove("mode");group.Remove("personId");
            group.Remove("exportId");group.Remove("exportPreview");
            group["personIds"]=new JsonArray(ids.Select(id=>(JsonNode?)JsonValue.Create(id)).ToArray());
            var merged=await service.ReportAsync(group,ct).ConfigureAwait(false);
            if(!merged.Success)
                throw new InvalidDataException(merged.UserMessage ?? "تعذر احتساب إجمالي المجموعة.");
            var data=(JsonObject)merged.Data!;
            summary.Add("إجمالي المجموعة","","","","",
                Number(data,"attendanceTotal").ToString(CultureInfo.InvariantCulture),
                Number(data,"movementTotal").ToString(CultureInfo.InvariantCulture),
                Read(payload,"dateFrom"),Read(payload,"dateTo"),
                string.Join("، ",(Array(data,"statusTotals")??new JsonArray())
                    .Select(st=>Val(st,"statusName")+" ("+
                        Number(st,"count").ToString(CultureInfo.InvariantCulture)+")")));
            // Date+status is a separately paged SQL projection. Do not infer
            // event counts by combining same-named people or sampling page one.
            var calendar=Start("الإجمالي اليومي","تاريخ التحضير","الحالة",
                "عدد الوقائع","عدد الأفراد");
            for(int summaryPage=1;;summaryPage++)
            {
                ct.ThrowIfCancellationRequested();
                group["summaryPage"]=summaryPage;
                var pageResult=summaryPage==1?merged:await service.ReportAsync(group,ct).ConfigureAwait(false);
                if(!pageResult.Success)
                    throw new InvalidDataException(pageResult.UserMessage??"تعذر قراءة إجمالي التقرير اليومي.");
                var pageData=(JsonObject)pageResult.Data!;
                foreach(var row in Array(pageData,"dailyStatusTotals")??new JsonArray())
                {
                    calendar.Add(Val(row,"date"),Val(row,"statusName"),
                        Number(row,"count").ToString(CultureInfo.InvariantCulture),
                        Number(row,"personCount").ToString(CultureInfo.InvariantCulture));
                    if(calendar.Rows.Count>MaxExportEvents)
                        throw new InvalidDataException("إجمالي التقرير اليومي تجاوز الحد الآمن للتصدير.");
                }
                if(pageData["dailyStatusHasNext"]?.GetValue<bool>()!=true)break;
                if(summaryPage>=100000)
                    throw new InvalidDataException("لم تكتمل صفحات الملخص اليومي.");
            }
            sheets.Add(calendar);
        }
        return sheets;
    }

    private async Task<List<Sheet>> ArchiveSheetsAsync(JsonObject? payload,
        bool dismissal,CancellationToken ct)
    {
        string type=Read(payload,"type");
        if(!dismissal && type is not ("internal" or "external"))
            throw new ArgumentException("اختر أرشيف النقل الداخلي أو الخارجي.");
        var title=dismissal?"أرشيف الفصل":type=="internal"?"نقل داخلي":"نقل خارجي";
        var sheet=dismissal
            ? Start(title,"رقم الفرد في التقرير","الرقم (ع)","اسم الفرد",
                "الموقع","سبب الفصل","تاريخ الفصل","زمن الاعتماد","منفذ العملية")
            : Start(title,"رقم الفرد في التقرير","الرقم (ع)","اسم الفرد",
                "نوع النقل","من موقع","إلى جهة","تاريخ النقل","زمن الاعتماد");
        var svc=new PersonnelMovementService(_db);
        var sep=new PersonnelSeparationService(_db);
        // Workbook-local ordinals preserve distinct persons without exposing PersonId.
        var archiveOrdinals=new Dictionary<string,int>(StringComparer.Ordinal);
        int page=1;
        for(;;page++)
        {
            ct.ThrowIfCancellationRequested();
            var query=new JsonObject{
                ["page"]=page,["pageSize"]=100,
                ["term"]=Read(payload,"term"),["siteId"]=Read(payload,"siteId")
            };
            if(!dismissal)
            {
                query["type"]=type;
                query["dateFrom"]=Read(payload,"dateFrom");
                query["dateTo"]=Read(payload,"dateTo");
            }
            var response=dismissal
                ? await sep.ArchiveAsync(query,ct).ConfigureAwait(false)
                : await svc.ArchiveAsync(query,ct).ConfigureAwait(false);
            if(!response.Success)
                throw new ArgumentException(response.UserMessage ?? "تعذر قراءة الأرشيف.");
            var data=(JsonObject)response.Data!;
            if(Number(data,"total")>MaxExportEvents)
                throw new InvalidDataException("الأرشيف يتجاوز حجم تصدير واحد؛ ضيّق نطاق البحث أو الفترة.");
            foreach(var row in Array(data,"items")??new JsonArray())
            {
                string key=Val(row,"personId");
                if(!archiveOrdinals.TryGetValue(key,out int sequence))
                {
                    sequence=archiveOrdinals.Count+1;
                    archiveOrdinals[key]=sequence;
                }
                string section=sequence.ToString(CultureInfo.InvariantCulture);
                if(dismissal)
                    sheet.Add(new[]{section}.Concat(Record(row,"serviceNumberA","name",
                        "siteName","reason","date","committedAtUtc","actorDisplay")).ToArray());
                else
                    sheet.Add(new[]{section}.Concat(Record(row,"serviceNumberA","name",
                        "type","sourceSite","destination","date","committedAtUtc")).ToArray());
            }
            if(!data["hasNext"]!.GetValue<bool>())break;
            if(page>=100000)throw new InvalidDataException("لم يكتمل استعراض صفحات الأرشيف.");
        }
        if(sheet.Rows.Count==1)
            throw new InvalidDataException("لا توجد عمليات مطابقة في هذا الأرشيف لتصديرها.");
        return [sheet];
    }

    public async Task<BridgeResponse> ExportAsync(JsonObject? payload,
        BridgePrincipal actor,CancellationToken ct,Action<int,int>? progress=null)
    {
        string mode=Read(payload,"mode");
        string label;
        string[] ids=[];
        if(mode is "" or "report")
        {
            if(!TrySelected(payload,out ids))return Invalid("حدد الفرد أو المجموعة قبل التصدير.");
            var preview=await PreviewReportAsync(payload,ct).ConfigureAwait(false);
            if(!preview.Success)return preview;
            label=ids.Length==1?"تقرير فرد":"تقرير جماعي";
        }
        else if(mode=="movementArchive")
        {
            string type=Read(payload,"type");
            if(type is not ("internal" or "external"))
                return Invalid("اختر نوع النقل المسموح للتصدير.");
            label=type=="internal"?"نقل داخلي":"نقل خارجي";
        }
        else if(mode=="separationArchive")label="أرشيف الفصل";
        else return Invalid("نوع تقرير Excel غير معروف.");

        if(!ExportId(payload,out var exportId))return Invalid("معرف العملية غير صالح.");
        string? directory=await FolderAsync(ct).ConfigureAwait(false);
        if(directory is null)return ExportFailure("مجلد التصدير غير متاح؛ غيّره من الإعدادات.");
        string fileName=FileName(label,exportId),destination=Path.Combine(directory,fileName);
        string partial=destination+".part";
        if(File.Exists(destination))return Invalid("سبق إنشاء الملف لهذه العملية؛ راجع المجلد قبل إعادة التصدير.");
        try
        {
            ct.ThrowIfCancellationRequested();
            List<Sheet> sheets=mode=="movementArchive"
                ? await ArchiveSheetsAsync(payload,false,ct).ConfigureAwait(false)
                : mode=="separationArchive"
                ? await ArchiveSheetsAsync(payload,true,ct).ConfigureAwait(false)
                : await ReportSheetsAsync(payload!,ids,ct).ConfigureAwait(false);
            int totalRows=sheets.Sum(x=>x.Rows.Count);
            progress?.Invoke(0,totalRows);
            Directory.CreateDirectory(directory);
            using(var stream=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,leaveOpen:false,Encoding.UTF8))
                NativeWorkbook(zip,sheets,ct,progress);
            ct.ThrowIfCancellationRequested();
            ReportWorkbookCommit.VerifyFlushAndMove(partial,destination,ct);
            try
            {
                await _db.ExecuteInWriteTransactionAsync(async(connection,tx)=>{
                    await BridgeAuditWriter.WriteAsync(connection,tx,TimeProvider.System,
                        "PERSONNEL_EXCEL_EXPORT","تصدير Excel",
                        actor.UserId,actor.Username,actor.Username,
                        "ExcelExport",exportId,label,null,
                        new JsonObject{
                            ["fileName"]=fileName,["mode"]=mode,
                            ["personCount"]=ids.Length,
                            ["recordRows"]=sheets.Sum(x=>x.Rows.Count-1),
                            ["sheetCount"]=sheets.Count,
                            ["siteId"]=Read(payload,"siteId"),
                            ["dateFrom"]=Read(payload,"dateFrom"),
                            ["dateTo"]=Read(payload,"dateTo")
                        }.ToJsonString(),
                        "تصدير "+label+" إلى Excel دون تضمين محتوى الجداول في سجل العمليات.",
                        null,DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
                        exportId,ct).ConfigureAwait(false);
                    return 0;
                },ct).ConfigureAwait(false);
            }
            catch
            {
                try{File.Delete(destination);}catch(IOException){}
                catch(UnauthorizedAccessException){}
                throw;
            }
            return BridgeHelpers.Ok(new JsonObject{
                ["filePath"]=destination,["fileName"]=fileName,
                ["format"]="xlsx",["sheetCount"]=sheets.Count,
                ["recordRows"]=sheets.Sum(x=>x.Rows.Count-1)
            });
        }
        catch(OperationCanceledException){throw;}
        catch(InvalidDataException e){return ExportFailure(e.Message);}
        catch(ArgumentException e){return Invalid(e.Message);}
        catch(IOException){return ExportFailure("تعذر كتابة Excel؛ تحقق من صلاحية المجلد ومساحته وأعد المحاولة.");}
        catch(UnauthorizedAccessException){return ExportFailure("لا توجد صلاحية لحفظ الملف؛ اختر مجلدًا محليًا قابلًا للكتابة.");}
        finally
        {
            try{if(File.Exists(partial))File.Delete(partial);}catch(IOException){}
            catch(UnauthorizedAccessException){}
        }
    }

    // Personnel numbers always remain textual (preserving leading zeros).
    // Only explicitly designated counters and ISO dates become Excel numbers.
    private static bool NumericReportCell(string header,string value,out string serial,
        out string? style)
    {
        serial="";style=null;
        if(header is ("رقم الفرد في التقرير" or "عدد أيام التحضير" or
            "عدد الحركات" or "عدد الوقائع" or "عدد الأفراد" or "رقم المراجعة"))
        {
            if(long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out long count)
                && count>=0)
            {
                serial=count.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }
        if(header is ("تاريخ التحضير" or "تاريخ الحركة" or "تاريخ الفصل" or
            "تاريخ النفاذ" or "تاريخ الإدراج" or "تاريخ العودة" or
            "الفترة من" or "الفترة إلى" or "من" or "إلى" or "التاريخ"))
        {
            if(DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,
                DateTimeStyles.None,out var date))
            {
                serial=date.ToDateTime(TimeOnly.MinValue).ToOADate()
                    .ToString("0.########",CultureInfo.InvariantCulture);
                style="1";
                return true;
            }
        }
        if(header is ("زمن الاعتماد" or "وقت تنفيذ العملية UTC" or
            "وقت التصحيح" or "توقيت التصحيح" or "وقت تسجيل الهوية UTC" or
            "وقت تسجيل الانتساب UTC"))
        {
            if(DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,out var moment))
            {
                serial=moment.UtcDateTime.ToOADate()
                    .ToString("0.############",CultureInfo.InvariantCulture);
                style="2";
                return true;
            }
        }
        return false;
    }

    private static void NativeWorkbook(ZipArchive zip,List<Sheet> sheets,
        CancellationToken ct,Action<int,int>? progress)
    {
        int completedRows=0,totalRows=sheets.Sum(x=>x.Rows.Count);
        const string baseNs="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string relationships="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        static void Part(ZipArchive zip,string name,Action<XmlWriter> render)
        {
            var entry=zip.CreateEntry(name,CompressionLevel.Optimal);
            using var stream=entry.Open();
            using var writer=XmlWriter.Create(stream,new XmlWriterSettings{
                Encoding=new UTF8Encoding(false),CloseOutput=false
            });
            writer.WriteStartDocument();
            render(writer);
            writer.WriteEndDocument();
        }
        static void Content(XmlWriter w,string name,string type)
        {
            w.WriteStartElement("Override");
            w.WriteAttributeString("PartName",name);
            w.WriteAttributeString("ContentType",type);
            w.WriteEndElement();
        }
        static void Relationship(XmlWriter w,string id,string type,string target)
        {
            w.WriteStartElement("Relationship");
            w.WriteAttributeString("Id",id);
            w.WriteAttributeString("Type",type);
            w.WriteAttributeString("Target",target);
            w.WriteEndElement();
        }
        Part(zip,"[Content_Types].xml",w=>{
            w.WriteStartElement("Types","http://schemas.openxmlformats.org/package/2006/content-types");
            foreach(var (ext,content) in new[]{
                ("rels","application/vnd.openxmlformats-package.relationships+xml"),
                ("xml","application/xml")})
            {
                w.WriteStartElement("Default");w.WriteAttributeString("Extension",ext);
                w.WriteAttributeString("ContentType",content);w.WriteEndElement();
            }
            Content(w,"/xl/workbook.xml",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Content(w,"/xl/styles.xml",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for(int i=0;i<sheets.Count;i++)
                Content(w,$"/xl/worksheets/sheet{i+1}.xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            w.WriteEndElement();
        });
        Part(zip,"_rels/.rels",w=>{
            w.WriteStartElement("Relationships","http://schemas.openxmlformats.org/package/2006/relationships");
            Relationship(w,"rId1",relationships+"/officeDocument","xl/workbook.xml");
            w.WriteEndElement();
        });
        Part(zip,"xl/_rels/workbook.xml.rels",w=>{
            w.WriteStartElement("Relationships","http://schemas.openxmlformats.org/package/2006/relationships");
            for(int i=0;i<sheets.Count;i++)
                Relationship(w,"rId"+(i+1),relationships+"/worksheet",
                    $"worksheets/sheet{i+1}.xml");
            Relationship(w,"rId"+(sheets.Count+1),relationships+"/styles","styles.xml");
            w.WriteEndElement();
        });
        Part(zip,"xl/workbook.xml",w=>{
            w.WriteStartElement("workbook",baseNs);
            w.WriteAttributeString("xmlns","r",null,relationships);
            w.WriteStartElement("sheets",baseNs);
            for(int i=0;i<sheets.Count;i++)
            {
                w.WriteStartElement("sheet",baseNs);
                w.WriteAttributeString("name",sheets[i].Name);
                w.WriteAttributeString("sheetId",(i+1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("r","id",relationships,"rId"+(i+1));
                w.WriteEndElement();
            }
            w.WriteEndElement();w.WriteEndElement();
        });
        Part(zip,"xl/styles.xml",w=>{
            w.WriteStartElement("styleSheet",baseNs);
            w.WriteStartElement("numFmts",baseNs);
            w.WriteAttributeString("count","2");
            foreach(var (id,format) in new[]{("164","yyyy-mm-dd"),("165","yyyy-mm-dd hh:mm")})
            {
                w.WriteStartElement("numFmt",baseNs);
                w.WriteAttributeString("numFmtId",id);
                w.WriteAttributeString("formatCode",format);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("fonts",baseNs);w.WriteAttributeString("count","1");
            w.WriteStartElement("font",baseNs);
            w.WriteStartElement("sz",baseNs);w.WriteAttributeString("val","11");w.WriteEndElement();
            w.WriteStartElement("name",baseNs);w.WriteAttributeString("val","Aptos");w.WriteEndElement();
            w.WriteEndElement();w.WriteEndElement();
            w.WriteStartElement("fills",baseNs);w.WriteAttributeString("count","2");
            foreach(string pattern in new[]{"none","gray125"})
            {
                w.WriteStartElement("fill",baseNs);w.WriteStartElement("patternFill",baseNs);
                w.WriteAttributeString("patternType",pattern);
                w.WriteEndElement();w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("borders",baseNs);w.WriteAttributeString("count","1");
            w.WriteStartElement("border",baseNs);w.WriteEndElement();w.WriteEndElement();
            w.WriteStartElement("cellStyleXfs",baseNs);w.WriteAttributeString("count","1");
            w.WriteStartElement("xf",baseNs);w.WriteAttributeString("numFmtId","0");
            w.WriteAttributeString("fontId","0");w.WriteAttributeString("fillId","0");
            w.WriteAttributeString("borderId","0");w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("cellXfs",baseNs);w.WriteAttributeString("count","3");
            foreach(string id in new[]{"0","164","165"})
            {
                w.WriteStartElement("xf",baseNs);
                w.WriteAttributeString("numFmtId",id);
                w.WriteAttributeString("fontId","0");w.WriteAttributeString("fillId","0");
                w.WriteAttributeString("borderId","0");w.WriteAttributeString("xfId","0");
                if(id!="0")w.WriteAttributeString("applyNumberFormat","1");
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("cellStyles",baseNs);w.WriteAttributeString("count","1");
            w.WriteStartElement("cellStyle",baseNs);
            w.WriteAttributeString("name","Normal");w.WriteAttributeString("xfId","0");
            w.WriteAttributeString("builtinId","0");w.WriteEndElement();w.WriteEndElement();
            w.WriteEndElement();
        });
        for(int i=0;i<sheets.Count;i++)
        {
            var sheet=sheets[i];
            if(sheet.Rows.Count>MaxRowsPerSheet)
                throw new InvalidDataException("تجاوز Excel الحد الأقصى للصفوف؛ لم يُقتطع أي سجل.");
            Part(zip,$"xl/worksheets/sheet{i+1}.xml",w=>{
                w.WriteStartElement("worksheet",baseNs);
                w.WriteStartElement("sheetViews",baseNs);
                w.WriteStartElement("sheetView",baseNs);
                w.WriteAttributeString("workbookViewId","0");
                w.WriteAttributeString("rightToLeft","1");
                w.WriteStartElement("pane",baseNs);
                w.WriteAttributeString("ySplit","1");
                w.WriteAttributeString("topLeftCell","A2");
                w.WriteAttributeString("activePane","bottomLeft");
                w.WriteAttributeString("state","frozen");
                w.WriteEndElement();w.WriteEndElement();w.WriteEndElement();
                w.WriteStartElement("cols",baseNs);
                for(int c=0;c<sheet.Rows[0].Length;c++)
                {
                    w.WriteStartElement("col",baseNs);
                    w.WriteAttributeString("min",(c+1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("max",(c+1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("width",c<4?"24":"34");
                    w.WriteAttributeString("customWidth","1");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteStartElement("sheetData",baseNs);
                for(int row=0;row<sheet.Rows.Count;row++)
                {
                    ct.ThrowIfCancellationRequested();
                    w.WriteStartElement("row",baseNs);
                    w.WriteAttributeString("r",(row+1).ToString(CultureInfo.InvariantCulture));
                    int col=0;
                    foreach(var val in sheet.Rows[row])
                    {
                        col++;
                        int n=col;var label=new StringBuilder();
                        while(n>0){n--;label.Insert(0,(char)('A'+n%26));n/=26;}
                        w.WriteStartElement("c",baseNs);
                        w.WriteAttributeString("r",label.ToString()+(row+1));
                        string header=col<=sheet.Rows[0].Length?sheet.Rows[0][col-1]:"";
                        if(row>0 && NumericReportCell(header,val??"",out var serial,out var style))
                        {
                            w.WriteAttributeString("t","n");
                            if(style is not null)w.WriteAttributeString("s",style);
                            w.WriteElementString("v",baseNs,serial);
                        }
                        else
                        {
                            w.WriteAttributeString("t","inlineStr");
                            w.WriteStartElement("is",baseNs);w.WriteStartElement("t",baseNs);
                            w.WriteString(val??"");
                            w.WriteEndElement();w.WriteEndElement();
                        }
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    completedRows++;
                    if(completedRows%50==0 || completedRows==totalRows)
                        progress?.Invoke(completedRows,totalRows);
                }
                w.WriteEndElement();w.WriteEndElement();
            });
        }
    }
}
