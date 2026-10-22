using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>Helpers shared by bridge services: correlation ids and canonical-envelope conveniences.</summary>
internal static class BridgeCorrelationExtensions
{
    /// <summary>A stable per-call correlation id for audit chaining (cancellation tokens carry no identity).</summary>
    internal static string CorrelationId(this CancellationToken _) => Guid.NewGuid().ToString("N");
}


/// <summary>
/// Append-only audit writer used by every committed domain mutation. Events are chained with a
/// SHA-256 over the canonical event body plus the prior event's hash, so the ledger is tamper-evident.
/// Runs inside the caller's transaction — an audit row and its fact commit exactly together or not at all.
/// </summary>
public static class BridgeAuditWriter
{
    /// <summary>Writes one immutable AuditEvents row inside the given transaction.</summary>
    public static async Task WriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeProvider time,
        string operationCode,
        string operationArabicName,
        string actorUserId,
        string actorUsername,
        string? actorDisplayName,
        string targetEntityType,
        string targetEntityId,
        string targetDisplay,
        string? oldValueJson,
        string? newValueJson,
        string description,
        string? reason,
        string effectiveLocalDate,
        string correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        DateTimeOffset nowUtc = time.GetUtcNow();
        string auditEventId = "AUD-" + Guid.NewGuid().ToString("N");

        using var priorCmd = connection.CreateCommand();
        priorCmd.Transaction = transaction;
        priorCmd.CommandText = "SELECT EventHash FROM AuditEvents ORDER BY ExecutedAtUtc DESC, AuditEventId DESC LIMIT 1;";
        string priorHash = await priorCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? "GENESIS";

        string body = string.Join('\n',
            operationCode, operationArabicName, actorUserId, actorUsername, actorDisplayName ?? "",
            targetEntityType, targetEntityId, targetDisplay, oldValueJson ?? "", newValueJson ?? "",
            description, reason ?? "", effectiveLocalDate, nowUtc.ToString("O"), correlationId, priorHash);
        string eventHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO AuditEvents (AuditEventId, OperationCode, OperationArabicName, ActorUserId, ActorPersonId,
                                     ActorUsernameSnapshot, ActorRosterFullNameSnapshot, TargetEntityType, TargetEntityId,
                                     TargetDisplaySnapshot, OldValueJson, NewValueJson, DefaultArabicDescription, Reason,
                                     EffectiveLocalDate, ExecutedAtUtc, CorrelationId, EventHash, PriorEventHash)
            VALUES ($id, $code, $arName, $actor, NULL, $username, $display, $tType, $tId, $tDisplay,
                    $old, $new, $desc, $reason, $eff, $now, $corr, $hash, $prior);
            """;
        insert.Parameters.AddWithValue("$id", auditEventId);
        insert.Parameters.AddWithValue("$code", operationCode);
        insert.Parameters.AddWithValue("$arName", operationArabicName);
        insert.Parameters.AddWithValue("$actor", actorUserId);
        insert.Parameters.AddWithValue("$username", actorUsername);
        insert.Parameters.AddWithValue("$display", (object?)actorDisplayName ?? DBNull.Value);
        insert.Parameters.AddWithValue("$tType", targetEntityType);
        insert.Parameters.AddWithValue("$tId", targetEntityId);
        insert.Parameters.AddWithValue("$tDisplay", targetDisplay);
        insert.Parameters.AddWithValue("$old", (object?)oldValueJson ?? DBNull.Value);
        insert.Parameters.AddWithValue("$new", (object?)newValueJson ?? DBNull.Value);
        insert.Parameters.AddWithValue("$desc", description);
        insert.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        insert.Parameters.AddWithValue("$eff", effectiveLocalDate);
        insert.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        insert.Parameters.AddWithValue("$corr", correlationId);
        insert.Parameters.AddWithValue("$hash", eventHash);
        insert.Parameters.AddWithValue("$prior", priorHash);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Convenience: serializes a JsonObject snapshot pair for old/new value columns.</summary>
    public static string? Json(JsonObject? value) => value?.ToJsonString();
}
