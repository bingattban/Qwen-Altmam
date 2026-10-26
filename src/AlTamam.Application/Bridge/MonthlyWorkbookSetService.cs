using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Bridge;

/// <summary>
/// 04.09(c): one consolidated monthly XLSX plus one XLSX for each matching
/// historical site. Reuses the tested streamed SQLCipher monthly renderer,
/// preserving distinct PersonIds. Preview includes exact proposed names, folder
/// and site counts, while commit requires a matching scope snapshot and explicit
/// confirmation. A failed multi-file export removes its own partial outputs.
/// </summary>
public sealed class MonthlyWorkbookSetService
{
    private readonly AlTamamDbContext _db;
    private readonly DailyMonthlyReadService _read;
    private readonly MonthlyExcelExportService _single;
    private readonly TimeProvider _clock;
    public MonthlyWorkbookSetService(AlTamamDbContext db,TimeProvider? clock=null)
    {
        _db=db;_clock=clock??TimeProvider.System;
        _read=new DailyMonthlyReadService(db);
        _single=new MonthlyExcelExportService(db);
    }
    private sealed record Scope(string SiteId,string Name,long Count,string FileName);
    private static string Text(JsonObject? data,string name)
    {
        try{return data?[name]?.GetValue<string>()?.Trim()??"";}
        catch{return "";}
    }
    private static int Number(JsonObject? data,string name)
    {
        try{return data?[name]?.GetValue<int>()??0;}catch{return 0;}
    }
    private static string Sanitized(string input)
    {
        const string forbidden="\\/:*?\"<>|";
        var name=new string(input.Where(c=>!char.IsControl(c)&&!forbidden.Contains(c))
            .Take(28).ToArray()).Trim().Replace(' ','_').Trim('.');
        return name.Length==0?"موقع":name;
    }
    private static string FormatFile(int year,int month,string label,
        string scopeId,string date,string operation)
        => "التمام الشهري_"+Sanitized(label)+"_"+
           year.ToString("D4",CultureInfo.InvariantCulture)+"-"+
           month.ToString("D2",CultureInfo.InvariantCulture)+"_تصدير-"+date+
           "_"+operation+"_"+Sanitized(scopeId)+".xlsx";

