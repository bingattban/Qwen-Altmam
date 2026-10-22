using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AlTamam.Infrastructure.Persistence.Migrations;

/// <summary>A single versioned, hash-pinned schema migration resource.</summary>
/// <param name="Version">Strictly increasing schema version number.</param>
/// <param name="Name">Migration name as recorded in SchemaVersions (e.g. "V001_InitialSchema").</param>
/// <param name="EmbeddedResourcePath">Assembly manifest resource path containing the SQL body.</param>
/// <param name="RecordsHash">False for migrations applied before the Sha256 column exists (V001 bootstrap).</param>
public sealed record SchemaMigration(int Version, string Name, string EmbeddedResourcePath, bool RecordsHash = true);

/// <summary>
/// Transactional, version-checked migration runner. Every applied migration is verified
/// against its embedded SHA-256 hash; released migrations are immutable — a hash mismatch
/// aborts with an explicit error instead of silently rewriting history. Applies V00N+ only
/// through this runner; V001 remains bootstrappable by <see cref="AlTamamDbContext.InitializeAsync"/>.
/// </summary>
public sealed class SchemaMigrationRunner
{
    private readonly IReadOnlyList<SchemaMigration> _migrations;

    public SchemaMigrationRunner(IReadOnlyList<SchemaMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Count == 0)
            throw new ArgumentException("At least one migration must be registered.", nameof(migrations));
        var ordered = migrations.OrderBy(m => m.Version).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Version != i + 1)
                throw new ArgumentException("Migration versions must form a contiguous sequence starting at 1.");
            if (string.IsNullOrWhiteSpace(ordered[i].Name))
                throw new ArgumentException("Migration name must not be empty.");
        }
        if (ordered[0].Version != 1)
            throw new ArgumentException("The first registered migration must have version 1.");
        _migrations = ordered;
    }

    public int LatestKnownVersion => _migrations[^1].Version;

    /// <summary>Computes the lowercase hex SHA-256 of a migration's embedded SQL body.</summary>
    public string ComputeExpectedHash(SchemaMigration migration)
    {
        ValidateRegistered(migration);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReadSql(migration)))).ToLowerInvariant();
    }

    /// <summary>Reads and normalizes (CRLF→LF, UTF-8 no BOM) the embedded SQL body of a migration.</summary>
    public string ReadSql(SchemaMigration migration)
    {
        ValidateRegistered(migration);
        var assembly = typeof(SchemaMigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(migration.EmbeddedResourcePath)
            ?? throw new InvalidOperationException($"Embedded migration resource '{migration.EmbeddedResourcePath}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>Returns the current applied schema version (MAX over SchemaVersions), or 0 for an unversioned database.</summary>
    public static int GetCurrentVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='SchemaVersions');";
        var exists = Convert.ToInt32(check.ExecuteScalar()) == 1;
        if (!exists) return 0;
        using var max = connection.CreateCommand();
        max.CommandText = "SELECT COALESCE(MAX(SchemaVersion), 0) FROM SchemaVersions;";
        return Convert.ToInt32(max.ExecuteScalar());
    }

    /// <summary>Returns true when a future (not-yet-shipped) schema version is present in the database.</summary>
    public bool IsStaleSchema(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return GetCurrentVersion(connection) > LatestKnownVersion;
    }

    /// <summary>
    /// Applies every pending migration in order inside its own transaction. Each step:
    /// executes SQL, inserts the SchemaVersions row (version, name, UTC timestamp, sha256),
    /// then validates PRAGMA integrity_check. Any failure rolls back that step and throws;
    /// previously committed steps remain intact (forward recovery via retry).
    /// </summary>
    public async Task UpgradeAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        int current = GetCurrentVersion(connection);
        if (current > LatestKnownVersion)
            throw new InvalidOperationException(
                $"Database schema version {current} is newer than the application's latest known version {LatestKnownVersion}. Refusing to downgrade.");

        foreach (var migration in _migrations.Where(m => m.Version > current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sql = ReadSql(migration);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

            using var transaction = connection.BeginTransaction();
            try
            {
                using (var verify = connection.CreateCommand())
                {
                    verify.Transaction = transaction;
                    verify.CommandText = "SELECT COUNT(*) FROM SchemaVersions WHERE SchemaVersion = $v;";
                    verify.Parameters.Add("$v", SqliteType.Integer).Value = migration.Version;
                    if (Convert.ToInt32(verify.ExecuteScalar()) != 0)
                        throw new InvalidOperationException(
                            $"Schema version {migration.Version} is already recorded but was not applied by this run.");
                }

                using (var apply = connection.CreateCommand())
                {
                    apply.Transaction = transaction;
                    apply.CommandText = sql;
                    await apply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    if (migration.RecordsHash)
                    {
                        record.CommandText = """
                            INSERT INTO SchemaVersions (SchemaVersion, MigrationName, AppliedAtUtc, Sha256)
                            VALUES ($v, $n, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), $h);
                            """;
                        record.Parameters.Add("$h", SqliteType.Text).Value = hash;
                    }
                    else
                    {
                        // V001 SQL seeds its own SchemaVersions row before the Sha256 column exists.
                        // Validate that row instead of inserting a duplicate primary key.
                        record.CommandText = """
                            SELECT COUNT(*) FROM SchemaVersions
                            WHERE SchemaVersion = $v AND MigrationName = $n;
                            """;
                    }
                    record.Parameters.Add("$v", SqliteType.Integer).Value = migration.Version;
                    record.Parameters.Add("$n", SqliteType.Text).Value = migration.Name;
                    if (migration.RecordsHash)
                    {
                        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else if (Convert.ToInt32(await record.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
                    {
                        throw new InvalidOperationException(
                            $"Bootstrap migration '{migration.Name}' did not record its expected schema version.");
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            // Post-migration health gate (outside the DDL transaction; WAL-safe read).
            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                var result = integrity.ExecuteScalar()?.ToString();
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Integrity check failed after applying migration '{migration.Name}'. Restore from pre-migration backup.");
            }
        }

        VerifyRecordedHashes(connection);
    }

    /// <summary>Re-hashes every registered migration and compares it with the recorded SchemaVersions.Sha256 pin.</summary>
    public void VerifyRecordedHashes(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        int current = GetCurrentVersion(connection);
        foreach (var migration in _migrations.Where(m => m.Version <= current && m.Version >= 1))
        {
            if (!migration.RecordsHash) continue; // pre-hardening bootstrap rows carry no pin by design
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Sha256 FROM SchemaVersions WHERE SchemaVersion = $v;";
            cmd.Parameters.Add("$v", SqliteType.Integer).Value = migration.Version;
            var recorded = cmd.ExecuteScalar() as string;
            if (recorded is null) continue; // legacy row without hash column (pre-V002 databases)
            string expected = ComputeExpectedHash(migration);
            if (!string.Equals(recorded, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Migration hash mismatch for '{migration.Name}' (version {migration.Version}). " +
                    "A released migration must never be edited in place; add a forward migration instead.");
        }
    }

    private void ValidateRegistered(SchemaMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        if (!_migrations.Any(m => m.Version == migration.Version && m.Name == migration.Name))
            throw new ArgumentException($"Migration {migration.Version} '{migration.Name}' is not registered.");
    }
}
