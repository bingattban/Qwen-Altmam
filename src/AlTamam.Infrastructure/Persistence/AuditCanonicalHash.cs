using System.Security.Cryptography;
using System.Text;

namespace AlTamam.Infrastructure.Persistence;

/// <summary>
/// One canonical SHA-256 chain encoding shared by normal domain audit writes
/// and the forward-only founding-account migration. Never alter field order
/// without an explicitly versioned audit-chain migration.
/// </summary>
public static class AuditCanonicalHash
{
    public static string Compute(
        string operationCode, string operationArabicName,
        string actorUserId, string actorUsername, string? actorFullName,
        string targetType, string targetId, string targetDisplay,
        string? oldValueJson, string? newValueJson, string description,
        string? reason, string effectiveLocalDate, string executedAtUtc,
        string correlationId, string priorHash)
    {
        string body = string.Join('\n',
            operationCode, operationArabicName, actorUserId, actorUsername,
            actorFullName ?? "", targetType, targetId, targetDisplay,
            oldValueJson ?? "", newValueJson ?? "", description, reason ?? "",
            effectiveLocalDate, executedAtUtc, correlationId, priorHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();
    }
}
