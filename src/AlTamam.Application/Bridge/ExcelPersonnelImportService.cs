using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using AlTamam.Contracts.Bridge;
using AlTamam.Application.Imports;
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
    // Base64 stays below the canonical 256-KiB WebView2 request envelope limit.
    private const int MaxWorkbookBytes = 160 * 1024;
    private const int MaxRecords = 500;
    private const string WorkbookOperation = "PERSONNEL-IMPORT-WORKBOOK";
    private const string PlanOperation = "PERSONNEL-IMPORT-PLAN";
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public ExcelPersonnelImportService(AlTamamDbContext db, TimeProvider? time = null)
    { _db = db; _time = time ?? TimeProvider.System; }

    private Dictionary<string,(string Id,int Type)> ReadCustomColumnHeadings()
    {
        using var c=_db.CreateOpenConnection();
        using var query=c.CreateCommand();
        query.CommandText="""
            SELECT FieldDefinitionId,ArabicCaption,FieldType FROM CustomFieldDefinitions
            WHERE IsSystemProtected=0 AND ActiveFlag=1 AND VisibleFlag=1;
            """;
        var result=new Dictionary<string,(string Id,int Type)>(StringComparer.Ordinal);
        using var rows=query.ExecuteReader();
        while(rows.Read())
            result.Add(ArabicTextNormalizer.NormalizeForSearch(rows.GetString(1)),
                (rows.GetString(0),rows.GetInt32(2)));
        return result;
    }

    private static string Read(JsonObject? payload, string key)
    {
        try { return payload?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private sealed record PersonInput(int Row, string Name, string Number, string NormalizedName, string NormalizedNumber, string SiteName, string Worksheet, string? Issue=null, JsonObject? CustomFields=null);

    public async Task<BridgeResponse> SelectAsync(JsonObject? payload, string actor, CancellationToken ct)
    {
        string base64 = Read(payload, "base64");
        string fileName = Read(payload, "fileName");
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            fileName.Length > 180 || base64.Length is < 10 or > 220_000)
            return Invalid("اختر ملف Excel بصيغة xlsx وبحجم لا يتجاوز 160 كيلوبايت.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return Invalid("محتوى ملف Excel غير صالح."); }
        try
        {
            if (bytes.Length > MaxWorkbookBytes) return Invalid("ملف Excel كبير جداً (الحد 160 كيلوبايت).");
            var rows = ParseWorkbook(bytes, payload, ReadCustomColumnHeadings());
            // Even entirely rejected worksheets must reach analysis so users can
            // inspect every validation error without changing the database.
            if (rows.Count == 0)
                return Invalid("ملف Excel لا يحتوي على صفوف أفراد قابلة للتحليل.");
            var data = new JsonArray();
            foreach (var row in rows)
                data.Add(new JsonObject
                {
                    ["row"] = row.Row, ["name"] = row.Name, ["number"] = row.Number,
                    ["normalizedName"] = row.NormalizedName, ["normalizedNumber"] = row.NormalizedNumber,
                    ["siteName"] = row.SiteName, ["worksheet"] = row.Worksheet,
                    ["issue"] = row.Issue, ["customFields"] = row.CustomFields?.DeepClone()
                });
            string sha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes));
            string token = BridgePreviewTokens.Issue(_db, _time, WorkbookOperation, actor,
                new JsonObject { ["rows"] = data, ["fileName"] = fileName,
                    ["sha256"] = sha256,
                    ["updateExistingCustomFields"] = payload?["updateExistingCustomFields"]?.ToString()=="true" }, 0, ct);
            return BridgeHelpers.Ok(new JsonObject { ["workbookToken"] = token,
                ["rowCount"] = rows.Count, ["fileName"] = fileName,
                ["maxRows"] = MaxRecords,
                ["selectedWorksheets"] = new JsonArray(rows.Select(x=>x.Worksheet)
                    .Distinct(StringComparer.Ordinal).Select(n=>(JsonNode?)JsonValue.Create(n)).ToArray()) });
        }
        catch (InvalidDataException e) { return Invalid(e.Message); }
        catch (XmlException) { return Invalid("تعذر قراءة بنية ملف Excel."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

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

    private static List<PersonInput> Rows(JsonObject staged,bool allowEmpty=false)
    {
        var items = staged["rows"] as JsonArray ?? throw new InvalidDataException("غياب بيانات المعاينة.");
        if (items.Count > MaxRecords || (!allowEmpty && items.Count==0))
            throw new InvalidDataException("عدد صفوف الاستيراد غير آمن.");
        return items.Select(n =>
        {
            var obj = n as JsonObject ?? throw new InvalidDataException("صف غير صالح.");
            return new PersonInput(obj["row"]!.GetValue<int>(),
                obj["name"]!.GetValue<string>(), obj["number"]!.GetValue<string>(),
                obj["normalizedName"]!.GetValue<string>(), obj["normalizedNumber"]!.GetValue<string>(),
                obj["siteName"]?.GetValue<string>() ?? "غير محدد",
                obj["worksheet"]?.GetValue<string>() ?? "الورقة الأولى",
                obj["issue"]?.GetValue<string>(),obj["customFields"] as JsonObject);
        }).ToList();
    }

    private static async Task<(JsonArray accepted, JsonArray issues,JsonArray updates)> InspectAsync(
        SqliteConnection connection,SqliteTransaction? tx,List<PersonInput> rows,
        CancellationToken ct,bool allowCustomUpdates=false)
    {
        var accepted=new JsonArray();
        var issues=new JsonArray();
        var updates=new JsonArray();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        using var exists=connection.CreateCommand();
        exists.Transaction=tx;
        exists.CommandText="""
            SELECT PersonId,CurrentVisibleName,NormalizedName,RowVersion,
                ActiveFlag,OrganizationalStatus
            FROM Persons WHERE NormalizedServiceNumberA=$number LIMIT 1;
            """;
        var param=exists.Parameters.Add("$number",SqliteType.Text);
        foreach(var item in rows)
        {
            ct.ThrowIfCancellationRequested();
            string? reason=item.Issue;
            if(reason is null && (item.Name.Length is <3 or >180 ||
                item.Name.Any(char.IsControl) || item.NormalizedName.Length==0 ||
                item.NormalizedName!=ArabicTextNormalizer.NormalizeForSearch(item.Name) ||
                !ServiceNumberPolicy.TryCanonicalize(item.Number,out string canonical) ||
                item.NormalizedNumber!=canonical))
                reason="الرقم (ع) يجب أن يتكون من أربعة أرقام على الأقل، وهو غير الرقم التسلسلي العادي.";
            else if(reason is null && (item.SiteName.Length is <2 or >100 ||
                item.SiteName.Any(char.IsControl)))
                reason="اسم الموقع غير صالح.";
            else if(reason is null && !seen.Add(item.NormalizedNumber))
                reason="«الرقم (ع)» مكرر داخل الملف.";
            JsonObject? candidate=null;
            if(reason is null)
            {
                param.Value=item.NormalizedNumber;
                using(var reader=await exists.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    if(await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        string personId=reader.GetString(0);
                        string originalName=reader.GetString(1);
                        string normalizedName=reader.GetString(2);
                        long revision=reader.GetInt64(3);
                        bool active=reader.GetInt32(4)==1 && reader.GetInt32(5)==1;
                        if(!active)
                            reason="«الرقم (ع)» محفوظ لهوية مؤرشفة؛ لا يمكن تعديله عبر Excel.";
                        else if(!allowCustomUpdates || item.CustomFields is null ||
                                item.CustomFields.Count==0)
                            reason="«الرقم (ع)» موجود مسبقاً في القوة؛ لن يتم استبداله.";
                        else if(normalizedName!=item.NormalizedName)
                            reason="«الرقم (ع)» موجود لكن الاسم مختلف؛ راجع الهوية قبل تحديث الأعمدة.";
                        else
                            candidate=new JsonObject {
                                ["personId"]=personId,["expectedRevision"]=revision,
                                ["name"]=originalName,["number"]=item.Number,
                                ["worksheet"]=item.Worksheet,["row"]=item.Row,
                                ["customFields"]=item.CustomFields.DeepClone()
                            };
                    }
                }
            }
            if(candidate is not null)
            {
                var changed=new JsonObject();
                var previous=new JsonObject();
                foreach(var cell in (JsonObject)candidate["customFields"]!)
                {
                    using var read=connection.CreateCommand();
                    read.Transaction=tx;
                    read.CommandText="""
                        SELECT FieldValueText FROM CustomFieldValues
                        WHERE PersonId=$person AND FieldDefinitionId=$field;
                        """;
                    read.Parameters.AddWithValue("$person",candidate["personId"]!.GetValue<string>());
                    read.Parameters.AddWithValue("$field",cell.Key);
                    string old=(await read.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string??"";
                    string next=cell.Value?.GetValue<string>()??"";
                    if(!string.Equals(old,next,StringComparison.Ordinal))
                    {
                        previous[cell.Key]=old;
                        changed[cell.Key]=next;
                    }
                }
                if(changed.Count==0)
                    reason="قيم الأعمدة الإضافية مطابقة لما هو محفوظ؛ لا يوجد تحديث.";
                else
                {
                    candidate["oldCustomFields"]=previous;
                    candidate["customFields"]=changed;
                    updates.Add(candidate);
                }
            }
            if(reason is not null)
                issues.Add(new JsonObject {
                    ["row"]=item.Row,["name"]=item.Name,
                    ["serviceNumberA"]=item.Number,["siteName"]=item.SiteName,
                    ["worksheet"]=item.Worksheet,["reason"]=reason
                });
            else if(candidate is null)
                accepted.Add(new JsonObject {
                    ["row"]=item.Row,["name"]=item.Name,
                    ["serviceNumberA"]=item.Number,["normalizedName"]=item.NormalizedName,
                    ["normalizedNumber"]=item.NormalizedNumber,["siteName"]=item.SiteName,
                    ["worksheet"]=item.Worksheet,["customFields"]=item.CustomFields?.DeepClone()
                });
        }
        return (accepted,issues,updates);
    }

    private static async Task<JsonArray> NewSitesAsync(SqliteConnection connection,
        SqliteTransaction? tx, JsonArray rows, CancellationToken ct)
    {
        var known=new HashSet<string>(StringComparer.Ordinal);
        using(var cmd=connection.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT NormalizedName FROM Sites WHERE ActiveFlag=1;";
            using var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await reader.ReadAsync(ct).ConfigureAwait(false))
                known.Add(reader.GetString(0));
        }
        var pending=new SortedDictionary<string,(string Name,int Count)>(StringComparer.Ordinal);
        foreach(var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            string name=Read((JsonObject)row!,"siteName");
            if(name=="غير محدد" || name.Length==0)continue;
            string normalized=ArabicTextNormalizer.NormalizeForSearch(name);
            if(known.Contains(normalized))continue;
            if(pending.TryGetValue(normalized,out var prior))
                pending[normalized]=(prior.Name,prior.Count+1);
            else pending[normalized]=(name,1);
        }
        var result=new JsonArray();
        foreach(var item in pending)
            result.Add(new JsonObject{["name"]=item.Value.Name,
                ["normalizedName"]=item.Key,["personCount"]=item.Value.Count});
        return result;
    }

    public async Task<BridgeResponse> AnalyzeAsync(JsonObject? payload, string actor, CancellationToken ct)
    {
        string ticket = Read(payload, "workbookToken");
        using var connection = _db.CreateOpenConnection();
        var staged = await ReadStagedAsync(ticket, WorkbookOperation, actor, connection, null, ct).ConfigureAwait(false);
        if (staged is null) return Stale();
        var (accepted, issues, updates) = await InspectAsync(connection, null, Rows(staged), ct,
            staged["updateExistingCustomFields"]?.ToString()=="true").ConfigureAwait(false);
        var newSites=await NewSitesAsync(connection,null,accepted,ct).ConfigureAwait(false);
        var similarNameMatches=await ImportNameCollisionHints.QueryAsync(connection,accepted,ct).ConfigureAwait(false);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["newSites"]=newSites,["similarNames"]=similarNameMatches.Items,["similarNameCount"]=similarNameMatches.Total,
            ["totalRows"] = accepted.Count + updates.Count + issues.Count,
            ["newCount"] = accepted.Count,["customUpdateCount"]=updates.Count,
            ["customUpdatesPreview"]=PreviewCustomUpdates(updates),
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
        var (accepted, issues, updates) = await InspectAsync(connection, null, Rows(staged), ct,
            staged["updateExistingCustomFields"]?.ToString()=="true").ConfigureAwait(false);
        if (accepted.Count == 0 && issues.Count == 0 && updates.Count == 0)
            return BridgeHelpers.Error(BridgeErrorCodes.InvalidWorkbook, "لا توجد بيانات تحليل قابلة للحفظ.");
        var newSites=await NewSitesAsync(connection,null,accepted,ct).ConfigureAwait(false);
        var similarNameMatches=await ImportNameCollisionHints.QueryAsync(connection,accepted,ct).ConfigureAwait(false);
        // Only approved, conflict-free new rows are carried forward to the signed, one-use plan.
        string token = BridgePreviewTokens.Issue(_db, _time, PlanOperation, actor,
            new JsonObject { ["fileName"] = Read(staged, "fileName"),
                ["sha256"] = Read(staged, "sha256"),
                ["newSites"] = newSites.DeepClone(),
                ["totalRows"] = accepted.Count + updates.Count + issues.Count,
                ["issues"] = issues.DeepClone(),
                ["updateRows"] = updates.DeepClone(),
                ["rows"] = new JsonArray(accepted.Select(node =>
            {
                var p = (JsonObject)node!;
                return (JsonNode?)new JsonObject {
                    ["row"] = p["row"]!.GetValue<int>(), ["name"] = p["name"]!.GetValue<string>(),
                    ["number"] = p["serviceNumberA"]!.GetValue<string>(),
                    ["normalizedName"] = p["normalizedName"]!.GetValue<string>(),
                    ["normalizedNumber"] = p["normalizedNumber"]!.GetValue<string>(),
                    ["siteName"] = p["siteName"]!.GetValue<string>(),
                    ["worksheet"] = p["worksheet"]?.GetValue<string>() ?? "الورقة الأولى",
                    ["customFields"] = p["customFields"]?.DeepClone()
                };
            }).ToArray()) }, accepted.Count, ct);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["previewToken"] = token, ["newSites"] = newSites.DeepClone(),
            ["similarNames"] = similarNameMatches.Items,
            ["similarNameCount"] = similarNameMatches.Total,
            ["newCount"] = accepted.Count,
            ["customUpdateCount"] = updates.Count,
            ["customUpdatesPreview"] = PreviewCustomUpdates(updates),
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
            var (replay, receipt) = await MutationIdempotency.StartAsync(
                connection, tx, "CommitImport", actor, payload, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
            var plan = await ReadStagedAsync(token, PlanOperation, actor, connection, tx, ct).ConfigureAwait(false);
            if (plan is null) return Stale();
            var rows = Rows(plan,allowEmpty:true);
            var updateRows=plan["updateRows"] as JsonArray ?? new JsonArray();
            if(updateRows.Count>0 && payload?["approveCustomUpdates"]?.ToString()!="true")
                return Invalid("تحديث أعمدة الأفراد الموجودين يتطلب موافقة مستقلة بعد مقارنة القيم.");
            // All compare-and-swap checks run before ANY new person is inserted.
            // No direct workbook may overwrite another person's identity/site/attendance.
            foreach(var item in updateRows)
            {
                if(item is not JsonObject update)return Stale();
                string id=Read(update,"personId");
                long rev=update["expectedRevision"]?.GetValue<long>()??0;
                using(var current=connection.CreateCommand())
                {
                    current.Transaction=tx;
                    current.CommandText="""
                        SELECT RowVersion FROM Persons
                        WHERE PersonId=$id AND ActiveFlag=1 AND OrganizationalStatus=1;
                        """;
                    current.Parameters.AddWithValue("$id",id);
                    object? value=await current.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if(value is null || Convert.ToInt64(value)!=rev)return Stale();
                }
                var changes=update["customFields"] as JsonObject;
                var oldValues=update["oldCustomFields"] as JsonObject;
                if(changes is null || changes.Count==0 || oldValues is null ||
                   oldValues.Count!=changes.Count)return Stale();
                foreach(var cell in oldValues)
                {
                    using var before=connection.CreateCommand();
                    before.Transaction=tx;
                    before.CommandText="""
                        SELECT FieldValueText FROM CustomFieldValues
                        WHERE PersonId=$person AND FieldDefinitionId=$field;
                        """;
                    before.Parameters.AddWithValue("$person",id);
                    before.Parameters.AddWithValue("$field",cell.Key);
                    string actual=(await before.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string??"";
                    if(actual!=(cell.Value?.GetValue<string>()??""))return Stale();
                }
            }
            var (accepted, issues, _) = await InspectAsync(connection, tx, rows, ct).ConfigureAwait(false);
            if (issues.Count > 0 || accepted.Count != rows.Count)
                return Stale();
            var proposedSites=plan["newSites"] as JsonArray ?? new JsonArray();
            var missingSites=await NewSitesAsync(connection,tx,accepted,ct).ConfigureAwait(false);
            if(proposedSites.ToJsonString()!=missingSites.ToJsonString())return Stale();
            if(proposedSites.Count>0)
            {
                bool approved=false;
                try{approved=payload?["confirmNewSites"]?.GetValue<bool>()==true;}
                catch(InvalidOperationException){}
                var names=payload?["approvedSiteNames"] as JsonArray;
                if(!approved || names is null || names.Count!=proposedSites.Count ||
                   !names.Select(x=>x?.ToString()??"").SequenceEqual(
                       proposedSites.Select(x=>Read((JsonObject)x!,"name")),StringComparer.Ordinal))
                    return Invalid("إنشاء المواقع يتطلب معاينة منفصلة وتأكيد أسمائها وعدد أفرادها.");
            }
            long? consumed = await BridgePreviewTokens.ConsumeAsync(_db, _time, connection,
                tx, token, PlanOperation, actor, rows.Count, ct).ConfigureAwait(false);
            if (consumed != rows.Count) return Stale();

            string now = _time.GetUtcNow().ToString("O");
            string batchId = "IMP-" + Guid.NewGuid().ToString("N");
            var plannedIssues = plan["issues"] as JsonArray ?? new JsonArray();
            using (var record = connection.CreateCommand())
            {
                record.Transaction = tx;
                record.CommandText = """
                    INSERT INTO ImportBatches(ImportBatchId,SourceFileName,SourceFileSha256,
                        TotalRows,AcceptedRows,RejectedRows,CreatedSitesCount,Status,
                        CreatedByUserId,CreatedAtUtc,CommittedAtUtc)
                    VALUES($id,$file,$hash,$total,$accepted,$rejected,0,'COMMITTED',$actor,$now,$now);
                    """;
                record.Parameters.AddWithValue("$id", batchId);
                record.Parameters.AddWithValue("$file", Read(plan, "fileName"));
                record.Parameters.AddWithValue("$hash", Read(plan, "sha256"));
                record.Parameters.AddWithValue("$total", rows.Count + updateRows.Count + plannedIssues.Count);
                record.Parameters.AddWithValue("$accepted", rows.Count + updateRows.Count);
                record.Parameters.AddWithValue("$rejected", plannedIssues.Count);
                record.Parameters.AddWithValue("$actor", actor);
                record.Parameters.AddWithValue("$now", now);
                await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (var issue in plannedIssues)
            {
                var rejected = issue as JsonObject ??
                    throw new InvalidDataException("تعذر استرجاع أسباب الاستبعاد المعتمدة.");
                using var record = connection.CreateCommand();
                record.Transaction = tx;
                record.CommandText = """
                    INSERT INTO ImportIssues(ImportBatchId,WorksheetName,RowNumber,
                        RawServiceNumberA,RawPersonName,RawSiteName,IssueReasonArabic)
                    VALUES($batch,$worksheet,$row,$number,$name,$site,$reason);
                    """;
                record.Parameters.AddWithValue("$batch", batchId);
                record.Parameters.AddWithValue("$worksheet", rejected["worksheet"]?.GetValue<string>() ?? "الورقة الأولى");
                record.Parameters.AddWithValue("$row", rejected["row"]!.GetValue<int>());
                record.Parameters.AddWithValue("$number", rejected["serviceNumberA"]?.GetValue<string>() ?? "");
                record.Parameters.AddWithValue("$name", rejected["name"]?.GetValue<string>() ?? "");
                record.Parameters.AddWithValue("$site", rejected["siteName"]?.GetValue<string>() ?? "غير محدد");
                record.Parameters.AddWithValue("$reason", rejected["reason"]!.GetValue<string>());
                await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            string today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var siteIds = new Dictionary<string, string>(StringComparer.Ordinal);
            int createdSites = 0;
            var createdIds = new List<string>(rows.Count);
            foreach (var item in rows)
            {
                ct.ThrowIfCancellationRequested();
                string personId = "PRN-" + Guid.NewGuid().ToString("N");
                string siteId = "SITE-UNSPECIFIED";
                if (item.SiteName != "غير محدد")
                {
                    string normalizedSite = ArabicTextNormalizer.NormalizeForSearch(item.SiteName);
                    if (!siteIds.TryGetValue(normalizedSite, out var savedSiteId))
                    {
                        using var findSite = connection.CreateCommand();
                        findSite.Transaction = tx;
                        findSite.CommandText = "SELECT SiteId FROM Sites WHERE NormalizedName=$name AND ActiveFlag=1 LIMIT 1;";
                        findSite.Parameters.AddWithValue("$name", normalizedSite);
                        siteId = await findSite.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? "";
                        if (siteId.Length == 0)
                        {
                            siteId = "SITE-" + Guid.NewGuid().ToString("N");
                            using var newSite = connection.CreateCommand();
                            newSite.Transaction = tx;
                            newSite.CommandText = """
                                INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
                                VALUES($id,$name,$norm,0,1,$now,1);
                                INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
                                VALUES($id,$name,$day,NULL,$actor,$now);
                                """;
                            newSite.Parameters.AddWithValue("$id", siteId);
                            newSite.Parameters.AddWithValue("$name", item.SiteName);
                            newSite.Parameters.AddWithValue("$norm", normalizedSite);
                            newSite.Parameters.AddWithValue("$day", today);
                            newSite.Parameters.AddWithValue("$actor", actor);
                            newSite.Parameters.AddWithValue("$now", now);
                            await newSite.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                            createdSites++;
                            await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                                "SITE_CREATE", "إنشاء موقع أثناء الاستيراد", actor, username, username,
                                "Site", siteId, item.SiteName, null,
                                new JsonObject { ["siteId"] = siteId, ["name"] = item.SiteName }.ToJsonString(),
                                "إنشاء موقع بعد معاينة منفصلة وموافقة المستخدم أثناء الاستيراد", null, today,
                                ct.CorrelationId(), ct).ConfigureAwait(false);
                        }
                        siteIds[normalizedSite] = siteId;
                    }
                    else siteId = savedSiteId;
                }
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
                    VALUES($assignment,$id,$site,$today,NULL,NULL,$now);
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
                insert.Parameters.AddWithValue("$site", siteId);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if(item.CustomFields is not null)
                {
                    foreach(var fieldValue in item.CustomFields)
                    {
                        using var col=connection.CreateCommand();
                        col.Transaction=tx;
                        col.CommandText="""
                            INSERT INTO CustomFieldValues
                                (PersonId,FieldDefinitionId,FieldValueText,UpdatedAtUtc)
                            VALUES($person,$field,$value,$at);
                            """;
                        col.Parameters.AddWithValue("$person",personId);
                        col.Parameters.AddWithValue("$field",fieldValue.Key);
                        col.Parameters.AddWithValue("$value",fieldValue.Value?.GetValue<string>()??"");
                        col.Parameters.AddWithValue("$at",now);
                        await col.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
                createdIds.Add(personId);
            }
            foreach(var item in updateRows)
            {
                var update=(JsonObject)item!;
                var changes=(JsonObject)update["customFields"]!;
                var applied=await RosterCustomFieldValueService.SaveInTransactionAsync(
                    connection,tx,actor,username,Read(update,"personId"),
                    update["expectedRevision"]!.GetValue<long>(),changes,
                    "تحديث أعمدة فرد موجود بعد معاينة واعتماد استيراد Excel",ct,_time)
                    .ConfigureAwait(false);
                if(!applied.Success)
                    throw new InvalidOperationException(
                        "تغيرت قيم أحد الأفراد أثناء الاستيراد؛ تراجعت الدفعة كاملة: "+applied.UserMessage);
                createdIds.Add(Read(update,"personId"));
            }
            using (var updateBatch = connection.CreateCommand())
            {
                updateBatch.Transaction = tx;
                updateBatch.CommandText = "UPDATE ImportBatches SET CreatedSitesCount=$count WHERE ImportBatchId=$id;";
                updateBatch.Parameters.AddWithValue("$count", createdSites);
                updateBatch.Parameters.AddWithValue("$id", batchId);
                await updateBatch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                "PERSONNEL_IMPORT", "استيراد أفراد من ملف Excel",
                actor, username, username, "PersonImport", batchId, "دفعة استيراد",
                null, new JsonObject { ["importBatchId"] = batchId,
                    ["createdCount"] = rows.Count,["customUpdatedCount"]=updateRows.Count,
                    ["rejectedCount"] = plannedIssues.Count,
                    ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)JsonValue.Create(r.Number)).ToArray()) }.ToJsonString(),
                $"إنشاء {rows.Count} فرد جديد وتحديث أعمدة {updateRows.Count} فرد موجود دون تعديل الأسماء أو المواقع أو التحضير.",
                "اعتماد كشف Excel بعد المعاينة", today, ct.CorrelationId(), ct).ConfigureAwait(false);
            var outcome = new JsonObject { ["createdCount"] = rows.Count,
                ["customUpdatedCount"] = updateRows.Count,
                ["committedAtUtc"] = now, ["importToken"] = token,
                ["importBatchId"] = batchId, ["rejectedCount"] = plannedIssues.Count,
                ["createdSitesCount"] = createdSites };
            await MutationIdempotency.StoreAsync(connection, tx, receipt, outcome, _time, ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome,
                events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonIdentityUpdated,
                    today, createdIds));
        }, ct).ConfigureAwait(false);
    }

    private static JsonArray PreviewCustomUpdates(JsonArray updates)
    {
        var rows=new JsonArray();
        foreach(var update in updates.OfType<JsonObject>())
        {
            var previous=(JsonObject)update["oldCustomFields"]!;
            var changed=(JsonObject)update["customFields"]!;
            foreach(var item in changed)
                rows.Add(new JsonObject {
                    ["row"]=update["row"]?.GetValue<int>()??0,
                    ["name"]=Read(update,"name"),
                    ["serviceNumberA"]=Read(update,"number"),
                    ["columnId"]=item.Key,
                    ["oldValue"]=previous[item.Key]?.GetValue<string>()??"",
                    ["newValue"]=item.Value?.GetValue<string>()??""
                });
        }
        return rows;
    }

    private static JsonArray PreviewNames(JsonArray accepted)
    {
        var preview = new JsonArray();
        foreach (var row in accepted.Take(100))
        {
            var x = (JsonObject)row!;
            preview.Add(new JsonObject {
                ["worksheet"] = x["worksheet"]?.GetValue<string>() ?? "الورقة الأولى",
                ["row"] = x["row"]!.GetValue<int>(),
                ["name"] = x["name"]!.GetValue<string>(),
                ["serviceNumberA"] = x["serviceNumberA"]!.GetValue<string>(),
                ["siteName"] = x["siteName"]!.GetValue<string>()
            });
        }
        return preview;
    }

    private static BridgeResponse Stale() => BridgeHelpers.Error(
        BridgeErrorCodes.ImportPlanStale, "انتهت صلاحية المعاينة أو تغيرت البيانات؛ أعد استيراد الملف.");
    private static BridgeResponse Invalid(string text) => BridgeHelpers.Error(BridgeErrorCodes.InvalidWorkbook, text);

    private static List<PersonInput> ParseWorkbook(byte[] bytes, JsonObject? options,
        IReadOnlyDictionary<string,(string Id,int Type)> customColumns)
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
            // Match selected worksheet index to the workbook's declared order,
            // not an assumed sheet1.xml/sheet2.xml physical filename sequence.
            var sheets=ExcelWorksheetResolver.Resolve(zip,SafeXml);
            var picked=options?["sheetIndexes"] as JsonArray;
            var indices=new List<int>();
            bool allSheets=false;
            try{allSheets=options?["allSheets"]?.GetValue<bool>()==true;}
            catch(InvalidOperationException){}
            if(allSheets)
            {
                if(picked is not null)throw new InvalidDataException("اختر الكل أو أوراقاً محددة فقط.");
                for(int i=1;i<=sheets.Count;i++)indices.Add(i);
            }
            else if(picked is null)indices.Add(1);
            else
            {
                if(picked.Count is <1 or >50)
                    throw new InvalidDataException("اختر ورقة Excel واحدة على الأقل.");
                foreach(var node in picked)
                {
                    int n;
                    try{n=node!.GetValue<int>();}
                    catch{throw new InvalidDataException("رقم ورقة Excel غير صحيح.");}
                    if(n<1 || n>sheets.Count || indices.Contains(n))
                        throw new InvalidDataException("رقم ورقة Excel خارج النطاق أو مكرر.");
                    indices.Add(n);
                }
            }
            string nameHeading=Read(options,"nameHeading");
            string numberHeading=Read(options,"numberHeading");
            string siteHeading=Read(options,"siteHeading");
            if(new[]{nameHeading,numberHeading,siteHeading}
                .Any(x=>x.Length>100 || x.Any(char.IsControl)))
                throw new InvalidDataException("عنوان عمود Excel مخصص غير صالح.");
            string[] strings = [];
            var shared = zip.GetEntry("xl/sharedStrings.xml");
            if (shared is not null)
            {
                XDocument list = SafeXml(shared);
                strings = list.Descendants(Ns + "si")
                    .Select(si => string.Concat(si.Descendants(Ns + "t").Select(n => n.Value))).ToArray();
                if (strings.Length > 50_000) throw new InvalidDataException("عدد النصوص داخل Excel كبير جداً.");
            }
            var records = new List<PersonInput>();
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
            foreach(int sheetIndex in indices)
            {
            XDocument document=SafeXml(sheets[sheetIndex-1].Entry);
            var rows=document.Descendants(Ns+"sheetData").Elements(Ns+"row").ToList();
            if(rows.Count is <2 or >MaxRecords+6)
                throw new InvalidDataException("ورقة Excel لا تحتوي على صفوف وأعمدة صالحة.");
            string worksheet=sheets[sheetIndex-1].Name;
            // Some official monthly workbooks put a report title and period above the header.
            // Locate the first valid header within five rows, never interpret title rows as persons.
            int headerIndex = -1, nameCol = 0, numberCol = 0, siteCol = 0;
            for (int index = 0; index < Math.Min(rows.Count, 5); index++)
            {
                var headers = rows[index].Elements(Ns + "c")
                    .Select(cell => (column: Col(cell), value: Value(cell))).ToList();
                int name = headers.Where(x => x.value is "الاسم" or "الاسم الكامل" or "اسم الفرد" || (nameHeading.Length>0 && x.value==nameHeading))
                    .Select(x => x.column).FirstOrDefault();
                int number = headers.Where(x => x.value is "الرقم (ع)" or "الرقم ع" or "الرقم(ع)" || (numberHeading.Length>0 && x.value==numberHeading))
                    .Select(x => x.column).FirstOrDefault();
                if (name > 0 && number > 0 && name != number)
                {
                    headerIndex = index; nameCol = name; numberCol = number;
                    siteCol = headers.Where(x => x.value is "الموقع" or "اسم الموقع" or "موقع الفرد" || (siteHeading.Length>0 && x.value==siteHeading))
                        .Select(x => x.column).FirstOrDefault();
                    break;
                }
            }
            if (headerIndex < 0)
                throw new InvalidDataException("تحتاج ورقة Excel إلى عمودي «الاسم» و«الرقم (ع)» ضمن الصفوف الخمسة الأولى.");
            // ColumnId is captured in the signed staging payload; mutable captions
            // are used only to discover the corresponding spreadsheet column.
            var extras=new List<(int Column,string Id,int Type)>();
            var extraIds=new HashSet<string>(StringComparer.Ordinal);
            foreach(var header in rows[headerIndex].Elements(Ns+"c"))
            {
                int column=Col(header);
                if(column==nameCol || column==numberCol || column==siteCol)continue;
                string heading=ArabicTextNormalizer.NormalizeForSearch(Value(header));
                if(customColumns.TryGetValue(heading,out var field))
                {
                    if(!extraIds.Add(field.Id))
                        throw new InvalidDataException("عنوان عمود إضافي مكرر في Excel.");
                    extras.Add((column,field.Id,field.Type));
                }
            }
            foreach (var row in rows.Skip(headerIndex + 1))
            {
                int rowIndex = int.TryParse((string?)row.Attribute("r"), out int n) ? n : records.Count + 2;
                try
                {
                var cells = row.Elements(Ns + "c").ToDictionary(Col);
                cells.TryGetValue(nameCol, out var nameCell);
                cells.TryGetValue(numberCol, out var numberCell);
                string name = nameCell is null ? "" : Value(nameCell);
                string number = numberCell is null ? "" : ServiceNumber(numberCell, Value(numberCell), rowIndex);
                cells.TryGetValue(siteCol, out var siteCell);
                string siteName = siteCell is null ? "غير محدد" : Value(siteCell);
                if (string.IsNullOrWhiteSpace(siteName)) siteName = "غير محدد";
                if (name.Length == 0 && number.Length == 0) continue;
                bool isValidNumber=ServiceNumberPolicy.TryCanonicalize(number,out string canonical);
                var customValues=new JsonObject();
                string? cellIssue=null;
                foreach(var extra in extras)
                {
                    cells.TryGetValue(extra.Column,out var rawCell);
                    if(rawCell is null)continue;
                    if(rawCell.Element(Ns+"f") is not null)
                    {
                        cellIssue="صيغة Excel الحسابية ممنوعة في الأعمدة الإضافية.";
                        break;
                    }
                    string rawCustom=Value(rawCell);
                    if(rawCustom.Length==0)continue; // blank Excel cells never erase saved values
                    if(!RosterCustomFieldValueService.TryCanonicalize(
                        rawCustom,extra.Type,out string normalized))
                    {
                        cellIssue="قيمة العمود الإضافي لا تطابق نوعه أو تجاوزت الحد الآمن.";
                        break;
                    }
                    customValues[extra.Id]=normalized;
                }
                records.Add(new PersonInput(rowIndex, name, isValidNumber ? canonical : number,
                    ArabicTextNormalizer.NormalizeForSearch(name),
                    isValidNumber ? canonical : ArabicTextNormalizer.NormalizeForSearch(number),
                    siteName,worksheet,cellIssue,customValues));
                }
                catch(Exception ex) when(ex is InvalidDataException or ArgumentException or OverflowException or FormatException or InvalidOperationException)
                {
                    records.Add(new PersonInput(rowIndex,"","","","","غير محدد",worksheet,
                        "صف Excel غير صالح: "+(ex is InvalidDataException ? ex.Message : "بيانات خلية أو تنسيق غير صالح.")));
                }
                if (records.Count > MaxRecords) throw new InvalidDataException("تجاوز عدد الأفراد الحد الآمن.");
            }
            }
            return records;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException or InvalidOperationException or XmlException)
        { throw new InvalidDataException("ملف Excel تالف أو لا يطابق نموذج الكشف المعتمد.", ex); }
    }

    /// <summary>
    /// Excel supports genuine numeric cells for الرقم (ع). Convert integer-valued numbers
    /// using invariant culture; keep the database identity as an exact string because
    /// it is an identifier, not a quantity. Reject fractions, formulae and values beyond
    /// Excel's 15-digit numeric precision rather than silently corrupting identities.
    /// Existing text cells remain supported for historic cards with leading zeroes.
    /// </summary>
    private static string ServiceNumber(XElement cell, string raw, int rowIndex)
    {
        if (cell.Element(Ns + "f") is not null)
            return "#INVALID-FORMULA"; // only this row is rejected during InspectAsync
        string kind = (string?)cell.Attribute("t") ?? "";
        if (kind is "inlineStr" or "s" or "str")
            return raw; // preserve legacy leading zeroes from genuine text cells
        if (kind is not ("" or "n"))
            return "#INVALID-TYPE";
        if (raw.Length == 0) return ""; // mark incomplete row as an import issue
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                out decimal value) ||
            value < 0m || value > 999_999_999_999_999m ||
            decimal.Truncate(value) != value)
            return "#INVALID-NUMBER";
        return value.ToString("0", CultureInfo.InvariantCulture);
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
