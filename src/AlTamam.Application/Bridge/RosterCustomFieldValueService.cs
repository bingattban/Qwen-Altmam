using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// The only authorized writer of per-person custom column values.
/// ColumnId and PersonId are immutable keys. Each change increments the
/// person's revision and produces hash-chained, immutable audit evidence.
/// </summary>
public sealed class RosterCustomFieldValueService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _clock;
    public RosterCustomFieldValueService(AlTamamDbContext db, TimeProvider? clock=null)
    {
        _db=db;
        _clock=clock??TimeProvider.System;
    }

    /// <summary>Canonicalizes values without accepting spreadsheet formula syntax.</summary>
    internal static bool TryCanonicalize(string? input,int kind,out string result)
    {
        result=(input??"").Trim();
        if(result.Length==0) return true; // explicit clearing of an existing value
        if(result.Length>250 || result.Any(char.IsControl)) return false;
        switch(kind)
        {
            case 1: return true;
            case 2:
                result=string.Concat(result.Select(c=>c switch
                {
                    >= '\u0660' and <= '\u0669' => (char)('0'+c-'\u0660'),
                    >= '\u06F0' and <= '\u06F9' => (char)('0'+c-'\u06F0'),
                    _ => c
                }));
                if(result.Length>64 || !System.Text.RegularExpressions.Regex.IsMatch(
                    result,@"^[+-]?[0-9]+(?:\.[0-9]+)?$",System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    return false;
                if(!decimal.TryParse(result,NumberStyles.AllowLeadingSign|
                    NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out decimal number))
                    return false;
                result=number.ToString("0.############################",CultureInfo.InvariantCulture);
                return true;
            case 3:
                if(!DateOnly.TryParseExact(result,"yyyy-MM-dd",CultureInfo.InvariantCulture,
                       DateTimeStyles.None,out var date))return false;
                result=date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
                return true;
            case 4:
                if(result is "نعم" or "true" or "TRUE" or "1") {result="نعم";return true;}
                if(result is "لا" or "false" or "FALSE" or "0") {result="لا";return true;}
                return false;
            default:return false;
        }
    }

    public async Task<BridgeResponse> UpdateAsync(JsonObject? payload,
        BridgePrincipal actor,CancellationToken ct)
    {
        string personId="";
        long expected=0;
        JsonObject? values=null;
        try
        {
            personId=payload?["personId"]?.GetValue<string>()?.Trim()??"";
            try{expected=payload?["expectedRevision"]?.GetValue<long>()??0;}
            catch{expected=payload?["expectedRevision"]?.GetValue<int>()??0;}
            values=payload?["values"] as JsonObject;
        }
        catch(InvalidOperationException)
        {
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "بيانات التعديل أو إصدار الفرد غير صالح.");
        }
        if(personId.Length is <2 or >128 || expected<1 ||
           values is null || values.Count is <1 or >64 ||
           string.IsNullOrWhiteSpace(payload?["operationId"]?.ToString()))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "اختر فرداً وأعمدة صالحة ومعرف عملية جديد.");
        return await _db.ExecuteInWriteTransactionAsync(async(c,tx)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(c,tx,
                "UpdateCustomFieldValues",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            var result=await SaveInTransactionAsync(c,tx,actor.UserId,actor.Username,personId,expected,
                values,"تعديل بيانات الأعمدة الإضافية من شاشة إدارة القوة",ct)
                .ConfigureAwait(false);
            if(!result.Success)return result;
            await MutationIdempotency.StoreAsync(c,tx,receipt,
                (JsonObject)result.Data!,_clock,ct).ConfigureAwait(false);
            return result;
        },ct).ConfigureAwait(false);
    }

    internal static async Task<BridgeResponse> SaveInTransactionAsync(
        SqliteConnection c,SqliteTransaction tx,string actorUserId,string actorUsername,
        string personId,long expectedRevision,JsonObject values,
        string reason,CancellationToken ct,TimeProvider? clock=null)
    {
        TimeProvider now=clock??TimeProvider.System;
        if(values.Count is <1 or >64)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "يجب تحديد عمود إضافي واحد على الأقل (حتى 64 عموداً).");
        string personName;
        using(var person=c.CreateCommand())
        {
            person.Transaction=tx;
            person.CommandText="""
                SELECT CurrentVisibleName FROM Persons
                WHERE PersonId=$id AND RowVersion=$revision
                  AND ActiveFlag=1 AND OrganizationalStatus=1;
                """;
            person.Parameters.AddWithValue("$id",personId);
            person.Parameters.AddWithValue("$revision",expectedRevision);
            personName=(await person.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string??"";
        }
        if(personName.Length==0)return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
            "تغيرت بيانات الفرد أو لم يعد ضمن القوة النشطة؛ حدث الكشف وأعد المحاولة.");

        var updates=new List<(string Id,string Caption,string? Old,string New)>();
        foreach(var (fieldId,node) in values)
        {
            ct.ThrowIfCancellationRequested();
            string raw;
            try{raw=node?.GetValue<string>()??"";}
            catch{return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "قيمة العمود يجب أن تكون نصا أو فراغا واضحا.");}
            using var q=c.CreateCommand();
            q.Transaction=tx;
            q.CommandText="""
                SELECT d.ArabicCaption,d.FieldType,
                    (SELECT v.FieldValueText FROM CustomFieldValues v
                     WHERE v.PersonId=$person AND v.FieldDefinitionId=d.FieldDefinitionId)
                FROM CustomFieldDefinitions d
                WHERE d.FieldDefinitionId=$field AND d.IsSystemProtected=0
                  AND d.ActiveFlag=1 AND d.VisibleFlag=1;
                """;
            q.Parameters.AddWithValue("$field",fieldId);
            q.Parameters.AddWithValue("$person",personId);
            using var reader=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if(!await reader.ReadAsync(ct).ConfigureAwait(false))
                return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                    "العمود غير موجود أو مخفي أو معطل؛ أعد تحميل الكشف.");
            string caption=reader.GetString(0);
            int type=reader.GetInt32(1);
            string? prior=reader.IsDBNull(2)?null:reader.GetString(2);
            if(!TryCanonicalize(raw,type,out string normalized))
                return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                    "قيمة العمود «"+caption+"» لا تطابق نوعه المحفوظ.");
            if(!string.Equals(prior??"",normalized,StringComparison.Ordinal))
                updates.Add((fieldId,caption,prior,normalized));
        }
        if(updates.Count==0)return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
            "لم تتغير أي قيمة في الأعمدة الإضافية.");

        using(var revision=c.CreateCommand())
        {
            revision.Transaction=tx;
            revision.CommandText="""
                UPDATE Persons SET RowVersion=RowVersion+1
                WHERE PersonId=$id AND RowVersion=$revision AND ActiveFlag=1;
                """;
            revision.Parameters.AddWithValue("$id",personId);
            revision.Parameters.AddWithValue("$revision",expectedRevision);
            if(await revision.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "تغير إصدار الفرد أثناء حفظ الأعمدة؛ لم تُحفظ أي قيمة.");
        }
        foreach(var update in updates)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx;
                cmd.CommandText="""
                    INSERT INTO CustomFieldValues
                        (PersonId,FieldDefinitionId,FieldValueText,UpdatedAtUtc)
                    VALUES($person,$field,$value,$at)
                    ON CONFLICT(PersonId,FieldDefinitionId) DO UPDATE
                    SET FieldValueText=excluded.FieldValueText,UpdatedAtUtc=excluded.UpdatedAtUtc;
                    """;
                cmd.Parameters.AddWithValue("$person",personId);
                cmd.Parameters.AddWithValue("$field",update.Id);
                cmd.Parameters.AddWithValue("$value",update.New);
                cmd.Parameters.AddWithValue("$at",now.GetUtcNow().ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await BridgeAuditWriter.WriteAsync(c,tx,now,
                "CUSTOM_FIELD_VALUE_UPDATE","تعديل قيمة عمود إضافي",
                actorUserId,actorUsername,actorUsername,"Person",personId,personName,
                new JsonObject {["columnId"]=update.Id,["column"]=update.Caption,
                    ["value"]=update.Old??""}.ToJsonString(),
                new JsonObject {["columnId"]=update.Id,["column"]=update.Caption,
                    ["value"]=update.New}.ToJsonString(),
                "تعديل قيمة «"+update.Caption+"» للفرد «"+personName+"».",
                reason,now.GetLocalNow().ToString("yyyy-MM-dd"),
                Guid.NewGuid().ToString("N"),ct).ConfigureAwait(false);
        }
        return BridgeHelpers.Ok(new JsonObject{
            ["personId"]=personId,["revision"]=expectedRevision+1,
            ["changedCount"]=updates.Count
        },revision:expectedRevision+1,
        events:BridgeHelpers.InvalidationEvent(
            UiInvalidationEvents.PersonIdentityUpdated,
            now.GetLocalNow().ToString("yyyy-MM-dd"),[personId]));
    }
}
