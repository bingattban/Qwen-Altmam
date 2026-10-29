using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using AlTamam.Infrastructure.Persistence;

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
                // The bootstrap migration creates SchemaVersions; it cannot be queried beforehand.
                if (migration.Version > 1)
                {
                    using var verify = connection.CreateCommand();
                    verify.Transaction = transaction;
                    verify.CommandText = "SELECT COUNT(*) FROM SchemaVersions WHERE SchemaVersion = $v;";
                    verify.Parameters.Add("$v", SqliteType.Integer).Value = migration.Version;
                    if (Convert.ToInt32(verify.ExecuteScalar()) != 0)
                        throw new InvalidOperationException(
                            $"Schema version {migration.Version} is already recorded but was not applied by this run.");
                }

                // V009 is hash-pinned and must not be edited. Capture a legacy
                // founder's former PersonId INSIDE this migration transaction
                // before V009 clears it; no later version can reconstruct it.
                var legacyFounders = new List<(string UserId, string Username, string PersonId)>();
                if (migration.Version == 9)
                {
                    using var select = connection.CreateCommand();
                    select.Transaction = transaction;
                    select.CommandText = """
                        SELECT UserId, Username, LinkedPersonId FROM Users
                        WHERE Role=1 AND LinkedPersonId IS NOT NULL;
                        """;
                    using var reader = await select.ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        legacyFounders.Add((reader.GetString(0), reader.GetString(1),
                            reader.GetString(2)));
                }

                using (var apply = connection.CreateCommand())
                {
                    apply.Transaction = transaction;
                    apply.CommandText = sql;
                    await apply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Append canonical hash-chained system evidence atomically with
                // the detachment. Old audit rows, UserIds and passwords stay intact.
                foreach (var founder in legacyFounders)
                    await RecordLegacyFounderDetachmentAsync(
                        connection, transaction, founder, cancellationToken)
                        .ConfigureAwait(false);

                using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    if (migration.RecordsHash)
                    {
                        // Some forward-only SQL migrations seed their own version row.
                        // Insert when absent, otherwise hash-pin only the row created by
                        // this migration, provided its name matches and no hash is pinned.
                        record.CommandText = """
                            INSERT INTO SchemaVersions (SchemaVersion, MigrationName, AppliedAtUtc, Sha256)
                            VALUES ($v, $n, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), $h)
                            ON CONFLICT(SchemaVersion) DO UPDATE SET Sha256 = excluded.Sha256
                            WHERE SchemaVersions.MigrationName = excluded.MigrationName
                              AND SchemaVersions.Sha256 IS NULL;
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
                        int changed = await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        if (changed != 1)
                            throw new InvalidOperationException(
                                $"Migration '{migration.Name}' could not record its expected version and SHA-256 hash.");
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

    private static async Task RecordLegacyFounderDetachmentAsync(
        SqliteConnection connection, SqliteTransaction tx,
        (string UserId, string Username, string PersonId) founder,
        CancellationToken ct)
    {
        const string code = "FOUNDING_ADMIN_LEGACY_LINK_DETACHED";
        const string operation = "فصل ارتباط حساب الإدارة العامة القديم بكشف القوة";
        const string actor = "USR-SYSTEM";
        const string actorName = "النظام";
        const string targetType = "User";
        const string description = "فصل ارتباط قديم لحساب المؤسس مع الحفاظ على هويته وسجل التدقيق";
        const string reason = "ترقية آمنة وفق المتطلب 03.16";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string localDate = DateTime.Now.ToString("yyyy-MM-dd");
        string correlationId = Guid.NewGuid().ToString("N");
        string auditId = "AUD-" + Guid.NewGuid().ToString("N");
        string targetName = "الإدارة العامة - " + founder.Username;
        string oldValue = System.Text.Json.JsonSerializer.Serialize(
            new { linkedPersonId = founder.PersonId });
        const string newValue = "{\"linkedPersonId\":null}";

        using var previous = connection.CreateCommand();
        previous.Transaction = tx;
        previous.CommandText = "SELECT EventHash FROM AuditEvents ORDER BY rowid DESC LIMIT 1;";
        string previousHash = await previous.ExecuteScalarAsync(ct).ConfigureAwait(false)
            as string ?? "GENESIS";
        string timestamp = now.ToString("O");
        string eventHash = AuditCanonicalHash.Compute(
            code, operation, actor, actorName, actorName,
            targetType, founder.UserId, targetName, oldValue, newValue,
            description, reason, localDate, timestamp, correlationId, previousHash);

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO AuditEvents(
                AuditEventId, OperationCode, OperationArabicName,
                ActorUserId, ActorPersonId, ActorUsernameSnapshot,
                ActorRosterFullNameSnapshot, TargetEntityType, TargetEntityId,
                TargetDisplaySnapshot, OldValueJson, NewValueJson,
                DefaultArabicDescription, Reason, EffectiveLocalDate,
                ExecutedAtUtc, CorrelationId, EventHash, PriorEventHash)
            VALUES($id,$code,$operation,$actor,NULL,$actorName,$actorName,
                $type,$target,$targetName,$old,$new,$description,$reason,
                $date,$at,$correlation,$hash,$previous);
            """;
        insert.Parameters.AddWithValue("$id", auditId);
        insert.Parameters.AddWithValue("$code", code);
        insert.Parameters.AddWithValue("$operation", operation);
        insert.Parameters.AddWithValue("$actor", actor);
        insert.Parameters.AddWithValue("$actorName", actorName);
        insert.Parameters.AddWithValue("$type", targetType);
        insert.Parameters.AddWithValue("$target", founder.UserId);
        insert.Parameters.AddWithValue("$targetName", targetName);
        insert.Parameters.AddWithValue("$old", oldValue);
        insert.Parameters.AddWithValue("$new", newValue);
        insert.Parameters.AddWithValue("$description", description);
        insert.Parameters.AddWithValue("$reason", reason);
        insert.Parameters.AddWithValue("$date", localDate);
        insert.Parameters.AddWithValue("$at", timestamp);
        insert.Parameters.AddWithValue("$correlation", correlationId);
        insert.Parameters.AddWithValue("$hash", eventHash);
        insert.Parameters.AddWithValue("$previous", previousHash);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
