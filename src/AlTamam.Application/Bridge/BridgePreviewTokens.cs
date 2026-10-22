using System.Security.Cryptography;
using System.Text;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Server-side one-time preview tokens that turn destructive or identity-changing actions into a
/// genuine two-step flow (Phase 05). A token binds the exact operation, actor, canonical payload and
/// base revision captured at preview time; commits must present a live, unconsumed token issued to
/// the same actor for the same operation — stale previews and forged commits are rejected.
/// </summary>
public static class BridgePreviewTokens
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    /// <summary>Issues a token inside its own write transaction and returns it in hex form.</summary>
    public static string Issue(
        AlTamamDbContext db,
        TimeProvider time,
        string operation,
        string? actorUserId,
        object payloadForCanonicalization,
        long baseRevision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        string canonical = Canonicalize(payloadForCanonicalization);
        byte[] random = RandomNumberGenerator.GetBytes(16);
        string raw = $"{operation}|{actorUserId ?? ""}|{canonical}|{baseRevision}|{time.GetUtcNow():O}|{Convert.ToHexString(random)}";
        string token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        DateTimeOffset now = time.GetUtcNow();

        db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO PreviewTokens (Token, Operation, ActorUserId, PayloadCanonicalJson, BaseRevision,
                                          SummaryJson, IssuedAtUtc, ExpiresAtUtc, ConsumedFlag)
                VALUES ($t, $op, $actor, $payload, $rev, $summary, $now, $exp, 0);
                """;
            cmd.Parameters.AddWithValue("$t", token);
            cmd.Parameters.AddWithValue("$op", operation);
            cmd.Parameters.AddWithValue("$actor", actorUserId ?? "");
            cmd.Parameters.AddWithValue("$payload", canonical);
            cmd.Parameters.AddWithValue("$rev", baseRevision);
            cmd.Parameters.AddWithValue("$summary", "{}");
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            cmd.Parameters.AddWithValue("$exp", now.Add(DefaultTtl).ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return token;
        }, ct).GetAwaiter().GetResult();

        return token;
    }

    /// <summary>
    /// Atomically consumes a presented token: it must exist, match the operation and actor,
    /// carry the same base revision, be unexpired and unconsumed. Returns null when consumption fails.
    /// </summary>
    public static async Task<long?> ConsumeAsync(
        AlTamamDbContext db,
        TimeProvider time,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? token,
        string operation,
        string actorUserId,
        long expectedBaseRevision,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = """
            SELECT BaseRevision, ExpiresAtUtc, ConsumedFlag FROM PreviewTokens
            WHERE Token = $t AND Operation = $op AND ActorUserId = $actor;
            """;
        find.Parameters.AddWithValue("$t", token);
        find.Parameters.AddWithValue("$op", operation);
        find.Parameters.AddWithValue("$actor", actorUserId);
        using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null; // unknown token, wrong operation, or issued to a different actor
        long baseRevision = reader.GetInt64(0);
        string expires = reader.GetString(1);
        bool consumed = Convert.ToInt32(reader.GetValue(2)) == 1;
        reader.Close();
        if (consumed) return -1; // replayed commit
        if (DateTimeOffset.Parse(expires, null, System.Globalization.DateTimeStyles.AdjustToUniversal) <= time.GetUtcNow())
            return -2; // expired preview
        if (baseRevision != expectedBaseRevision) return -3; // stale preview

        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "UPDATE PreviewTokens SET ConsumedFlag = 1 WHERE Token = $t AND ConsumedFlag = 0;";
        mark.Parameters.AddWithValue("$t", token);
        int touched = await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return touched == 1 ? baseRevision : -1;
    }

    /// <summary>Deterministic key-sorted JSON rendering used as the token's payload fingerprint.</summary>
    public static string Canonicalize(object payload)
    {
        string json = payload is System.Text.Json.Nodes.JsonNode node
            ? node.ToJsonString()
            : System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var sb = new StringBuilder();
        WriteCanonical(doc.RootElement, sb);
        return sb.ToString();
    }

    private static void WriteCanonical(System.Text.Json.JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                sb.Append('{');
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sb.Append('"').Append(prop.Name).Append("\":");
                    WriteCanonical(prop.Value, sb);
                    sb.Append(',');
                }
                TrimTrailing(sb);
                sb.Append('}');
                break;
            case System.Text.Json.JsonValueKind.Array:
                sb.Append('[');
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, sb);
                    sb.Append(',');
                }
                TrimTrailing(sb);
                sb.Append(']');
                break;
            default:
                sb.Append(element.GetRawText());
                break;
        }
    }

    private static void TrimTrailing(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] == ',') sb.Length--;
    }
}