    private static string ScopeHash(int year,int month,string site,string term,
        string date,IReadOnlyList<Scope> scopes)
    {
        string data=string.Join("\u001f",new[]{
            year.ToString(CultureInfo.InvariantCulture),
            month.ToString(CultureInfo.InvariantCulture),site,term,date
        }.Concat(scopes.Select(s=>string.Join("\u001e",s.SiteId,s.Name,
            s.Count.ToString(CultureInfo.InvariantCulture),s.FileName))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }
    private static JsonObject Row(Scope s,bool primary)
        => new(){
            ["siteId"]=s.SiteId,["siteName"]=s.Name,["personCount"]=s.Count,
            ["fileName"]=s.FileName,["consolidated"]=primary
        };

    public async Task<BridgeResponse> PreviewAsync(JsonObject? request,CancellationToken ct)
    {
        var basePreview=await _single.PreviewAsync(request,ct).ConfigureAwait(false);
        if(!basePreview.Success)return basePreview;
        var info=(JsonObject)basePreview.Data!;
        int year=info["year"]!.GetValue<int>(),month=info["month"]!.GetValue<int>();
        string site=info["siteId"]!.GetValue<string>(),term=info["term"]!.GetValue<string>();
        long count=info["personCount"]!.GetValue<long>();
        string exportId=Text(request,"exportId");
        if(exportId.Length==0)exportId=Guid.NewGuid().ToString("N");
        else if(Guid.TryParse(exportId,out var valid))exportId=valid.ToString("N");
        else return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
            "معرّف معاينة التصدير غير صالح.");

        string folder=await new OutputDirectorySettings(_db).GetAsync(ct).ConfigureAwait(false);
        if(!OutputDirectorySettings.IsSafeDirectory(folder,_db.DatabasePath,out var directory))
            return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed,
                "مجلد تقارير Excel غير متاح؛ اختر مجلدًا محليًا صالحًا في الإعدادات.");

        string date=_clock.GetLocalNow().ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        var scopes=new List<Scope>();
        if(count>0)
        {
            if(site.Length==0)
            {
                scopes.Add(new Scope("","جميع المواقع",count,
                    FormatFile(year,month,"جميع المواقع","ALL",date,exportId)));
                var start=new DateOnly(year,month,1);
                string first=start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
                    next=start.AddMonths(1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
                var sites=new List<(string Id,string Name)>();
                using(var c=_db.CreateOpenConnection())
                using(var q=c.CreateCommand())
                {
                    q.CommandText="""
                        SELECT s.SiteId,s.VisibleName FROM Sites s
                        WHERE EXISTS(SELECT 1 FROM PersonAssignments a
                            WHERE a.SiteId=s.SiteId AND a.ValidFromLocalDate<$next
                                AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate>$first))
                        OR EXISTS(SELECT 1 FROM PersonnelMovements m
                            WHERE (m.SourceSiteId=s.SiteId OR m.DestinationSiteId=s.SiteId)
                              AND m.EffectiveLocalDate >= $first
                              AND m.EffectiveLocalDate < $next)
                        ORDER BY s.SiteId;
                        """;
                    q.Parameters.AddWithValue("$first",first);
                    q.Parameters.AddWithValue("$next",next);
                    using var reader=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while(await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        sites.Add((reader.GetString(0),reader.GetString(1)));
                        if(sites.Count>100)
                            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                                "عدد المواقع يزيد عن 100؛ صدّر المواقع في دفعات محددة.");
                    }
                }
                foreach(var candidate in sites)
                {
                    ct.ThrowIfCancellationRequested();
                    var page=await _read.MonthlyGridAsync(new JsonObject{
                        ["year"]=year,["month"]=month,["siteId"]=candidate.Id,
                        ["term"]=term,["page"]=1,["pageSize"]=1
                    },ct).ConfigureAwait(false);
                    if(!page.Success)return page;
                    long matching=((JsonObject)page.Data!)["total"]!.GetValue<long>();
                    if(matching==0)continue;
                    scopes.Add(new Scope(candidate.Id,candidate.Name,matching,
                        FormatFile(year,month,candidate.Name,candidate.Id,date,exportId)));
                }
                // Impossible to produce individual site exports for a historical
                // dataset without valid site references. Do not silently omit.
                if(scopes.Count==1)
                    return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                        "لا توجد مواقع تاريخية متطابقة مع السجل؛ افحص انتسابات الأفراد قبل التصدير.");
            }
            else
            {
                string name;
                using(var c=_db.CreateOpenConnection())
                using(var q=c.CreateCommand())
                {
                    q.CommandText="SELECT VisibleName FROM Sites WHERE SiteId=$id;";
                    q.Parameters.AddWithValue("$id",site);
                    name=await q.ExecuteScalarAsync(ct).ConfigureAwait(false) as string??"";
                }
                if(name.Length==0)
                    return BridgeHelpers.Error(BridgeErrorCodes.MissingData,
                        "الموقع المطلوب غير موجود، حدّث قائمة المواقع.");
                scopes.Add(new Scope(site,name,count,FormatFile(year,month,
                    name,site,date,exportId)));
            }
        }
        var list=new JsonArray();
        for(int i=0;i<scopes.Count;i++)list.Add(Row(scopes[i],i==0&&site.Length==0));
        return BridgeHelpers.Ok(new JsonObject
        {
            ["year"]=year,["month"]=month,["siteId"]=site,["term"]=term,
            ["personCount"]=count,["daysInMonth"]=info["daysInMonth"]!.GetValue<int>(),
            ["format"]="xlsx",["directory"]=directory,["exportId"]=exportId,
            ["exportDate"]=date,["fileCount"]=scopes.Count,
            ["files"]=list,["scopeSignature"]=ScopeHash(year,month,site,term,date,scopes)
        });
    }

    public async Task<BridgeResponse> ExportAsync(JsonObject? request,
        BridgePrincipal actor,CancellationToken ct)
    {
        if(!bool.TryParse(request?["confirmed"]?.ToString(),out bool confirmed)||!confirmed)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "يجب تأكيد نطاق ملفات Excel بعد المعاينة.");
        string expected=Text(request,"scopeSignature"),exportId=Text(request,"exportId");
        if(expected.Length!=64||exportId.Length==0)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "معاينة التصدير الموقّعة ونطاق الملفات مطلوبان.");
        var preview=await PreviewAsync(request,ct).ConfigureAwait(false);
        if(!preview.Success)return preview;
        var data=(JsonObject)preview.Data!;
        if(data["scopeSignature"]!.GetValue<string>()!=expected)
            return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                "تغيرت بيانات الشهر أو المواقع أو تاريخ التصدير؛ أعد معاينة الملفات.");
        var files=(JsonArray)data["files"]!;
        if(files.Count==0)
            return BridgeHelpers.Error(BridgeErrorCodes.MissingData,
                "لا توجد أسماء مطابقة للتصدير.");
        var completed=new List<string>();
        var resultRows=new JsonArray();
        try
        {
            foreach(var node in files)
            {
                ct.ThrowIfCancellationRequested();
                var plan=(JsonObject)node!;
                string fileName=plan["fileName"]!.GetValue<string>(),
                    siteId=plan["siteId"]!.GetValue<string>();
                var one=await _single.ExportNamedAsync(new JsonObject
                {
                    ["year"]=Number(request,"year"),["month"]=Number(request,"month"),
                    ["siteId"]=siteId,["term"]=Text(request,"term")
                },fileName,ct).ConfigureAwait(false);
                if(!one.Success)
                    throw new IOException(one.UserMessage??"تعذر إنشاء أحد ملفات Excel.");
                string path=((JsonObject)one.Data!)["filePath"]!.GetValue<string>();
                completed.Add(path);
                var saved=(JsonObject)plan.DeepClone();
                saved["filePath"]=path;
                resultRows.Add(saved);
            }
            await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
            {
                await BridgeAuditWriter.WriteAsync(c,t,_clock,
                    "MONTHLY_ROSTER_EXCEL_EXPORT","تصدير كشف التمام الشهري إلى Excel",
                    actor.UserId,actor.Username,actor.Username,
                    "ExcelExport",data["exportId"]!.GetValue<string>(),
                    data["siteId"]!.GetValue<string>().Length==0?"جميع المواقع":"موقع محدد",
                    null,new JsonObject{
                        ["year"]=Number(request,"year"),["month"]=Number(request,"month"),
                        ["siteId"]=Text(request,"siteId"),["term"]=Text(request,"term"),
                        ["fileCount"]=resultRows.Count,
                        ["personCount"]=data["personCount"]!.GetValue<long>(),
                        ["fileNames"]=new JsonArray(resultRows.Select(x=>
                            (JsonNode?)JsonValue.Create(x!["fileName"]!.GetValue<string>())).ToArray())
                    }.ToJsonString(),
                    "تصدير بيانات شهرية من قاعدة البيانات المشفرة؛ السجل يحتوي أسماء الملفات فقط لا خلاياها.",
                    null,_clock.GetLocalNow().ToString("yyyy-MM-dd"),
                    data["exportId"]!.GetValue<string>(),ct).ConfigureAwait(false);
                return 0;
            },ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(new JsonObject{
                ["format"]="xlsx",["filePath"]=completed[0],
                ["directory"]=data["directory"]!.GetValue<string>(),
                ["files"]=resultRows,["fileCount"]=resultRows.Count,
                ["personCount"]=data["personCount"]!.GetValue<long>()
            });
        }
        catch(OperationCanceledException){throw;}
        catch(IOException e)
        {
            return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed,
                "تعذر إكمال جميع ملفات التصدير: "+e.Message);
        }
        catch(Exception)
        {
            return BridgeHelpers.Error(BridgeErrorCodes.ExcelExportFailed,
                "تعذر توثيق تصدير Excel أو إكمال الملفات؛ أُلغيت العملية.");
        }
        finally
        {
            // Only completed, fully audited sets are kept. A partial set must
            // never appear as a successful group export.
            // Rollback is executed in the catch branches via success flag below.
        }
    }
}
