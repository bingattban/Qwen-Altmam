using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Policies;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// 04.09(d): editable roster column metadata. The immutable FieldDefinitionId
/// (ColumnId) is the only key used by CustomFieldValues; TechnicalKey is
/// generated once, never derived from a mutable display heading.
/// System columns are read-only and never deleted/hidden. Custom column
/// retirement preserves every existing value and historical audit event.
/// </summary>
public sealed class RosterColumnStructureService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    public RosterColumnStructureService(AlTamamDbContext db, TimeProvider? time=null)
    {
        _db=db;
        _time=time??TimeProvider.System;
    }

    private sealed record Column(string Id,string Key,string Caption,int Type,int Order,
        bool System,bool Visible,bool Active);
    private static string Read(JsonObject? p,string key)
    {
        try{return p?[key]?.GetValue<string>()?.Trim()??"";}
        catch{return "";}
    }
    private static int Int(JsonObject? p,string key,int fallback=0)
    {
        try{return p?[key]?.GetValue<int>()??fallback;}
        catch{try{return checked((int)(p?[key]?.GetValue<long>()??fallback));}
              catch{return fallback;}}
    }
    private static bool Bool(JsonObject? p,string key,bool fallback=false)
    {
        try{return p?[key]?.GetValue<bool>()??fallback;}catch{return fallback;}
    }
    private static BridgeResponse Invalid(string message,string code=BridgeErrorCodes.ValidationError)
        => BridgeHelpers.Error(code,message);
    private static bool ValidCaption(string value)
        => value.Length is >=2 and <=80 && !value.Any(char.IsControl)
           && ArabicTextNormalizer.NormalizeForSearch(value).Length>0;
    private static bool ValidType(int type)=>type is >=1 and <=4;
    private static string Fingerprint(Column c)
    {
        string data=string.Join("\0",new[]{
            c.Id,c.Key,c.Caption,c.Type.ToString(),c.Order.ToString(),
            c.System?"1":"0",c.Visible?"1":"0",c.Active?"1":"0"
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }
    private static JsonObject AsJson(Column c,long valueCount) =>new()
    {
        ["columnId"]=c.Id,["technicalKey"]=c.Key,["caption"]=c.Caption,
        ["fieldType"]=c.Type,["displayOrder"]=c.Order,
        ["isSystemProtected"]=c.System,["visible"]=c.Visible,
        ["active"]=c.Active,["storedValueCount"]=valueCount,
        ["snapshot"]=Fingerprint(c)
    };
    private static Column FromReader(SqliteDataReader r) =>new(
        r.GetString(0),r.GetString(1),r.GetString(2),
        r.GetInt32(3),r.GetInt32(4),r.GetInt32(5)!=0,
        r.GetInt32(6)!=0,r.GetInt32(7)!=0);
    private static async Task<Column?> GetAsync(
        SqliteConnection c,SqliteTransaction tx,string id,CancellationToken ct)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT FieldDefinitionId,TechnicalKey,ArabicCaption,FieldType,
                   DisplayOrder,IsSystemProtected,VisibleFlag,ActiveFlag
            FROM CustomFieldDefinitions WHERE FieldDefinitionId=$id;
            """;
        cmd.Parameters.AddWithValue("$id",id);
        using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false)?FromReader(r):null;
    }
    private static async Task<bool> DuplicateCaptionAsync(
        SqliteConnection c,SqliteTransaction tx,string caption,string? excludedId,CancellationToken ct)
    {
        string norm=ArabicTextNormalizer.NormalizeForSearch(caption);
        using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT FieldDefinitionId,ArabicCaption
            FROM CustomFieldDefinitions WHERE ActiveFlag=1;
            """;
        using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while(await r.ReadAsync(ct).ConfigureAwait(false))
            if(r.GetString(0)!=excludedId &&
               ArabicTextNormalizer.NormalizeForSearch(r.GetString(1))==norm)
                return true;
        return false;
    }
    private async Task WriteAuditAsync(SqliteConnection c,SqliteTransaction tx,
        BridgePrincipal actor,string operation,Column? before,Column after,
        string? reason,CancellationToken ct)
    {
        JsonObject? old=before is null?null:AsJson(before,0);
        await BridgeAuditWriter.WriteAsync(c,tx,_time,operation,
            operation switch {
                "ROSTER_COLUMN_CREATE"=>"إضافة عمود للكشف",
                "ROSTER_COLUMN_EDIT"=>"تعديل هيكل عمود",
                _=>"تعطيل عمود من الكشف"
            },actor.UserId,actor.Username,actor.Username,
            "CustomField",after.Id,after.Caption,old?.ToJsonString(),
            AsJson(after,0).ToJsonString(),
            "تغيير تعريف العمود حسب ColumnId مستقل عن عنوانه الظاهر.",
            reason,_time.GetLocalNow().ToString("yyyy-MM-dd"),
            ct.CorrelationId(),ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> ListAsync(JsonObject? payload,CancellationToken ct)
    {
        bool inactive=Bool(payload,"includeInactive",false);
        using var conn=_db.CreateOpenConnection();
        using var query=conn.CreateCommand();
        query.CommandText="""
            SELECT f.FieldDefinitionId,f.TechnicalKey,f.ArabicCaption,
                f.FieldType,f.DisplayOrder,f.IsSystemProtected,f.VisibleFlag,f.ActiveFlag,
                (SELECT COUNT(*) FROM CustomFieldValues v
                   WHERE v.FieldDefinitionId=f.FieldDefinitionId
                     AND v.FieldValueText IS NOT NULL AND TRIM(v.FieldValueText)<>'')
            FROM CustomFieldDefinitions f
            WHERE ($inactive=1 OR f.ActiveFlag=1)
            ORDER BY f.ActiveFlag DESC,f.DisplayOrder,f.FieldDefinitionId;
            """;
        query.Parameters.AddWithValue("$inactive",inactive?1:0);
        var result=new JsonArray();
        using var r=await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while(await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if(result.Count>=512)
                return Invalid("تجاوز عدد تعريفات الأعمدة حد العرض. راجع الأعمدة المعطلة.");
            result.Add(AsJson(FromReader(r),r.GetInt64(8)));
        }
        return BridgeHelpers.Ok(new JsonObject{["items"]=result,["total"]=result.Count});
    }

    public async Task<BridgeResponse> CreateAsync(JsonObject? payload,
        BridgePrincipal actor,CancellationToken ct)
    {
        string caption=Read(payload,"caption"),reason=Read(payload,"reason");
        int type=Int(payload,"fieldType",0);
        if(!ValidCaption(caption)||!ValidType(type))
            return Invalid("اكتب عنوان العمود (2–80 حرفًا) وحدد نوعًا صحيحًا: نص أو رقم أو تاريخ أو نعم/لا.");
        if(string.IsNullOrWhiteSpace(Read(payload,"operationId")))
            return Invalid("معرّف الإضافة مطلوب حتى لا يتكرر إنشاء العمود.");
        return await _db.ExecuteInWriteTransactionAsync(async(conn,tx)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                conn,tx,"CreateCustomFieldDefinition",actor.UserId,payload,ct)
                .ConfigureAwait(false);
            if(replay is not null)return replay;
            using var count=conn.CreateCommand();count.Transaction=tx;
            count.CommandText="""
                SELECT COUNT(*) FROM CustomFieldDefinitions
                WHERE IsSystemProtected=0 AND ActiveFlag=1;
                """;
            long active=Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if(active>=64)return Invalid("بلغت الحد الآمن للأعمدة الإضافية (64 عمودًا).");
            if(await DuplicateCaptionAsync(conn,tx,caption,null,ct).ConfigureAwait(false))
                return Invalid("يوجد عمود نشط بعنوان مطابق أو مماثل؛ اختر عنوانًا مختلفًا.");
            using var maxOrder=conn.CreateCommand();maxOrder.Transaction=tx;
            maxOrder.CommandText="SELECT COALESCE(MAX(DisplayOrder),4) FROM CustomFieldDefinitions WHERE ActiveFlag=1;";
            int order=Convert.ToInt32(await maxOrder.ExecuteScalarAsync(ct).ConfigureAwait(false))+1;
            string random=Guid.NewGuid().ToString("N");
            var col=new Column("FLD-"+random,"custom_"+random,caption,type,order,
                false,true,true);
            using var cmd=conn.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO CustomFieldDefinitions(FieldDefinitionId,TechnicalKey,ArabicCaption,
                    FieldType,DisplayOrder,IsSystemProtected,VisibleFlag,ActiveFlag,CreatedAtUtc)
                VALUES($id,$key,$caption,$type,$order,0,1,1,$at);
                """;
            cmd.Parameters.AddWithValue("$id",col.Id);
            cmd.Parameters.AddWithValue("$key",col.Key);
            cmd.Parameters.AddWithValue("$caption",caption);
            cmd.Parameters.AddWithValue("$type",type);
            cmd.Parameters.AddWithValue("$order",order);
            cmd.Parameters.AddWithValue("$at",_time.GetUtcNow().ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await WriteAuditAsync(conn,tx,actor,"ROSTER_COLUMN_CREATE",null,col,reason,ct)
                .ConfigureAwait(false);
            var outcome=AsJson(col,0);
            await MutationIdempotency.StoreAsync(conn,tx,receipt,outcome,_time,ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome);
        },ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> UpdateAsync(JsonObject? payload,
        BridgePrincipal actor,CancellationToken ct)
    {
        string id=Read(payload,"columnId"),caption=Read(payload,"caption"),
            snapshot=Read(payload,"expectedSnapshot"),reason=Read(payload,"reason");
        int type=Int(payload,"fieldType",-1),position=Int(payload,"displayOrder",-1);
        bool visible=Bool(payload,"visible",true);
        if(id.Length is <4 or >128||snapshot.Length!=64||!ValidCaption(caption)||
           !ValidType(type)||position<5||position>1024)
            return Invalid("راجع بيانات العمود وإصداره وترتيبه قبل الحفظ.");
        if(string.IsNullOrWhiteSpace(Read(payload,"operationId")))
            return Invalid("معرّف تعديل العمود مطلوب.");
        return await _db.ExecuteInWriteTransactionAsync(async(conn,tx)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(conn,tx,
                "UpdateCustomFieldMetadata",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            var previous=await GetAsync(conn,tx,id,ct).ConfigureAwait(false);
            if(previous is null||!previous.Active)
                return Invalid("العمود غير موجود أو أصبح معطلاً.",BridgeErrorCodes.ConflictStale);
            if(previous.System)
                return Invalid("الأعمدة الأساسية محمية ولا يمكن تعديل تعريفها.");
            if(Fingerprint(previous)!=snapshot)
                return Invalid("تغيّر تعريف العمود؛ حدّث القائمة قبل حفظ التعديل.",
                    BridgeErrorCodes.ConflictStale);
            using var total=conn.CreateCommand();total.Transaction=tx;
            total.CommandText="""
                SELECT COUNT(*) FROM CustomFieldDefinitions
                WHERE ActiveFlag=1 AND IsSystemProtected=0;
                """;
            int maxPosition=Convert.ToInt32(await total.ExecuteScalarAsync(ct).ConfigureAwait(false))+4;
            if(position>maxPosition)
                return Invalid("موضع العمود يتجاوز عدد الأعمدة المتاحة.");
            if(previous.Caption==caption && previous.Type==type &&
               previous.Order==position && previous.Visible==visible)
                return Invalid("لم تتغير خصائص العمود.");
            if(previous.Caption!=caption &&
               await DuplicateCaptionAsync(conn,tx,caption,id,ct).ConfigureAwait(false))
                return Invalid("عنوان العمود مستخدم مسبقًا في عمود آخر.");
            if(type!=previous.Type)
            {
                using var evidence=conn.CreateCommand();evidence.Transaction=tx;
                evidence.CommandText="""
                    SELECT EXISTS(SELECT 1 FROM CustomFieldValues
                        WHERE FieldDefinitionId=$id
                        AND FieldValueText IS NOT NULL AND TRIM(FieldValueText)<>'');
                    """;
                evidence.Parameters.AddWithValue("$id",id);
                if(Convert.ToInt32(await evidence.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                    return Invalid("العمود يحتوي بيانات محفوظة؛ لا يمكن تغيير نوعها دون ترحيل معتمد يحفظ القيم القديمة.");
            }
            if(position!=previous.Order)
            {
                using var reorder=conn.CreateCommand();reorder.Transaction=tx;
                if(position<previous.Order)
                {
                    reorder.CommandText="""
                        UPDATE CustomFieldDefinitions SET DisplayOrder=DisplayOrder+1
                        WHERE ActiveFlag=1 AND IsSystemProtected=0
                          AND FieldDefinitionId<>$id
                          AND DisplayOrder >= $position AND DisplayOrder < $old;
                        """;
                }
                else
                {
                    reorder.CommandText="""
                        UPDATE CustomFieldDefinitions SET DisplayOrder=DisplayOrder-1
                        WHERE ActiveFlag=1 AND IsSystemProtected=0
                          AND FieldDefinitionId<>$id
                          AND DisplayOrder <= $position AND DisplayOrder > $old;
                        """;
                }
                reorder.Parameters.AddWithValue("$id",id);
                reorder.Parameters.AddWithValue("$position",position);
                reorder.Parameters.AddWithValue("$old",previous.Order);
                await reorder.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            using var update=conn.CreateCommand();update.Transaction=tx;
            update.CommandText="""
                UPDATE CustomFieldDefinitions SET ArabicCaption=$caption,
                  FieldType=$type,DisplayOrder=$position,VisibleFlag=$visible
                WHERE FieldDefinitionId=$id AND ActiveFlag=1 AND IsSystemProtected=0;
                """;
            update.Parameters.AddWithValue("$caption",caption);
            update.Parameters.AddWithValue("$type",type);
            update.Parameters.AddWithValue("$position",position);
            update.Parameters.AddWithValue("$visible",visible?1:0);
            update.Parameters.AddWithValue("$id",id);
            if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                throw new InvalidOperationException("تغير العمود أثناء الحفظ، تراجعت العملية كاملة.");
            var next=previous with{
                Caption=caption,Type=type,Order=position,Visible=visible
            };
            await WriteAuditAsync(conn,tx,actor,"ROSTER_COLUMN_EDIT",previous,next,reason,ct)
                .ConfigureAwait(false);
            var outcome=AsJson(next,0);
            await MutationIdempotency.StoreAsync(conn,tx,receipt,outcome,_time,ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome);
        },ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> DeactivateAsync(JsonObject? payload,
        BridgePrincipal actor,CancellationToken ct)
    {
        string id=Read(payload,"columnId"),snapshot=Read(payload,"expectedSnapshot");
        if(id.Length is <4 or >128||snapshot.Length!=64||
           !Bool(payload,"confirmed",false)||
           string.IsNullOrWhiteSpace(Read(payload,"operationId")))
            return Invalid("حدد العمود وإصداره وأكّد تعطيله صراحة.");
        return await _db.ExecuteInWriteTransactionAsync(async(conn,tx)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(conn,tx,
                "DeactivateCustomField",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            var previous=await GetAsync(conn,tx,id,ct).ConfigureAwait(false);
            if(previous is null||!previous.Active)
                return Invalid("العمود أصبح معطلاً أو غير موجود.",BridgeErrorCodes.ConflictStale);
            if(previous.System)return Invalid("لا يجوز حذف أو تعطيل الأعمدة الأساسية.");
            if(Fingerprint(previous)!=snapshot)
                return Invalid("تغيّر تعريف العمود منذ عرضه؛ حدّثه قبل التعطيل.",
                    BridgeErrorCodes.ConflictStale);
            using var cmd=conn.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE CustomFieldDefinitions SET ActiveFlag=0,VisibleFlag=0
                WHERE FieldDefinitionId=$id AND ActiveFlag=1 AND IsSystemProtected=0;
                UPDATE CustomFieldDefinitions SET DisplayOrder=DisplayOrder-1
                WHERE ActiveFlag=1 AND IsSystemProtected=0 AND DisplayOrder>$old;
                """;
            cmd.Parameters.AddWithValue("$id",id);
            cmd.Parameters.AddWithValue("$old",previous.Order);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var next=previous with{Active=false,Visible=false};
            await WriteAuditAsync(conn,tx,actor,"ROSTER_COLUMN_DEACTIVATE",
                previous,next,Read(payload,"reason"),ct).ConfigureAwait(false);
            var result=AsJson(next,0);
            await MutationIdempotency.StoreAsync(conn,tx,receipt,result,_time,ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(result);
        },ct).ConfigureAwait(false);
    }
}
