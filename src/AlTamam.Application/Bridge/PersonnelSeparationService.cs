using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// A separation is a dated organizational transition, NOT a physical deletion.
/// Preview, enforced five-second confirmation, revalidation, single-use token,
/// membership close, permanent archive and tamper-evident audit are atomic.
/// </summary>
public sealed class PersonnelSeparationService
{
    private const string Operation = "PERSONNEL_SEPARATION";
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    public PersonnelSeparationService(AlTamamDbContext db, TimeProvider? time=null)
    { _db=db;_time=time??TimeProvider.System; }
    private string Today()=>_time.GetLocalNow().ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static string Text(JsonObject? o,string key)
    { try{return o?[key]?.GetValue<string>()?.Trim()??"";}catch{return "";} }
    private static int Number(JsonObject? o,string key,int fallback)
    { try{return o?[key]?.GetValue<int>()??fallback;}catch{return fallback;} }
    private static BridgeResponse Fail(string code,string message)=>BridgeHelpers.Error(code,message);

    private sealed record Person(string Id,string Name,string Number,long Revision,
        string Assignment,string SiteId,string SiteName);

    private static async Task<Person?> Resolve(SqliteConnection c,SqliteTransaction? tx,
        string personId,string date,CancellationToken ct)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT p.CurrentVisibleName,p.CurrentServiceNumberA,p.RowVersion,
                   a.AssignmentId,a.SiteId,s.VisibleName
            FROM Persons p
            JOIN PersonAssignments a ON a.PersonId=p.PersonId
                AND a.ValidFromLocalDate<=$date AND a.ValidToLocalDate IS NULL
            JOIN Sites s ON s.SiteId=a.SiteId
            WHERE p.PersonId=$id AND p.ActiveFlag=1 AND p.OrganizationalStatus=1
              AND p.FirstRecordedLocalDate<=$date;
            """;
        cmd.Parameters.AddWithValue("$id",personId);
        cmd.Parameters.AddWithValue("$date",date);
        using var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(!await reader.ReadAsync(ct).ConfigureAwait(false))return null;
        var result=new Person(personId,reader.GetString(0),reader.GetString(1),
            reader.GetInt64(2),reader.GetString(3),reader.GetString(4),reader.GetString(5));
        return await reader.ReadAsync(ct).ConfigureAwait(false)?null:result;
    }
    private static async Task<bool> Forbidden(SqliteConnection c,SqliteTransaction? tx,
        string personId,string date,CancellationToken ct)
    {
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT
              (SELECT COUNT(*) FROM AttendanceRecords WHERE PersonId=$id AND AttendanceLocalDate=$date)
              +(SELECT COUNT(*) FROM PersonnelMovements WHERE PersonId=$id AND EffectiveLocalDate=$date)
              +(SELECT COUNT(*) FROM PeriodClosures WHERE IsClosed=1
                   AND ((PeriodType='DAY' AND PeriodKey=$date)
                        OR (PeriodType='MONTH' AND PeriodKey=$month)));
            """;
        q.Parameters.AddWithValue("$id",personId);
        q.Parameters.AddWithValue("$date",date);
        q.Parameters.AddWithValue("$month",date[..7]);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0;
    }
    public async Task<BridgeResponse> PreviewAsync(JsonObject? data,BridgePrincipal actor,CancellationToken ct)
    {
        string id=Text(data,"personId"),reason=Text(data,"reason"),date=Text(data,"businessDate");
        if(date!=Today())return Fail(BridgeErrorCodes.HistoricDayLocked,
            "الفصل مسموح لليوم الحالي فقط، ولا يمكن إعادة كتابة التواريخ السابقة.");
        if(id.Length<2||reason.Length is <5 or >1000)
            return Fail(BridgeErrorCodes.ValidationError,"اختر فرداً مسجلاً واكتب سبب فصل واضحاً (5–1000 حرف).");
        using var c=_db.CreateOpenConnection();
        var person=await Resolve(c,null,id,date,ct).ConfigureAwait(false);
        if(person is null)return Fail(BridgeErrorCodes.ConflictStale,
            "الفرد غير نشط، أو نُقل، أو لا يمتلك موقعاً صالحاً للفصل.");
        if(await Forbidden(c,null,id,date,ct).ConfigureAwait(false))
            return Fail(BridgeErrorCodes.TransferDayAttendanceForbidden,
                "يوجد تحضير أو نقل أو إغلاق يوم للفرد؛ لن يتم تنفيذ الفصل أو المساس بسجله.");
        var snapshot=new JsonObject
        {
            ["personId"]=person.Id,["name"]=person.Name,["serviceNumberA"]=person.Number,
            ["rowVersion"]=person.Revision,["assignmentId"]=person.Assignment,
            ["siteId"]=person.SiteId,["siteName"]=person.SiteName,
            ["businessDate"]=date,["reason"]=reason
        };
        string token=BridgePreviewTokens.Issue(_db,_time,Operation,actor.UserId,snapshot,
            person.Revision,ct);
        var result=(JsonObject)snapshot.DeepClone();
        result["previewToken"]=token;
        result["minimumConfirmationSeconds"]=5;
        return BridgeHelpers.Ok(result);
    }
    public async Task<BridgeResponse> CommitAsync(JsonObject? data,BridgePrincipal actor,CancellationToken ct)
    {
        string token=Text(data,"previewToken");
        if(token.Length<32)return Fail(BridgeErrorCodes.ValidationError,
            "رمز المعاينة مطلوب لإتمام الفصل.");
        return await _db.ExecuteInWriteTransactionAsync(async(c,tx)=>
        {
            // Preserve the existing five-second server-side delay for FIRST use.
            // A retry may read an atomic receipt only after the original
            // successful confirmation has already committed.
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,tx,"CommitSeparation",actor.UserId,data,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            using var read=c.CreateCommand();read.Transaction=tx;
            read.CommandText="""
                SELECT PayloadCanonicalJson,BaseRevision,IssuedAtUtc
                FROM PreviewTokens
                WHERE Token=$token AND Operation=$operation AND ActorUserId=$actor
                  AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
                """;
            read.Parameters.AddWithValue("$token",token);
            read.Parameters.AddWithValue("$operation",Operation);
            read.Parameters.AddWithValue("$actor",actor.UserId);
            read.Parameters.AddWithValue("$now",_time.GetUtcNow().ToString("O"));
            string original;long version;DateTimeOffset issued;
            using(var reader=await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return Fail(BridgeErrorCodes.ConflictStale,"انتهت المعاينة أو استُخدمت؛ أعد المعاينة.");
                original=reader.GetString(0);
                version=reader.GetInt64(1);
                issued=DateTimeOffset.Parse(reader.GetString(2),CultureInfo.InvariantCulture);
            }
            // Server-side enforcement, not just a cosmetic JavaScript countdown.
            if(_time.GetUtcNow()-issued<TimeSpan.FromSeconds(5))
                return Fail(BridgeErrorCodes.ValidationError,"لا يمكن الفصل قبل انتهاء مهلة التأكيد (5 ثوانٍ).");
            if(JsonNode.Parse(original) is not JsonObject preview ||
               Text(preview,"businessDate")!=Today() || version<1)
                return Fail(BridgeErrorCodes.HistoricDayLocked,"المعاينة لا تنتمي ليوم العمل الحالي.");
            string id=Text(preview,"personId"),date=Today();
            var person=await Resolve(c,tx,id,date,ct).ConfigureAwait(false);
            if(person is null || person.Revision!=version ||
               person.Assignment!=Text(preview,"assignmentId") ||
               person.SiteId!=Text(preview,"siteId") ||
               person.Name!=Text(preview,"name") ||
               person.Number!=Text(preview,"serviceNumberA") ||
               await Forbidden(c,tx,id,date,ct).ConfigureAwait(false))
                return Fail(BridgeErrorCodes.ConflictStale,
                    "تغيرت بيانات الفرد أو تحضيره بعد المعاينة؛ ألغي الفصل دون تعديل.");
            long? consumed=await BridgePreviewTokens.ConsumeAsync(_db,_time,c,tx,token,
                Operation,actor.UserId,version,ct).ConfigureAwait(false);
            if(consumed!=version)return Fail(BridgeErrorCodes.ConflictStale,
                "المعاينة غير صالحة أو استُخدمت بالفعل.");
            string movementId="MOV-"+Guid.NewGuid().ToString("N");
            string now=_time.GetUtcNow().ToString("O"),reason=Text(preview,"reason");
            using(var close=c.CreateCommand())
            {
                close.Transaction=tx;
                close.CommandText="""
                    UPDATE PersonAssignments SET ValidToLocalDate=$date
                    WHERE AssignmentId=$assignment AND ValidToLocalDate IS NULL;
                    """;
                close.Parameters.AddWithValue("$date",date);
                close.Parameters.AddWithValue("$assignment",person.Assignment);
                if(await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("Stale membership during separation.");
            }
            using(var update=c.CreateCommand())
            {
                update.Transaction=tx;
                update.CommandText="""
                    UPDATE Persons SET OrganizationalStatus=3,ActiveFlag=0,RowVersion=RowVersion+1
                    WHERE PersonId=$id AND RowVersion=$version AND ActiveFlag=1;
                    """;
                update.Parameters.AddWithValue("$id",id);
                update.Parameters.AddWithValue("$version",version);
                if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("Stale person revision during separation.");
            }
            using(var move=c.CreateCommand())
            {
                move.Transaction=tx;
                move.CommandText="""
                    INSERT INTO PersonnelMovements
                    (MovementId,PersonId,PersonNameSnapshot,ServiceNumberASnapshot,MovementType,
                     SourceSiteId,SourceSiteNameSnapshot,DestinationSiteId,
                     DestinationSiteNameSnapshot,DestinationExternalName,SeparationReason,
                     EffectiveLocalDate,CommittedAtUtc,ActorUserId,IdempotencyKey)
                    VALUES($movement,$person,$name,$number,3,$site,$siteName,
                           NULL,NULL,NULL,$reason,$date,$now,$actor,$idempotency);
                    """;
                move.Parameters.AddWithValue("$movement",movementId);
                move.Parameters.AddWithValue("$person",person.Id);
                move.Parameters.AddWithValue("$name",person.Name);
                move.Parameters.AddWithValue("$number",person.Number);
                move.Parameters.AddWithValue("$site",person.SiteId);
                move.Parameters.AddWithValue("$siteName",person.SiteName);
                move.Parameters.AddWithValue("$reason",reason);
                move.Parameters.AddWithValue("$date",date);
                move.Parameters.AddWithValue("$now",now);
                move.Parameters.AddWithValue("$actor",actor.UserId);
                move.Parameters.AddWithValue("$idempotency",token+":"+person.Id);
                await move.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await BridgeAuditWriter.WriteAsync(c,tx,_time,"PERSON_SEPARATE","فصل فرد",
                actor.UserId,actor.Username,actor.Username,"Person",person.Id,person.Name,
                new JsonObject{["active"]=true,["siteId"]=person.SiteId,
                    ["serviceNumberA"]=person.Number}.ToJsonString(),
                new JsonObject{["active"]=false,["organizationalStatus"]="Separated",
                    ["movementId"]=movementId,["reason"]=reason}.ToJsonString(),
                "فصل الفرد "+person.Name+" بسبب: "+reason,reason,date,
                Guid.NewGuid().ToString("N"),ct).ConfigureAwait(false);
            var responseData=new JsonObject
            {
                ["movementId"]=movementId,["personId"]=person.Id,
                ["name"]=person.Name,["serviceNumberA"]=person.Number,
                ["date"]=date,["separated"]=true
            };
            await MutationIdempotency.StoreAsync(c,tx,receipt,responseData,_time,ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(responseData,events:
                BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonnelMoved,
                    date,[person.Id]));
        },ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> ArchiveAsync(JsonObject? data,CancellationToken ct)
    {
        int page=Math.Clamp(Number(data,"page",1),1,100000);
        int size=Math.Clamp(Number(data,"pageSize",50),1,100);
        string term=Text(data,"term"),site=Text(data,"siteId");
        string like="%"+term.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%";
        const string filter="""
            WHERE m.MovementType=3
              AND ($site='' OR m.SourceSiteId=$site)
              AND (m.PersonNameSnapshot LIKE $term ESCAPE '\'
                   OR m.ServiceNumberASnapshot LIKE $term ESCAPE '\')
            """;
        using var c=_db.CreateOpenConnection();
        using var count=c.CreateCommand();
        count.CommandText="SELECT COUNT(*) FROM PersonnelMovements m "+filter;
        count.Parameters.AddWithValue("$site",site);count.Parameters.AddWithValue("$term",like);
        long total=Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
            SELECT m.MovementId,m.PersonId,m.PersonNameSnapshot,m.ServiceNumberASnapshot,
                   m.SourceSiteNameSnapshot,m.SeparationReason,m.EffectiveLocalDate,
                   m.CommittedAtUtc,m.ActorUserId
            FROM PersonnelMovements m
            """+"\n"+filter+"\n"+"""
            ORDER BY m.EffectiveLocalDate DESC,m.CommittedAtUtc DESC,m.MovementId
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$site",site);cmd.Parameters.AddWithValue("$term",like);
        cmd.Parameters.AddWithValue("$limit",size);
        cmd.Parameters.AddWithValue("$offset",(long)(page-1)*size);
        var items=new JsonArray();
        using var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while(await reader.ReadAsync(ct).ConfigureAwait(false))
            items.Add(new JsonObject
            {
                ["movementId"]=reader.GetString(0),["personId"]=reader.GetString(1),
                ["name"]=reader.GetString(2),["serviceNumberA"]=reader.GetString(3),
                ["siteName"]=reader.GetString(4),["reason"]=reader.GetString(5),
                ["date"]=reader.GetString(6),["committedAtUtc"]=reader.GetString(7),
                ["actorUserId"]=reader.GetString(8)
            });
        return BridgeHelpers.Ok(new JsonObject
        {
            ["items"]=items,["page"]=page,["pageSize"]=size,["total"]=total,
            ["hasNext"]=(long)page*size<total
        });
    }
    public async Task<BridgeResponse> DetailsAsync(JsonObject? data,CancellationToken ct)
    {
        string id=Text(data,"movementId");
        if(id.Length<3)return Fail(BridgeErrorCodes.ValidationError,"حدد قيد الفصل المطلوب.");
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
            SELECT MovementId,PersonId,PersonNameSnapshot,ServiceNumberASnapshot,
                   SourceSiteNameSnapshot,SeparationReason,EffectiveLocalDate,CommittedAtUtc,ActorUserId
            FROM PersonnelMovements WHERE MovementId=$id AND MovementType=3;
            """;
        cmd.Parameters.AddWithValue("$id",id);
        using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(!await r.ReadAsync(ct).ConfigureAwait(false))
            return Fail(BridgeErrorCodes.MissingData,"قيد الفصل غير موجود.");
        return BridgeHelpers.Ok(new JsonObject
        {
            ["movementId"]=r.GetString(0),["personId"]=r.GetString(1),
            ["name"]=r.GetString(2),["serviceNumberA"]=r.GetString(3),
            ["siteName"]=r.GetString(4),["reason"]=r.GetString(5),
            ["date"]=r.GetString(6),["committedAtUtc"]=r.GetString(7),
            ["actorUserId"]=r.GetString(8)
        });
    }
}
