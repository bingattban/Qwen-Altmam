using Microsoft.Data.Sqlite;

namespace AlTamam.Infrastructure.Audit;

/// <summary>Read-only access to immutable evidence and its latest overlay.</summary>
public sealed record AuditPresentationEvidence(
    string RawDescription,long OverlayVersion,bool IsTombstoned,string? CorrectedDescription);

public static class AuditPresentationEvidenceReader
{
    public static async Task<AuditPresentationEvidence?> ReadAsync(
        SqliteConnection connection,SqliteTransaction transaction,
        string auditEventId,CancellationToken ct)
    {
        using var cmd=connection.CreateCommand();
        cmd.Transaction=transaction;
        cmd.CommandText="""
            SELECT e.DefaultArabicDescription,COALESCE(o.OverlayVersion,0),
                   COALESCE(o.IsTombstoned,0),o.CorrectedDescription
            FROM AuditEvents e
            LEFT JOIN AuditLogPresentationEdits o ON o.AuditEventId=e.AuditEventId
                AND o.OverlayVersion=(SELECT MAX(last.OverlayVersion)
                    FROM AuditLogPresentationEdits last
                    WHERE last.AuditEventId=e.AuditEventId)
            WHERE e.AuditEventId=$id;
            """;
        cmd.Parameters.AddWithValue("$id",auditEventId);
        using var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(!await reader.ReadAsync(ct).ConfigureAwait(false))return null;
        return new AuditPresentationEvidence(reader.GetString(0),reader.GetInt64(1),
            reader.GetInt64(2)==1,reader.IsDBNull(3)?null:reader.GetString(3));
    }
}
