using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Identity;

/// <summary>First-run authority comes from durable SQLCipher state, never UI flags.
/// The founding audit event survives legacy deletion of the Users row;
/// the explicitly confirmed whole-database reset deletes both sources.</summary>
public static class FoundingProvisioningGuard
{
    public static async Task<bool> HasExistingEvidenceAsync(
        SqliteConnection connection, SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM Users
                UNION ALL
                SELECT 1 FROM AuditEvents WHERE OperationCode='FOUNDING_ADMIN_CREATED'
            );
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }
}
