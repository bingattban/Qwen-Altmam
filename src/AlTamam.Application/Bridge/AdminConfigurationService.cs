using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Policies;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Real configuration writes with immutable IDs, effective-date name history,
/// optimistic versions, and append-only audit in the same SQLCipher transaction.
/// </summary>
public sealed class AdminConfigurationService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    public AdminConfigurationService(AlTamamDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    private static string Read(JsonObject? p, string key)
    {
        try { return p?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }
    private static long ReadVersion(JsonObject? p)
    {
        try { return p?["expectedRevision"]?.GetValue<long>() ?? 0; }
        catch
        {
            try { return p?["expectedRevision"]?.GetValue<int>() ?? 0; }
            catch { return 0; }
        }
    }
    private static bool ColorValid(string color) => Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$");
    private string LocalDate() => _time.GetLocalNow().ToString("yyyy-MM-dd");
    private string Now() => _time.GetUtcNow().ToString("O");
    private static BridgeResponse Fail(string message, string code = BridgeErrorCodes.ValidationError)
        => BridgeHelpers.Error(code, message);

    private async Task Audit(SqliteConnection c, SqliteTransaction t, BridgePrincipal actor,
        string action, string title, string type, string targetId,
        string targetName, string? previous, string current, CancellationToken ct)
    {
        await BridgeAuditWriter.WriteAsync(c,t,_time,action,title,actor.UserId,actor.Username,
            actor.Username,type,targetId,targetName,previous,current,title+" — "+targetName,
            null,LocalDate(),ct.CorrelationId(),ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> CreateSiteAsync(JsonObject? payload, BridgePrincipal actor, CancellationToken ct)
    {
        string name = Read(payload, "name");
        if (name.Length is < 2 or > 100 || name == "غير محدد")
            return Fail("اسم الموقع مطلوب من حرفين إلى 100 حرف، ولا يجوز استعمال الاسم المحمي.");
        string normalized = ArabicTextNormalizer.NormalizeForSearch(name);
        string id = "SITE-" + Guid.NewGuid().ToString("N");
        return await _db.ExecuteInWriteTransactionAsync(async (c,t) =>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,"CreateSite",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            using var check = c.CreateCommand();
            check.Transaction = t;
            check.CommandText = "SELECT COUNT(*) FROM Sites WHERE NormalizedName=$n AND ActiveFlag=1;";
            check.Parameters.AddWithValue("$n",normalized);
            if(Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false))>0)
                return Fail("يوجد موقع نشط بنفس الاسم.");
            using var insert = c.CreateCommand();
            insert.Transaction = t;
            insert.CommandText = """
                INSERT INTO Sites (SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
                VALUES ($id,$name,$norm,0,1,$now,1);
                INSERT INTO SiteNameHistory (SiteId,VisibleNameSnapshot,ValidFromLocalDate,
                                             ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
                VALUES ($id,$name,$date,NULL,$user,$now);
                """;
            insert.Parameters.AddWithValue("$id",id);
            insert.Parameters.AddWithValue("$name",name);
            insert.Parameters.AddWithValue("$norm",normalized);
            insert.Parameters.AddWithValue("$now",Now());
            insert.Parameters.AddWithValue("$date",LocalDate());
            insert.Parameters.AddWithValue("$user",actor.UserId);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await Audit(c,t,actor,"SITE_CREATE","إنشاء موقع","Site",id,name,null,
                new JsonObject{["name"]=name}.ToJsonString(),ct).ConfigureAwait(false);
            var outcome=new JsonObject{["siteId"]=id,["name"]=name,["rowVersion"]=1L};
            await MutationIdempotency.StoreAsync(c,t,receipt,outcome,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome);
        },ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> RenameSiteAsync(JsonObject? payload, BridgePrincipal actor, CancellationToken ct)
    {
        string id=Read(payload,"siteId"), name=Read(payload,"name");
        long version=ReadVersion(payload);
        if(id.Length==0 || name.Length is <2 or >100 || version<1)
            return Fail("معرّف الموقع واسمه وإصداره الحالي مطلوبة.");
        if(id=="SITE-UNSPECIFIED")return Fail("موقع غير محدد محمي ولا يمكن تعديله.");
        string normalized=ArabicTextNormalizer.NormalizeForSearch(name);
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>{
            using var check=c.CreateCommand();check.Transaction=t;
            check.CommandText="SELECT VisibleName,RowVersion,IsSystemSite FROM Sites WHERE SiteId=$id AND ActiveFlag=1;";
            check.Parameters.AddWithValue("$id",id);
            string oldName;long oldVersion;int system;
            using(var reader=await check.ExecuteReaderAsync(ct).ConfigureAwait(false)){
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))return Fail("الموقع المطلوب غير موجود.");
                oldName=reader.GetString(0);oldVersion=reader.GetInt64(1);system=reader.GetInt32(2);
            }
            if(system!=0)return Fail("الموقع المحمي لا يمكن تغييره.");
            if(oldVersion!=version)return Fail("تغير الموقع منذ تحميله؛ حدّث القائمة.",
                BridgeErrorCodes.ConflictStale);
            if(oldName==name)return Fail("لم يتغير اسم الموقع.");
            using var dup=c.CreateCommand();dup.Transaction=t;
            dup.CommandText="SELECT COUNT(*) FROM Sites WHERE NormalizedName=$norm AND ActiveFlag=1 AND SiteId<>$id;";
            dup.Parameters.AddWithValue("$norm",normalized);dup.Parameters.AddWithValue("$id",id);
            if(Convert.ToInt64(await dup.ExecuteScalarAsync(ct).ConfigureAwait(false))>0)
                return Fail("اسم الموقع مستخدم مسبقاً.");
            using var hist=c.CreateCommand();hist.Transaction=t;
            hist.CommandText="SELECT ValidFromLocalDate FROM SiteNameHistory WHERE SiteId=$id AND ValidToLocalDate IS NULL ORDER BY HistoryId DESC LIMIT 1;";
            hist.Parameters.AddWithValue("$id",id);
            if((await hist.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString()==LocalDate())
                return Fail("تم تغيير اسم هذا الموقع اليوم بالفعل؛ يحفظ تاريخ الأسماء اعتباراً من اليوم التالي.");
            using var change=c.CreateCommand();change.Transaction=t;
            change.CommandText="""
                UPDATE Sites SET VisibleName=$name,NormalizedName=$norm,RowVersion=RowVersion+1
                WHERE SiteId=$id AND RowVersion=$ver;
                UPDATE SiteNameHistory SET ValidToLocalDate=$date WHERE SiteId=$id AND ValidToLocalDate IS NULL;
                INSERT INTO SiteNameHistory (SiteId,VisibleNameSnapshot,ValidFromLocalDate,
                                             ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
                VALUES ($id,$name,$date,NULL,$user,$now);
                """;
            change.Parameters.AddWithValue("$name",name);change.Parameters.AddWithValue("$norm",normalized);
            change.Parameters.AddWithValue("$id",id);change.Parameters.AddWithValue("$ver",version);
            change.Parameters.AddWithValue("$date",LocalDate());change.Parameters.AddWithValue("$user",actor.UserId);
            change.Parameters.AddWithValue("$now",Now());
            await change.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await Audit(c,t,actor,"SITE_RENAME","إعادة تسمية موقع","Site",id,name,
                new JsonObject{["name"]=oldName}.ToJsonString(),
                new JsonObject{["name"]=name}.ToJsonString(),ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(new JsonObject{["siteId"]=id,["rowVersion"]=version+1});
        },ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retire a non-system site without deleting its historical foreign keys.
    /// The move takes effect TODAY only if no selected person was enrolled,
    /// prepared or moved today. Otherwise refuse rather than rewrite history.
    /// Both the reassignment and per-person/site audit events are atomic.
    /// </summary>
    public async Task<BridgeResponse> RemoveSiteAndReassignCurrentPersonsAsync(
        JsonObject? payload,BridgePrincipal actor,CancellationToken ct)
    {
        string siteId=Read(payload,"siteId");
        long version=ReadVersion(payload);
        long expectedCount;
        bool confirmed;
        try
        {
            expectedCount=payload?["expectedActiveCount"]?.GetValue<long>()??-1;
        }
        catch
        {
            try{expectedCount=payload?["expectedActiveCount"]?.GetValue<int>()??-1;}
            catch{expectedCount=-1;}
        }
        try{confirmed=payload?["confirmed"]?.GetValue<bool>()==true;}
        catch{confirmed=false;}
        if(siteId.Length is <3 or >128 || siteId.Any(char.IsControl) ||
           siteId=="SITE-UNSPECIFIED" || version<1 || expectedCount<0 || !confirmed)
            return Fail("حدد موقعًا غير محمي وإصداره وعدد أفراده، ثم أكّد النقل والإزالة صراحة.");
        if(string.IsNullOrWhiteSpace(Read(payload,"operationId")))
            return Fail("معرّف عملية الإزالة الموثوق مطلوب لمنع تنفيذها مرتين.");
        string date=LocalDate(),now=Now();
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,"RemoveSiteAndReassignCurrentPersons",actor.UserId,payload,ct)
                .ConfigureAwait(false);
            if(replay is not null)return replay;
            using var site=c.CreateCommand();site.Transaction=t;
            site.CommandText="""
                SELECT VisibleName,RowVersion,IsSystemSite FROM Sites
                WHERE SiteId=$id AND ActiveFlag=1;
                """;
            site.Parameters.AddWithValue("$id",siteId);
            string name;long latestVersion;bool isSystem;
            using(var r=await site.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if(!await r.ReadAsync(ct).ConfigureAwait(false))
                    return Fail("الموقع غير موجود أو سبق تعطيله؛ حدّث قائمة المواقع.",
                        BridgeErrorCodes.ConflictStale);
                name=r.GetString(0);latestVersion=r.GetInt64(1);
                isSystem=r.GetInt32(2)!=0;
            }
            if(isSystem)return Fail("موقع غير محدد محمي ولا يمكن إزالته.");
            if(latestVersion!=version)
                return Fail("تغير إصدار الموقع منذ المعاينة؛ حدّث القائمة وأكّد مجددًا.",
                    BridgeErrorCodes.ConflictStale);
            using var current=c.CreateCommand();current.Transaction=t;
            current.CommandText="""
                SELECT a.AssignmentId,a.PersonId,p.CurrentVisibleName,p.CurrentServiceNumberA,
                       a.ValidFromLocalDate,p.OrganizationalStatus,p.ActiveFlag
                FROM PersonAssignments a
                JOIN Persons p ON p.PersonId=a.PersonId
                WHERE a.SiteId=$id AND a.ValidToLocalDate IS NULL
                ORDER BY a.PersonId,a.AssignmentId;
                """;
            current.Parameters.AddWithValue("$id",siteId);
            var people=new List<(string Assignment,string PersonId,string Name,
                string Number,string From,bool Eligible)>();
            using(var r=await current.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while(await r.ReadAsync(ct).ConfigureAwait(false))
                    people.Add((r.GetString(0),r.GetString(1),r.GetString(2),
                        r.GetString(3),r.GetString(4),
                        r.GetInt32(5)==1&&r.GetInt32(6)==1));
            if(people.Any(x=>!x.Eligible))
                return Fail("الموقع يحتوي قيد انتساب غير نشط ما زال مفتوحًا؛ صحّح هذا القيد أولًا دون حذف التاريخ.");
            if(people.Count!=expectedCount)
                return Fail("تغير عدد الأفراد في الموقع منذ المعاينة؛ أعد فتحه وأكّد العدد الجديد.",
                    BridgeErrorCodes.ConflictStale);
            // The storage trigger forbids a zero-length [from,to) assignment;
            // changing an entry opened today would also erase today's location.
            if(people.Any(x=>string.CompareOrdinal(x.From,date)>=0))
                return Fail("يوجد فرد أُدرج أو نُقل إلى الموقع اليوم؛ أجّل إزالة الموقع إلى يوم لاحق لحفظ تاريخه دون تداخل.",
                    BridgeErrorCodes.ConflictStale);
            if(people.Count>0)
            {
                using var closure=c.CreateCommand();closure.Transaction=t;
                closure.CommandText="""
                    SELECT EXISTS(SELECT 1 FROM PeriodClosures
                    WHERE IsClosed=1 AND ((PeriodType='DAY' AND PeriodKey=$day)
                      OR (PeriodType='MONTH' AND PeriodKey=$month)));
                    """;
                closure.Parameters.AddWithValue("$day",date);
                closure.Parameters.AddWithValue("$month",date[..7]);
                if(Convert.ToInt32(await closure.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                    return Fail("اليوم أو الشهر مغلق؛ لا يمكن تغيير الانتساب خلال فترة مغلقة.");
                using var dayEvidence=c.CreateCommand();dayEvidence.Transaction=t;
                dayEvidence.CommandText="""
                    SELECT EXISTS(
                      SELECT 1 FROM PersonAssignments a
                      WHERE a.SiteId=$site AND a.ValidToLocalDate IS NULL
                        AND (EXISTS(SELECT 1 FROM AttendanceRecords d
                            WHERE d.PersonId=a.PersonId AND d.AttendanceLocalDate=$day)
                        OR EXISTS(SELECT 1 FROM PersonnelMovements m
                            WHERE m.PersonId=a.PersonId AND m.EffectiveLocalDate=$day)));
                    """;
                dayEvidence.Parameters.AddWithValue("$site",siteId);
                dayEvidence.Parameters.AddWithValue("$day",date);
                if(Convert.ToInt32(await dayEvidence.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                    return Fail("أحد أفراد الموقع لديه تحضير أو حركة اليوم؛ لا يجوز تغيير موقعه بأثر رجعي. أعد المحاولة في يوم مناسب.",
                        BridgeErrorCodes.ConflictStale);
            }
            foreach(var p in people)
            {
                string nextId="ASG-"+Guid.NewGuid().ToString("N");
                using var move=c.CreateCommand();move.Transaction=t;
                move.CommandText="""
                    UPDATE PersonAssignments SET ValidToLocalDate=$day
                    WHERE AssignmentId=$old AND PersonId=$person AND ValidToLocalDate IS NULL;
                    """;
                move.Parameters.AddWithValue("$day",date);
                move.Parameters.AddWithValue("$old",p.Assignment);
                move.Parameters.AddWithValue("$person",p.PersonId);
                if(await move.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("تغير انتساب أحد الأفراد أثناء التنفيذ؛ تراجعت العملية كاملة.");
                using var create=c.CreateCommand();create.Transaction=t;
                create.CommandText="""
                    INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                      ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                    VALUES($assignment,$person,'SITE-UNSPECIFIED',$day,NULL,NULL,$now);
                    UPDATE Persons SET RowVersion=RowVersion+1 WHERE PersonId=$person;
                    """;
                create.Parameters.AddWithValue("$assignment",nextId);
                create.Parameters.AddWithValue("$person",p.PersonId);
                create.Parameters.AddWithValue("$day",date);
                create.Parameters.AddWithValue("$now",now);
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await BridgeAuditWriter.WriteAsync(c,t,_time,
                    "SITE_RETIRE_PERSON_REASSIGNED","نقل الفرد إلى غير محدد عند إزالة الموقع",
                    actor.UserId,actor.Username,actor.Username,"Person",p.PersonId,p.Name,
                    new JsonObject{["siteId"]=siteId,["siteName"]=name,
                        ["assignmentId"]=p.Assignment}.ToJsonString(),
                    new JsonObject{["siteId"]="SITE-UNSPECIFIED",
                        ["siteName"]="غير محدد",["assignmentId"]=nextId,
                        ["effectiveDate"]=date}.ToJsonString(),
                    "نقل تلقائي آمن بسبب إزالة موقع دون تغيير التحضير التاريخي.",
                    "إزالة الموقع "+name,date,ct.CorrelationId(),ct).ConfigureAwait(false);
            }
            using var deactivate=c.CreateCommand();deactivate.Transaction=t;
            deactivate.CommandText="""
                UPDATE Sites SET ActiveFlag=0,RowVersion=RowVersion+1
                WHERE SiteId=$id AND RowVersion=$version AND IsSystemSite=0 AND ActiveFlag=1;
                """;
            deactivate.Parameters.AddWithValue("$id",siteId);
            deactivate.Parameters.AddWithValue("$version",version);
            if(await deactivate.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                throw new InvalidOperationException("تغير الموقع أثناء التنفيذ؛ تراجعت العملية كاملة.");
            await Audit(c,t,actor,"SITE_RETIRE","إزالة موقع ونقل الأفراد",
                "Site",siteId,name,
                new JsonObject{["active"]=true,["affectedPersons"]=people.Count}.ToJsonString(),
                new JsonObject{["active"]=false,["reassignedTo"]="SITE-UNSPECIFIED",
                    ["effectiveDate"]=date,["affectedPersons"]=people.Count}.ToJsonString(),
                ct).ConfigureAwait(false);
            var outcome=new JsonObject{
                ["siteId"]=siteId,["siteName"]=name,["active"]=false,
                ["reassignedCount"]=people.Count,["effectiveDate"]=date,
                ["rowVersion"]=version+1
            };
            await MutationIdempotency.StoreAsync(c,t,receipt,outcome,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome);
        },ct).ConfigureAwait(false);
    }

    /// <summary>Unique active symbols are compared after Arabic/Latin/digit normalization.
    /// Previously deactivated definitions are historical evidence, not active reservations.</summary>
    private static async Task<bool> ActiveStatusSymbolExists(SqliteConnection c,
        SqliteTransaction tx,string symbol,string? excludedId,CancellationToken ct)
    {
        string normalized=ArabicTextNormalizer.NormalizeForSearch(symbol);
        using var query=c.CreateCommand();query.Transaction=tx;
        query.CommandText="""
            SELECT StatusId,ShortSymbol FROM AttendanceStatusDefinitions
            WHERE ActiveFlag=1;
            """;
        using var reader=await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while(await reader.ReadAsync(ct).ConfigureAwait(false))
            if(reader.GetString(0)!=excludedId &&
               ArabicTextNormalizer.NormalizeForSearch(reader.GetString(1))==normalized)
                return true;
        return false;
    }

    public async Task<BridgeResponse> CreateStatusAsync(JsonObject? payload,BridgePrincipal actor,CancellationToken ct)
    {
        string name=Read(payload,"name"),symbol=Read(payload,"symbol"),color=Read(payload,"color");
        if(name.Length is <2 or >75 || symbol.Length is <1 or >5 || !ColorValid(color))
            return Fail("اسم الحالة (2–75) والرمز (1–5) ولون HEX (#RRGGBB) مطلوبة.");
        string id="STS-"+Guid.NewGuid().ToString("N"), norm=ArabicTextNormalizer.NormalizeForSearch(name);
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>{
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,"CreateAttendanceStatus",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            using var dup=c.CreateCommand();dup.Transaction=t;
            dup.CommandText="SELECT COUNT(*) FROM AttendanceStatusDefinitions WHERE NormalizedName=$name AND ActiveFlag=1;";
            dup.Parameters.AddWithValue("$name",norm);
            if(Convert.ToInt64(await dup.ExecuteScalarAsync(ct).ConfigureAwait(false))>0)
                return Fail("توجد حالة نشطة بنفس الاسم.");
            if(await ActiveStatusSymbolExists(c,t,symbol,null,ct).ConfigureAwait(false))
                return Fail("رمز الحالة مستخدم في حالة نشطة أخرى؛ اختر اختصارًا مختلفًا.");
            using var insert=c.CreateCommand();insert.Transaction=t;
            insert.CommandText="""
                INSERT INTO AttendanceStatusDefinitions (StatusId,VisibleName,NormalizedName,ShortSymbol,
                    ChosenColorHex,IsSystemSeeded,ActiveFlag,SortOrder,CreatedAtUtc,RowVersion)
                VALUES ($id,$name,$norm,$symbol,$color,0,1,100,$now,1);
                INSERT INTO AttendanceStatusNameHistory (StatusId,VisibleNameSnapshot,ShortSymbolSnapshot,
                    ColorSnapshot,ValidFromLocalDate,ValidToLocalDate,RecordedAtUtc)
                VALUES ($id,$name,$symbol,$color,$date,NULL,$now);
                """;
            foreach(var (key,value) in new[]{("$id",id),("$name",name),("$norm",norm),("$symbol",symbol),
                                              ("$color",color),("$now",Now()),("$date",LocalDate())})
                insert.Parameters.AddWithValue(key,value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await Audit(c,t,actor,"STATUS_CREATE","إضافة حالة","Status",id,name,null,
                new JsonObject{["name"]=name,["symbol"]=symbol,["color"]=color}.ToJsonString(),ct).ConfigureAwait(false);
            var outcome=new JsonObject{["statusId"]=id,["rowVersion"]=1L};
            await MutationIdempotency.StoreAsync(c,t,receipt,outcome,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome);
        },ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> UpdateStatusAsync(JsonObject? payload,BridgePrincipal actor,CancellationToken ct)
    {
        string id=Read(payload,"statusId"),name=Read(payload,"name"),symbol=Read(payload,"symbol"),
               color=Read(payload,"color");
        long ver=ReadVersion(payload);
        if(id.Length==0||ver<1||name.Length is <2 or >75||symbol.Length is <1 or >5||!ColorValid(color))
            return Fail("معرّف الحالة، الإصدار، الاسم، الرمز ولون HEX مطلوبة.");
        string norm=ArabicTextNormalizer.NormalizeForSearch(name);
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>{
            using var find=c.CreateCommand();find.Transaction=t;
            find.CommandText="SELECT VisibleName,ShortSymbol,ChosenColorHex,RowVersion FROM AttendanceStatusDefinitions WHERE StatusId=$id AND ActiveFlag=1;";
            find.Parameters.AddWithValue("$id",id);
            string oldName,oldSymbol,oldColor;long oldVer;
            using(var reader=await find.ExecuteReaderAsync(ct).ConfigureAwait(false)){
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))return Fail("الحالة غير موجودة.");
                oldName=reader.GetString(0);oldSymbol=reader.GetString(1);
                oldColor=reader.GetString(2);oldVer=reader.GetInt64(3);
            }
            if(oldVer!=ver)return Fail("الحالة تغيرت؛ يرجى التحديث.",BridgeErrorCodes.ConflictStale);
            if(oldName==name&&oldSymbol==symbol&&oldColor==color)return Fail("لم تتغير بيانات الحالة.");
            using var dup=c.CreateCommand();dup.Transaction=t;
            dup.CommandText="SELECT COUNT(*) FROM AttendanceStatusDefinitions WHERE NormalizedName=$name AND StatusId<>$id AND ActiveFlag=1;";
            dup.Parameters.AddWithValue("$name",norm);dup.Parameters.AddWithValue("$id",id);
            if(Convert.ToInt64(await dup.ExecuteScalarAsync(ct).ConfigureAwait(false))>0)return Fail("اسم الحالة مستخدم.");
            if(await ActiveStatusSymbolExists(c,t,symbol,id,ct).ConfigureAwait(false))
                return Fail("رمز الحالة مستخدم في حالة نشطة أخرى؛ اختر اختصارًا مختلفًا.");
            using var hist=c.CreateCommand();hist.Transaction=t;
            hist.CommandText="SELECT ValidFromLocalDate FROM AttendanceStatusNameHistory WHERE StatusId=$id AND ValidToLocalDate IS NULL ORDER BY HistoryId DESC LIMIT 1;";
            hist.Parameters.AddWithValue("$id",id);
            if((await hist.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString()==LocalDate())
                return Fail("عُدلت الحالة اليوم بالفعل؛ منعاً لتداخل نسخها التاريخية، أعد المحاولة غداً.");
            using var change=c.CreateCommand();change.Transaction=t;
            change.CommandText="""
                UPDATE AttendanceStatusDefinitions SET VisibleName=$name,NormalizedName=$norm,
                    ShortSymbol=$symbol,ChosenColorHex=$color,RowVersion=RowVersion+1
                WHERE StatusId=$id AND RowVersion=$ver;
                UPDATE AttendanceStatusNameHistory SET ValidToLocalDate=$date WHERE StatusId=$id AND ValidToLocalDate IS NULL;
                INSERT INTO AttendanceStatusNameHistory (StatusId,VisibleNameSnapshot,ShortSymbolSnapshot,
                    ColorSnapshot,ValidFromLocalDate,ValidToLocalDate,RecordedAtUtc)
                VALUES ($id,$name,$symbol,$color,$date,NULL,$now);
                """;
            foreach(var (key,value) in new[]{("$id",id),("$name",name),("$norm",norm),("$symbol",symbol),
                                              ("$color",color),("$date",LocalDate()),("$now",Now())})
                change.Parameters.AddWithValue(key,value);
            change.Parameters.AddWithValue("$ver",ver);
            await change.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await Audit(c,t,actor,"STATUS_UPDATE","تعديل حالة","Status",id,name,
                new JsonObject{["name"]=oldName,["symbol"]=oldSymbol,["color"]=oldColor}.ToJsonString(),
                new JsonObject{["name"]=name,["symbol"]=symbol,["color"]=color}.ToJsonString(),ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(new JsonObject{["statusId"]=id,["rowVersion"]=ver+1});
        },ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> DeactivateStatusAsync(JsonObject? payload,BridgePrincipal actor,CancellationToken ct)
    {
        string id=Read(payload,"statusId");long ver=ReadVersion(payload);
        if(id.Length==0||ver<1)return Fail("معرّف الحالة وإصدارها مطلوبان.");
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>{
            using var find=c.CreateCommand();find.Transaction=t;
            find.CommandText="SELECT VisibleName,RowVersion FROM AttendanceStatusDefinitions WHERE StatusId=$id AND ActiveFlag=1;";
            find.Parameters.AddWithValue("$id",id);
            string name;long current;
            using(var reader=await find.ExecuteReaderAsync(ct).ConfigureAwait(false)){
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))return Fail("الحالة غير موجودة.");
                name=reader.GetString(0);current=reader.GetInt64(1);
            }
            if(current!=ver)return Fail("الحالة تغيرت منذ عرضها.",BridgeErrorCodes.ConflictStale);
            using(var configured=c.CreateCommand())
            {
                configured.Transaction=t;
                configured.CommandText="SELECT EXISTS(SELECT 1 FROM AppSettings WHERE SettingKey=$key AND SettingValue=$id);";
                configured.Parameters.AddWithValue("$key",AutoAttendancePolicyService.DefaultStatusKey);
                configured.Parameters.AddWithValue("$id",id);
                if(Convert.ToInt64(await configured.ExecuteScalarAsync(ct).ConfigureAwait(false))>0)
                    return Fail("لا يمكن تعطيل الحالة الافتراضية للتحضير التلقائي. اختر حالة أخرى من الإعدادات أولاً.");
            }
            using var count=c.CreateCommand();count.Transaction=t;
            count.CommandText="SELECT COUNT(*) FROM AttendanceRecords WHERE StatusId=$id;";
            count.Parameters.AddWithValue("$id",id);
            // Deactivation is safe for historical records because their immutable
            // status snapshots and FK parent remain; don't physically delete.
            using var update=c.CreateCommand();update.Transaction=t;
            update.CommandText="UPDATE AttendanceStatusDefinitions SET ActiveFlag=0,RowVersion=RowVersion+1 WHERE StatusId=$id AND RowVersion=$v;";
            update.Parameters.AddWithValue("$id",id);update.Parameters.AddWithValue("$v",ver);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await Audit(c,t,actor,"STATUS_DEACTIVATE","تعطيل حالة","Status",id,name,null,
                new JsonObject{["active"]=false}.ToJsonString(),ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(new JsonObject{["statusId"]=id,["active"]=false});
        },ct).ConfigureAwait(false);
    }
}
