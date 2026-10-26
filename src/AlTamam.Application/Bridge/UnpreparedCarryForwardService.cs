using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// SECTION 04 — early, user-approved carry-forward for unprepared personnel.
/// Reads the PREVIOUS, closed business day's committed status snapshot per person,
/// or the administrator-configured fallback when absent. Selection is previewed,
/// then all selected persons are atomically committed under one-use, actor-bound
/// token; 20:00 automatic reconciliation never overwrites these manual approvals.
/// </summary>
public sealed class UnpreparedCarryForwardService
{
    private const string PreviewOperation="PreviewUnpreparedCarryForward";
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    public UnpreparedCarryForwardService(AlTamamDbContext db,TimeProvider? time=null)
    {
        _db=db;_time=time??TimeProvider.System;
    }
    private string Today()=>_time.GetLocalNow().ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private bool PastCutoff()=>_time.GetLocalNow().TimeOfDay>=TimeSpan.FromHours(20);
    private static BridgeResponse Fail(string message,string code=BridgeErrorCodes.ConflictStale)
        => BridgeHelpers.Error(code,message);
    private static string Read(JsonObject? data,string key)
    {
        try{return data?[key]?.GetValue<string>()?.Trim()??"";}
        catch{return "";}
    }
    private static string[] Ids(JsonObject? data)
    {
        if(data?["personIds"] is not JsonArray a||a.Count is <1 or >100)return [];
        try
        {
            var ids=a.Select(x=>x?.GetValue<string>()?.Trim()??"").ToArray();
            return ids.All(x=>x.Length is >0 and <=128)&&ids.Distinct().Count()==ids.Length?ids:[];
        }
        catch{return [];}
    }
    private static async Task<bool> IsClosed(SqliteConnection c,SqliteTransaction? tx,
        string date,CancellationToken ct)
    {
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT EXISTS(SELECT 1 FROM PeriodClosures WHERE IsClosed=1
                AND ((PeriodType='DAY' AND PeriodKey=$date)
                    OR (PeriodType='MONTH' AND PeriodKey=$month)));
            """;
        q.Parameters.AddWithValue("$date",date);
        q.Parameters.AddWithValue("$month",date[..7]);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct).ConfigureAwait(false))>0;
    }
    private static async Task<bool> PreviousFinal(SqliteConnection c,SqliteTransaction? tx,
        string yesterday,CancellationToken ct)
    {
        if(await IsClosed(c,tx,yesterday,ct).ConfigureAwait(false))return true;
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT EXISTS(SELECT 1 FROM AutoAttendanceRuns WHERE BusinessDate=$date);";
        q.Parameters.AddWithValue("$date",yesterday);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0;
    }
    private static async Task<(string Id,string Name,string Symbol,string Color)?> DefaultStatus(
        SqliteConnection c,SqliteTransaction? tx,CancellationToken ct)
    {
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT s.StatusId,s.VisibleName,s.ShortSymbol,s.ChosenColorHex
            FROM AppSettings a
            JOIN AttendanceStatusDefinitions s
              ON s.StatusId=a.SettingValue AND s.ActiveFlag=1
            WHERE a.SettingKey=$key;
            """;
        q.Parameters.AddWithValue("$key",AutoAttendancePolicyService.DefaultStatusKey);
        using var rd=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(!await rd.ReadAsync(ct).ConfigureAwait(false))return null;
        return (rd.GetString(0),rd.GetString(1),rd.GetString(2),rd.GetString(3));
    }
    private static async Task<JsonObject?> Snapshot(SqliteConnection c,SqliteTransaction? tx,
        string id,string today,string yesterday,
        (string Id,string Name,string Symbol,string Color)? defaultStatus,CancellationToken ct)
    {
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT p.CurrentVisibleName,p.CurrentServiceNumberA,
                a.AssignmentId,a.SiteId,s.VisibleName,
                old.StatusId,old.StatusNameSnapshot,old.StatusSymbolSnapshot,
                old.StatusColorSnapshot
            FROM Persons p
            JOIN PersonAssignments a ON a.PersonId=p.PersonId
                AND a.ValidFromLocalDate<=$today
                AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate>$today)
            JOIN Sites s ON s.SiteId=a.SiteId
            LEFT JOIN AttendanceRecords old ON old.PersonId=p.PersonId
                AND old.AttendanceLocalDate=$yesterday
            WHERE p.PersonId=$id AND p.ActiveFlag=1 AND p.OrganizationalStatus=1
                AND p.FirstRecordedLocalDate<=$today
                AND NOT EXISTS (SELECT 1 FROM AttendanceRecords curr
                    WHERE curr.PersonId=p.PersonId AND curr.AttendanceLocalDate=$today)
                AND NOT EXISTS (SELECT 1 FROM PersonnelMovements m
                    WHERE m.PersonId=p.PersonId AND m.EffectiveLocalDate=$today
                      AND m.MovementType IN (1,2,3));
            """;
        q.Parameters.AddWithValue("$id",id);
        q.Parameters.AddWithValue("$today",today);
        q.Parameters.AddWithValue("$yesterday",yesterday);
        using var rd=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(!await rd.ReadAsync(ct).ConfigureAwait(false))return null;
        bool hasYesterday=!rd.IsDBNull(5);
        if(!hasYesterday && defaultStatus is null) return null;
        var item=new JsonObject
        {
            ["personId"]=id,
            ["name"]=rd.GetString(0),
            ["serviceNumberA"]=rd.GetString(1),
            ["assignmentId"]=rd.GetString(2),
            ["siteId"]=rd.GetString(3),
            ["siteName"]=rd.GetString(4),
            ["statusId"]=hasYesterday?rd.GetString(5):defaultStatus!.Value.Id,
            ["statusName"]=hasYesterday?rd.GetString(6):defaultStatus!.Value.Name,
            ["statusSymbol"]=hasYesterday?rd.GetString(7):defaultStatus!.Value.Symbol,
            ["statusColor"]=hasYesterday?rd.GetString(8):defaultStatus!.Value.Color,
            ["fromYesterday"]=hasYesterday,
            ["referenceDate"]=yesterday
        };
        // If assignments overlap, never guess an active site for a person.
        if(await rd.ReadAsync(ct).ConfigureAwait(false))return null;
        return item;
    }
    private async Task<BridgeResponse?> Guard(
        SqliteConnection c,SqliteTransaction? tx,string date,CancellationToken ct)
    {
        if(date!=Today())
            return Fail("اعتماد غير المحضرين متاح لتاريخ اليوم المحلي فقط.",BridgeErrorCodes.HistoricDayLocked);
        if(PastCutoff())
            return Fail("انتهى الاعتماد المبكر عند الساعة 8 مساءً؛ حالة اليوم تعتمدها الجدولة الآلية.",
                BridgeErrorCodes.HistoricDayLocked);
        if(await IsClosed(c,tx,date,ct).ConfigureAwait(false))
            return Fail("يوم التحضير مغلق؛ لا يجوز إعادة تحريره.",BridgeErrorCodes.HistoricDayLocked);
        if(!await PreviousFinal(c,tx,DateOnly.ParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture)
            .AddDays(-1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),ct).ConfigureAwait(false))
            return Fail("لا يمكن اعتماد حالة أمس قبل اكتمال إغلاق اليوم السابق. "+
                "راجع سجل التحضير التلقائي أو إغلاق اليوم السابق.");
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT EXISTS(SELECT 1 FROM AutoAttendanceRuns WHERE BusinessDate=$date);";
        q.Parameters.AddWithValue("$date",date);
        if(Convert.ToInt64(await q.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
            return Fail("تم اعتماد اليوم تلقائياً بالفعل؛ لا يمكن إعادة الكتابة فوقه.",
                BridgeErrorCodes.HistoricDayLocked);
        return null;
    }
    public async Task<BridgeResponse> PreviewAsync(JsonObject? data,BridgePrincipal actor,
        CancellationToken ct)
    {
        string date=Read(data,"businessDate"),site=Read(data,"siteId");
        string[] ids=Ids(data);
        if(ids.Length is <1 or >100)
            return Fail("اختر بين فرد واحد و100 فرد دون تكرار للمعاينة.",
                BridgeErrorCodes.ValidationError);
        using var c=_db.CreateOpenConnection();
        var blocked=await Guard(c,null,date,ct).ConfigureAwait(false);
        if(blocked is not null)return blocked;
        var fallback=await DefaultStatus(c,null,ct).ConfigureAwait(false);
        string yesterday=DateOnly.ParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture)
            .AddDays(-1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        var people=new JsonArray();
        foreach(string id in ids)
        {
            var row=await Snapshot(c,null,id,date,yesterday,fallback,ct).ConfigureAwait(false);
            if(row is null)
                return Fail("تعذر معاينة الفرد "+id+": ربما تحضّر اليوم أو نُقل، "+
                    "أو ليس له تحضير أمس ولا حالة افتراضية نشطة. حدّث القائمة.");
            if(site.Length>0 && Read(row,"siteId")!=site)
                return Fail("موقع الفرد "+id+" لا يطابق الموقع المحدد.");
            people.Add(row);
        }
        var plan=new JsonObject{["businessDate"]=date,["siteId"]=site,["persons"]=people};
        string token=BridgePreviewTokens.Issue(_db,_time,PreviewOperation,actor.UserId,plan,0,ct);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["previewToken"]=token,["businessDate"]=date,["referenceDate"]=yesterday,
            ["persons"]=people.DeepClone(),["count"]=people.Count
        });
    }
    public async Task<BridgeResponse> CommitAsync(JsonObject? data,BridgePrincipal actor,
        CancellationToken ct)
    {
        string token=Read(data,"previewToken");
        if(token.Length!=64)return Fail("رمز معاينة الاعتماد السابق مطلوب.",
            BridgeErrorCodes.ValidationError);
        return await _db.ExecuteInWriteTransactionAsync(async(c,tx)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,tx,"CommitUnpreparedCarryForward",actor.UserId,data,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            string? raw;
            using(var q=c.CreateCommand())
            {
                q.Transaction=tx;
                q.CommandText="""
                    SELECT PayloadCanonicalJson FROM PreviewTokens
                    WHERE Token=$token AND Operation=$op AND ActorUserId=$actor
                      AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
                    """;
                q.Parameters.AddWithValue("$token",token);
                q.Parameters.AddWithValue("$op",PreviewOperation);
                q.Parameters.AddWithValue("$actor",actor.UserId);
                q.Parameters.AddWithValue("$now",_time.GetUtcNow().ToString("O"));
                raw=await q.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }
            if(raw is null)return Fail("انتهت المعاينة أو استُخدمت مسبقاً؛ أعد تحديد الأفراد.");
            var plan=JsonNode.Parse(raw) as JsonObject;
            if(plan is null||plan["persons"] is not JsonArray people||people.Count is <1 or >100)
                return Fail("بيانات المعاينة غير صحيحة.");
            string date=Read(plan,"businessDate"),site=Read(plan,"siteId");
            var blocked=await Guard(c,tx,date,ct).ConfigureAwait(false);
            if(blocked is not null)return blocked;
            var fallback=await DefaultStatus(c,tx,ct).ConfigureAwait(false);
            string yesterday=DateOnly.ParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture)
                .AddDays(-1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            var current=new List<JsonObject>();
            foreach(var stored in people.OfType<JsonObject>())
            {
                string personId=Read(stored,"personId");
                var fresh=await Snapshot(c,tx,personId,date,yesterday,fallback,ct).ConfigureAwait(false);
                if(fresh is null||(site.Length>0 && Read(fresh,"siteId")!=site)||
                   BridgePreviewTokens.Canonicalize(fresh)!=BridgePreviewTokens.Canonicalize(stored))
                    return Fail("تغيرت حالة أو موقع «"+Read(stored,"name")+"» (ع: "+
                        Read(stored,"serviceNumberA")+"). لم يُعتمد أي فرد؛ أعد المعاينة.");
                current.Add(fresh);
            }
            if(current.Count!=people.Count)return Fail("هناك بيانات تكرار أو نقص في المعاينة.");
            long? consumed=await BridgePreviewTokens.ConsumeAsync(_db,_time,c,tx,token,
                PreviewOperation,actor.UserId,0,ct).ConfigureAwait(false);
            if(consumed!=0)return Fail("المعاينة استخدمت مسبقاً أو انتهت.");
            string now=_time.GetUtcNow().ToString("O");
            var auditPersons=new JsonArray();
            foreach(var row in current)
            {
                using var q=c.CreateCommand();q.Transaction=tx;
                q.CommandText="""
                    INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                        StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                        AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                        Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                    VALUES($id,$date,$status,$name,$symbol,$color,
                        $assign,$site,$siteName,'bulk',$actor,$now,1,$key);
                    """;
                q.Parameters.AddWithValue("$id",Read(row,"personId"));
                q.Parameters.AddWithValue("$date",date);
                q.Parameters.AddWithValue("$status",Read(row,"statusId"));
                q.Parameters.AddWithValue("$name",Read(row,"statusName"));
                q.Parameters.AddWithValue("$symbol",Read(row,"statusSymbol"));
                q.Parameters.AddWithValue("$color",Read(row,"statusColor"));
                q.Parameters.AddWithValue("$assign",Read(row,"assignmentId"));
                q.Parameters.AddWithValue("$site",Read(row,"siteId"));
                q.Parameters.AddWithValue("$siteName",Read(row,"siteName"));
                q.Parameters.AddWithValue("$actor",actor.UserId);
                q.Parameters.AddWithValue("$now",now);
                q.Parameters.AddWithValue("$key",token+":"+Read(row,"personId"));
                await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                auditPersons.Add(new JsonObject
                {
                    ["personId"]=Read(row,"personId"),
                    ["serviceNumberA"]=Read(row,"serviceNumberA"),
                    ["name"]=Read(row,"name"),
                    ["statusId"]=Read(row,"statusId"),
                    ["fromYesterday"]=row["fromYesterday"]!.GetValue<bool>()
                });
            }
            await BridgeAuditWriter.WriteAsync(c,tx,_time,
                "UNPREPARED_CARRY_FORWARD","اعتماد مبكر لحالة اليوم السابق",
                actor.UserId,actor.Username,actor.Username,"AttendanceBatch",date,
                "غير المحضرين: "+current.Count+" فرد",
                null,new JsonObject{["businessDate"]=date,["persons"]=auditPersons}.ToJsonString(),
                "اعتماد فردي/جماعي لحالة أمس المغلق، قبل الساعة الثامنة مساءً؛ "+
                "الحالة الافتراضية تُستخدم فقط لمن لم تكن له حالة أمس.",
                null,date,Guid.NewGuid().ToString("N"),ct).ConfigureAwait(false);
            var result=new JsonObject{["count"]=current.Count,["businessDate"]=date,
                ["referenceDate"]=yesterday,["approvedBeforeEight"]=true};
            await MutationIdempotency.StoreAsync(c,tx,receipt,result,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(result,
                events:BridgeHelpers.InvalidationEvent(UiInvalidationEvents.AttendanceCommitted,
                    date,current.Select(x=>Read(x,"personId"))));
        },ct).ConfigureAwait(false);
    }
}
