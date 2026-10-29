using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// ACID replay receipts for mutating requests. The operation ID belongs to an
/// explicit user intent, not to the generated personnel number/DB primary key.
/// Only successful writes are receipted, in the SAME SQLCipher transaction.
/// Legacy clients without an operationId keep their existing validation path;
/// the WebView2 forms supply operationId for new creates.
/// </summary>
internal static class MutationIdempotency
{
    internal sealed record Context(string Actor,string OperationId,string Action,string Fingerprint);

    // Exactly the same syntax is checked at the authenticated bridge boundary
    // and by services storing SQLCipher ACID receipts.
    internal static bool HasValidOperationId(JsonObject? payload)
    {
        try
        {
            string id = payload?["operationId"]?.GetValue<string>() ?? "";
            return id.Length is >=16 and <=128
                && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_');
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    internal static async Task<(BridgeResponse? EarlyResponse,Context? Receipt)> StartAsync(
        SqliteConnection c,SqliteTransaction tx,string action,string actor,
        JsonObject? payload,CancellationToken ct)
    {
        string operationId;
        try {operationId=payload?["operationId"]?.GetValue<string>()?.Trim()??"";}
        catch {return (BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
            "معرف عملية الإنشاء غير صالح."),null);}
        if(operationId.Length==0)return (null,null);
        if(operationId.Length is <16 or >128 ||
           operationId.Any(ch=>!char.IsAsciiLetterOrDigit(ch)&&ch!='-'&&ch!='_'))
            return (BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "يجب أن يكون معرف العملية من 16 إلى 128 حرفاً لاتينياً أو رقماً."),null);
        var content=(JsonObject)(payload?.DeepClone()??new JsonObject());
        content.Remove("operationId");
        string normalized=BridgePreviewTokens.Canonicalize(content);
        string fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        using var find=c.CreateCommand();
        find.Transaction=tx;
        find.CommandText="""
            SELECT ActionName,PayloadSha256,ResultJson
            FROM MutationReceipts
            WHERE ActorUserId=$actor AND OperationId=$id;
            """;
        find.Parameters.AddWithValue("$actor",actor);
        find.Parameters.AddWithValue("$id",operationId);
        using var reader=await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string actionSaved=reader.GetString(0),fingerprintSaved=reader.GetString(1),
                result=reader.GetString(2);
            if(actionSaved!=action || fingerprintSaved!=fingerprint)
                return (BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "معرف العملية مستخدم سابقاً مع إجراء أو بيانات مختلفة."),null);
            return (BridgeHelpers.Ok(JsonNode.Parse(result)),null);
        }
        return (null,new Context(actor,operationId,action,fingerprint));
    }

    internal static async Task StoreAsync(SqliteConnection c,SqliteTransaction tx,
        Context? receipt,JsonObject result,TimeProvider time,CancellationToken ct)
    {
        if(receipt is null)return;
        using var cmd=c.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO MutationReceipts(ActorUserId,OperationId,ActionName,
                PayloadSha256,ResultJson,RecordedAtUtc)
            VALUES($actor,$id,$action,$sha,$json,$at);
            """;
        cmd.Parameters.AddWithValue("$actor",receipt.Actor);
        cmd.Parameters.AddWithValue("$id",receipt.OperationId);
        cmd.Parameters.AddWithValue("$action",receipt.Action);
        cmd.Parameters.AddWithValue("$sha",receipt.Fingerprint);
        cmd.Parameters.AddWithValue("$json",result.ToJsonString());
        cmd.Parameters.AddWithValue("$at",time.GetUtcNow().ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
