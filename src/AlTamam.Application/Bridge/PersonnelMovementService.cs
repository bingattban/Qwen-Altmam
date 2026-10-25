using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Today's internal/external transfer. One-use preview; assignment history,
/// immutable movement and audit are committed together in SQLCipher.
/// Transfer day has no attendance at either site.
/// </summary>
public sealed class PersonnelMovementService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    public PersonnelMovementService(AlTamamDbContext db, TimeProvider? time=null)
    { _db=db; _time=time??TimeProvider.System; }

    private static string Get(JsonObject? obj,string name)
    { try{return obj?[name]?.GetValue<string>()?.Trim()??"";}catch{return "";} }
    private static int Num(JsonObject? obj,string name,int fallback)
    { try{return obj?[name]?.GetValue<int>()??fallback;}catch{return fallback;} }
    private string Today()=>_time.GetLocalNow().ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static BridgeResponse Reject(string code,string message)=>BridgeHelpers.Error(code,message);
    private static string[] Selected(JsonObject? data)
    {
        if(data?["personIds"] is not JsonArray list)return [];
        try
        {
            var ids=list.Select(x=>x?.GetValue<string>()?.Trim()??"").ToArray();
            return ids.Length is >=1 and <=100 && ids.All(x=>x.Length>0)
                && ids.Distinct(StringComparer.Ordinal).Count()==ids.Length?ids:[];
        }
        catch{return [];}
    }
    private sealed record Person(string Id,string Name,string Number,long Revision,
        string AssignmentId,string SiteId,string SiteName);

    private static async Task<Person?> ActivePerson(SqliteConnection conn,SqliteTransaction? tx,
        string id,string date,CancellationToken ct)
    {
        using var cmd=conn.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT p.CurrentVisibleName,p.CurrentServiceNumberA,p.RowVersion,
                   a.AssignmentId,a.SiteId,s.VisibleName
            FROM Persons p
            JOIN PersonAssignments a ON a.PersonId=p.PersonId
                AND a.ValidFromLocalDate <= $date AND a.ValidToLocalDate IS NULL
            JOIN Sites s ON s.SiteId=a.SiteId
            WHERE p.PersonId=$id AND p.OrganizationalStatus=1 AND p.ActiveFlag=1
              AND p.FirstRecordedLocalDate <= $date;
            """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$date",date);
        using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(!await r.ReadAsync(ct).ConfigureAwait(false))return null;
        var person=new Person(id,r.GetString(0),r.GetString(1),r.GetInt64(2),
            r.GetString(3),r.GetString(4),r.GetString(5));
        return await r.ReadAsync(ct).ConfigureAwait(false)?null:person;
    }
    private static JsonObject PersonJson(Person p)=>new()
    {
        ["personId"]=p.Id,["name"]=p.Name,["serviceNumberA"]=p.Number,
        ["rowVersion"]=p.Revision,["sourceAssignmentId"]=p.AssignmentId,
        ["sourceSiteId"]=p.SiteId,["sourceSiteName"]=p.SiteName
    };
    private static async Task<string?> ActiveSite(SqliteConnection c,SqliteTransaction? tx,
        string id,CancellationToken ct)
    {
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT VisibleName FROM Sites WHERE SiteId=$id AND ActiveFlag=1;";
        q.Parameters.AddWithValue("$id",id);
        return (await q.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string;
    }
    private static async Task<bool> Locked(SqliteConnection c,SqliteTransaction? tx,
        string date,CancellationToken ct)
    {
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT EXISTS(SELECT 1 FROM PeriodClosures WHERE IsClosed=1
            AND ((PeriodType='DAY' AND PeriodKey=$d) OR (PeriodType='MONTH' AND PeriodKey=$m)));
            """;
        q.Parameters.AddWithValue("$d",date);q.Parameters.AddWithValue("$m",date[..7]);
        return Convert.ToInt32(await q.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0;
    }
    private static async Task<bool> DayConflict(SqliteConnection c,SqliteTransaction? tx,
        string id,string date,CancellationToken ct)
    {
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT (SELECT COUNT(*) FROM AttendanceRecords
                WHERE PersonId=$id AND AttendanceLocalDate=$d) +
                (SELECT COUNT(*) FROM PersonnelMovements
                WHERE PersonId=$id AND EffectiveLocalDate=$d);
            """;
        q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$d",date);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct).ConfigureAwait(false))>0;
    }
    private static string Operation(bool external)=>external?"TRANSFER_EXTERNAL":"TRANSFER_INTERNAL";

    public async Task<BridgeResponse> PreviewAsync(JsonObject? data,BridgePrincipal actor,
        bool external,CancellationToken ct)
    {
        string date=Get(data,"businessDate"),source=Get(data,"sourceSiteId"),
            destination=Get(data,"destinationSiteId"),externalName=Get(data,"destinationExternalName");
        var ids=Selected(data);
        if(date!=Today())return Reject(BridgeErrorCodes.HistoricDayLocked,
            "النقل يُنفّذ لتاريخ اليوم فقط. تغيير الماضي غير مسموح.");
        if(ids.Length==0||source.Length==0)
            return Reject(BridgeErrorCodes.ValidationError,"حدد الموقع المصدر وفرداً أو أكثر (100 كحد أقصى).");
        if(external ? externalName.Length is <2 or >150 :
            (destination.Length==0||destination==source))
            return Reject(BridgeErrorCodes.InvalidTransferSites,"حدد جهة نقل مختلفة وصحيحة.");
        using var c=_db.CreateOpenConnection();
        if(await Locked(c,null,date,ct).ConfigureAwait(false))
            return Reject(BridgeErrorCodes.HistoricDayLocked,"اليوم أو الشهر مغلق.");
        var destName=external?externalName:await ActiveSite(c,null,destination,ct).ConfigureAwait(false);
        if(destName is null)return Reject(BridgeErrorCodes.InvalidTransferSites,"الموقع المنقول إليه غير موجود أو معطل.");
        var selected=new JsonArray();
        foreach(var id in ids)
        {
            var p=await ActivePerson(c,null,id,date,ct).ConfigureAwait(false);
            if(p is null||p.SiteId!=source)
                return Reject(BridgeErrorCodes.ConflictStale,"تغير موقع الفرد أو انتهى نشاطه. حدّث الكشف.");
            if(await DayConflict(c,null,id,date,ct).ConfigureAwait(false))
                return Reject(BridgeErrorCodes.TransferDayAttendanceForbidden,
                    "لا يمكن نقل فرد محضر اليوم أو سبق نقله اليوم. لم يتغير أي فرد.");
            selected.Add(PersonJson(p));
        }
        var envelope=new JsonObject
        {
            ["businessDate"]=date,["sourceSiteId"]=source,
            ["destinationSiteId"]=external?null:destination,
            ["destinationName"]=destName,["external"]=external,["persons"]=selected
        };
        string token=BridgePreviewTokens.Issue(_db,_time,Operation(external),actor.UserId,envelope,0,ct);
        var preview=(JsonObject)envelope.DeepClone();
        preview["previewToken"]=token;preview["count"]=ids.Length;
        return BridgeHelpers.Ok(preview);
    }

    public async Task<BridgeResponse> CommitAsync(JsonObject? data,BridgePrincipal actor,
        bool external,CancellationToken ct)
    {
        string token=Get(data,"previewToken");
        if(token.Length<32)return Reject(BridgeErrorCodes.ValidationError,"لا يوجد رمز معاينة موثوق.");
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            // A successful transfer and its retry receipt commit together.
            // On a lost bridge response, an IDENTICAL operationId + previewToken
            // replays the original success without duplicating movements/audit.
            // Different data for the same operationId fails closed.
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,external?"CommitExternalTransfer":"CommitInternalTransfer",
                actor.UserId,data,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            using var find=c.CreateCommand();find.Transaction=t;
            find.CommandText="""
                SELECT PayloadCanonicalJson FROM PreviewTokens WHERE Token=$token
                  AND Operation=$operation AND ActorUserId=$actor
                  AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
                """;
            find.Parameters.AddWithValue("$token",token);
            find.Parameters.AddWithValue("$operation",Operation(external));
            find.Parameters.AddWithValue("$actor",actor.UserId);
            find.Parameters.AddWithValue("$now",_time.GetUtcNow().ToString("O"));
            string? json=await find.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if(json is null)return Reject(BridgeErrorCodes.ConflictStale,
                "المعاينة منتهية أو مستعملة؛ أعد العرض قبل التأكيد.");
            var plan=JsonNode.Parse(json) as JsonObject;
            if(plan is null||Get(plan,"businessDate")!=Today() ||
               plan["persons"] is not JsonArray people||people.Count is <1 or >100)
                return Reject(BridgeErrorCodes.HistoricDayLocked,"المعاينة لا تنتمي ليوم العمل الحالي.");
            string date=Today(),destId=Get(plan,"destinationSiteId"),
                destName=Get(plan,"destinationName"),source=Get(plan,"sourceSiteId");
            if(await Locked(c,t,date,ct).ConfigureAwait(false))
                return Reject(BridgeErrorCodes.HistoricDayLocked,"أُغلق اليوم قبل النقل.");
            if(!external && await ActiveSite(c,t,destId,ct).ConfigureAwait(false)!=destName)
                return Reject(BridgeErrorCodes.ConflictStale,"تغير اسم الموقع الهدف منذ المعاينة.");
            var entries=new List<Person>();
            foreach(var item in people)
            {
                if(item is not JsonObject previous)return Reject(BridgeErrorCodes.ValidationError,"معاينة تالفة.");
                var person=await ActivePerson(c,t,Get(previous,"personId"),date,ct).ConfigureAwait(false);
                if(person is null || person.SiteId!=source ||
                   person.AssignmentId!=Get(previous,"sourceAssignmentId") ||
                   person.SiteName!=Get(previous,"sourceSiteName") ||
                   person.Revision!=previous["rowVersion"]?.GetValue<long>() ||
                   await DayConflict(c,t,Get(previous,"personId"),date,ct).ConfigureAwait(false))
                    return Reject(BridgeErrorCodes.ConflictStale,
                        "أحد الأفراد تغير موقعه أو تحضيره أو سبق نقله اليوم؛ أُلغي النقل الجماعي.");
                entries.Add(person);
            }
            long? used=await BridgePreviewTokens.ConsumeAsync(_db,_time,c,t,token,
                Operation(external),actor.UserId,0,ct).ConfigureAwait(false);
            if(used!=0)return Reject(BridgeErrorCodes.ConflictStale,"معاينة غير صالحة أو مستخدمة.");
            string timestamp=_time.GetUtcNow().ToString("O"),corr=Guid.NewGuid().ToString("N");
            foreach(var p in entries)
            {
                string movementId="MOV-"+Guid.NewGuid().ToString("N");
                using var q=c.CreateCommand();q.Transaction=t;
                q.CommandText="""
                    UPDATE PersonAssignments SET ValidToLocalDate=$date
                    WHERE AssignmentId=$assignment AND ValidToLocalDate IS NULL;
                    """;
                q.Parameters.AddWithValue("$date",date);
                q.Parameters.AddWithValue("$assignment",p.AssignmentId);
                if(await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("Stale source assignment.");
                if(!external)
                {
                    using var insert=c.CreateCommand();insert.Transaction=t;
                    insert.CommandText="""
                        INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                            ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                        VALUES($id,$person,$site,$date,NULL,$movement,$now);
                        """;
                    insert.Parameters.AddWithValue("$id","ASSIGN-"+Guid.NewGuid().ToString("N"));
                    insert.Parameters.AddWithValue("$person",p.Id);
                    insert.Parameters.AddWithValue("$site",destId);
                    insert.Parameters.AddWithValue("$date",date);
                    insert.Parameters.AddWithValue("$movement",movementId);
                    insert.Parameters.AddWithValue("$now",timestamp);
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                using(var state=c.CreateCommand())
                {
                    state.Transaction=t;
                    state.CommandText=external
                        ? "UPDATE Persons SET OrganizationalStatus=2,ActiveFlag=0,RowVersion=RowVersion+1 WHERE PersonId=$id AND RowVersion=$rev;"
                        : "UPDATE Persons SET RowVersion=RowVersion+1 WHERE PersonId=$id AND RowVersion=$rev;";
                    state.Parameters.AddWithValue("$id",p.Id);
                    state.Parameters.AddWithValue("$rev",p.Revision);
                    if(await state.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                        throw new InvalidOperationException("Concurrent personnel revision.");
                }
                using var move=c.CreateCommand();move.Transaction=t;
                move.CommandText="""
                    INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,
                        ServiceNumberASnapshot,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                        DestinationSiteId,DestinationSiteNameSnapshot,DestinationExternalName,
                        SeparationReason,EffectiveLocalDate,CommittedAtUtc,ActorUserId,IdempotencyKey)
                    VALUES($id,$person,$name,$number,$type,$source,$sourceName,
                           $dest,$destName,$external,NULL,$date,$now,$actor,$key);
                    """;
                move.Parameters.AddWithValue("$id",movementId);
                move.Parameters.AddWithValue("$person",p.Id);
                move.Parameters.AddWithValue("$name",p.Name);
                move.Parameters.AddWithValue("$number",p.Number);
                move.Parameters.AddWithValue("$type",external?2:1);
                move.Parameters.AddWithValue("$source",p.SiteId);
                move.Parameters.AddWithValue("$sourceName",p.SiteName);
                move.Parameters.AddWithValue("$dest",external?DBNull.Value:destId);
                move.Parameters.AddWithValue("$destName",external?DBNull.Value:destName);
                move.Parameters.AddWithValue("$external",external?destName:DBNull.Value);
                move.Parameters.AddWithValue("$date",date);
                move.Parameters.AddWithValue("$now",timestamp);
                move.Parameters.AddWithValue("$actor",actor.UserId);
                move.Parameters.AddWithValue("$key",token+":"+p.Id);
                await move.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await BridgeAuditWriter.WriteAsync(c,t,_time,
                    external?"TRANSFER_EXTERNAL":"TRANSFER_INTERNAL",
                    external?"نقل خارجي":"نقل داخلي",
                    actor.UserId,actor.Username,actor.Username,"Person",p.Id,p.Name,
                    new JsonObject{["siteId"]=p.SiteId,["siteName"]=p.SiteName}.ToJsonString(),
                    new JsonObject{["destinationSiteId"]=external?null:destId,
                        ["destinationName"]=destName,["movementId"]=movementId,
                        ["serviceNumberA"]=p.Number}.ToJsonString(),
                    "نقل "+p.Name+" من "+p.SiteName+" إلى "+destName,null,date,corr,ct)
                    .ConfigureAwait(false);
                if(external)
                    await AccountLifecycleService.SuspendAccountsForPersonnelExitAsync(
                        c,t,p.Id,"TRANSFER_EXTERNAL",actor.UserId,actor.Username,_time,date,ct)
                        .ConfigureAwait(false);
            }
            var responseData=new JsonObject
            {
                ["count"]=entries.Count,["destinationName"]=destName,
                ["businessDate"]=date,["external"]=external
            };
            await MutationIdempotency.StoreAsync(c,t,receipt,responseData,_time,ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(responseData,events:
                BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonnelMoved,
                    date,entries.Select(x=>x.Id)));
        },ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> ArchiveAsync(JsonObject? payload,CancellationToken ct)
    {
        int page=Math.Clamp(Num(payload,"page",1),1,100000),size=Math.Clamp(Num(payload,"pageSize",50),1,100);
        string type=Get(payload,"type"),term=Get(payload,"term"),
            siteId=Get(payload,"siteId"),dateFrom=Get(payload,"dateFrom"),dateTo=Get(payload,"dateTo");
        if (type.Length>0 && type!="internal" && type!="external")
            return Reject(BridgeErrorCodes.ValidationError,"نوع النقل غير صالح.");
        if ((dateFrom.Length>0 && (!DateOnly.TryParseExact(dateFrom,"yyyy-MM-dd",out _) || dateFrom.Length!=10)) ||
            (dateTo.Length>0 && (!DateOnly.TryParseExact(dateTo,"yyyy-MM-dd",out _) || dateTo.Length!=10)) ||
            (dateFrom.Length>0 && dateTo.Length>0 && string.CompareOrdinal(dateFrom,dateTo)>0))
            return Reject(BridgeErrorCodes.ValidationError,
                "نطاق التاريخ غير صالح؛ يجب أن يكون تاريخ البداية قبل النهاية.");
        int filter=type=="internal"?1:type=="external"?2:0;
        string like="%"+term.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%";
        const string where="""
            WHERE m.MovementType IN (1,2) AND ($type=0 OR m.MovementType=$type)
              AND (m.PersonNameSnapshot LIKE $term ESCAPE '\'
               OR m.ServiceNumberASnapshot LIKE $term ESCAPE '\')
              AND ($site='' OR m.SourceSiteId=$site OR m.DestinationSiteId=$site)
              AND ($dateFrom='' OR m.EffectiveLocalDate >= $dateFrom)
              AND ($dateTo='' OR m.EffectiveLocalDate <= $dateTo)
            """;
        using var c=_db.CreateOpenConnection();
        using var count=c.CreateCommand();
        count.CommandText="SELECT COUNT(*) FROM PersonnelMovements m "+where;
        count.Parameters.AddWithValue("$type",filter);
        count.Parameters.AddWithValue("$term",like);
        count.Parameters.AddWithValue("$site",siteId);
        count.Parameters.AddWithValue("$dateFrom",dateFrom);
        count.Parameters.AddWithValue("$dateTo",dateTo);
        long total=Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
            SELECT m.MovementId,m.PersonId,m.PersonNameSnapshot,m.ServiceNumberASnapshot,
                   m.MovementType,m.SourceSiteNameSnapshot,
                   COALESCE(m.DestinationSiteNameSnapshot,m.DestinationExternalName,''),
                   m.EffectiveLocalDate,m.CommittedAtUtc
            FROM PersonnelMovements m
            """+"\n"+where+"\n"+"""
            ORDER BY m.EffectiveLocalDate DESC,m.CommittedAtUtc DESC,m.MovementId
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$type",filter);
        cmd.Parameters.AddWithValue("$term",like);
        cmd.Parameters.AddWithValue("$site",siteId);
        cmd.Parameters.AddWithValue("$dateFrom",dateFrom);
        cmd.Parameters.AddWithValue("$dateTo",dateTo);
        cmd.Parameters.AddWithValue("$limit",size);
        cmd.Parameters.AddWithValue("$offset",(long)(page-1)*size);
        var rows=new JsonArray();
        using var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while(await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new JsonObject
            {
                ["movementId"]=reader.GetString(0),["personId"]=reader.GetString(1),
                ["name"]=reader.GetString(2),["serviceNumberA"]=reader.GetString(3),
                ["type"]=reader.GetInt32(4)==1?"داخلي":"خارجي",
                ["sourceSite"]=reader.GetString(5),["destination"]=reader.GetString(6),
                ["date"]=reader.GetString(7),["committedAtUtc"]=reader.GetString(8)
            });
        return BridgeHelpers.Ok(new JsonObject
        {
            ["items"]=rows,["page"]=page,["pageSize"]=size,
            ["total"]=total,["hasNext"]=(long)page*size<total
        });
    }
}
