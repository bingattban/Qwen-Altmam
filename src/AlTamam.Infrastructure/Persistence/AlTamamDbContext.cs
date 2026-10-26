using System.Reflection;
using AlTamam.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;

namespace AlTamam.Infrastructure.Persistence;

/// <summary>Encrypted SQLite connection management and atomic initialization of schema V001.</summary>
public sealed class AlTamamDbContext : IDisposable
{
    /// <summary>Highest schema version this build understands (V009 enforces independent founding account).</summary>
    public const int CurrentSchemaVersion = 9;

    /// <summary>The canonical, ordered migration set shipped with this build.</summary>
    public static readonly SchemaMigration[] CanonicalMigrations =
    [
        new(1, "V001_InitialSchema", "AlTamam.Infrastructure.Persistence.Migrations.V001_InitialSchema.sql", RecordsHash: false),
        new(2, "V002_MigrationHardening", "AlTamam.Infrastructure.Persistence.Migrations.V002_MigrationHardening.sql"),
        new(3, "V003_PersonnelMasterData", "AlTamam.Infrastructure.Persistence.Migrations.V003_PersonnelMasterData.sql"),
        new(4, "V004_HistoricalIntegrity", "AlTamam.Infrastructure.Persistence.Migrations.V004_HistoricalIntegrity.sql"),
        new(5, "V005_AuditPresentationEvidence", "AlTamam.Infrastructure.Persistence.Migrations.V005_AuditPresentationEvidence.sql"),
        new(6, "V006_FoundingAccount", "AlTamam.Infrastructure.Persistence.Migrations.V006_FoundingAccount.sql"),
        new(7, "V007_PermanentIdentifiers", "AlTamam.Infrastructure.Persistence.Migrations.V007_PermanentIdentifiers.sql"),
        new(8, "V008_MutationReceipts", "AlTamam.Infrastructure.Persistence.Migrations.V008_MutationReceipts.sql"),
        new(9, "V009_FoundingAdminIndependent", "AlTamam.Infrastructure.Persistence.Migrations.V009_FoundingAdminIndependent.sql"),
    ];
    private readonly string _databasePath;
    private readonly string _databaseKey;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    static AlTamamDbContext() => SQLitePCL.Batteries_V2.Init();

    public AlTamamDbContext(string databasePath, string databaseKeyHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseKeyHex);
        byte[] key;
        try { key = Convert.FromHexString(databaseKeyHex); }
        catch (FormatException ex) { throw new ArgumentException("Invalid database key encoding.", nameof(databaseKeyHex), ex); }
        if (key.Length != 32) throw new ArgumentException("The database key must contain 32 bytes.", nameof(databaseKeyHex));
        _databasePath = Path.GetFullPath(databasePath);
        _databaseKey = databaseKeyHex;
    }

    public string DatabasePath => _databasePath;

    public SqliteConnection CreateOpenConnection()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = _databaseKey,
            ForeignKeys = true,
            DefaultTimeout = 15
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            using var cipher = connection.CreateCommand();
            cipher.CommandText = "PRAGMA cipher_version;";
            if (string.IsNullOrWhiteSpace(cipher.ExecuteScalar()?.ToString()))
                throw new InvalidOperationException("SQLCipher native encryption provider is unavailable.");
            using var settings = connection.CreateCommand();
            settings.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=15000;";
            settings.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = CreateOpenConnection();
            using var transaction = connection.BeginTransaction();
            var hasVersionTable = false;
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='SchemaVersions');";
                hasVersionTable = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
            }
            if (hasVersionTable)
            {
                using var check = connection.CreateCommand();
                check.Transaction = transaction;
                check.CommandText = "SELECT MAX(SchemaVersion) FROM SchemaVersions;";
                var result = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var version = result is DBNull or null ? 0 : Convert.ToInt32(result);
                if (version > CurrentSchemaVersion)
                    throw new InvalidOperationException($"Database schema version {version} is incompatible with {CurrentSchemaVersion}.");
                // Bring an existing database up to this build's schema through the hash-pinned runner.
                if (version < CurrentSchemaVersion)
                {
                    transaction.Commit();
                    // On an existing encrypted database, a verified WAL-consistent rescue
                    // snapshot MUST exist before the first forward-only migration executes.
                    // A missing/unwritable backup folder fails closed: no schema changes.
                    string rescueFolder = _databasePath + ".migration-backups";
                    Directory.CreateDirectory(rescueFolder);
                    string rescuePath = Path.Combine(rescueFolder,
                        $"pre-migration-v{version:D3}-to-v{CurrentSchemaVersion:D3}-{Guid.NewGuid():N}.db");
                    await CreateVerifiedEncryptedBackupUnderWriteGateAsync(
                        rescuePath, cancellationToken).ConfigureAwait(false);
                    var runner = new SchemaMigrationRunner(CanonicalMigrations);
                    await runner.UpgradeAsync(connection, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                var assembly = typeof(AlTamamDbContext).Assembly;
                const string resource = "AlTamam.Infrastructure.Persistence.Migrations.V001_InitialSchema.sql";

                await using var stream = assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException("The embedded schema resource is missing.");
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                using var schema = connection.CreateCommand();
                schema.Transaction = transaction;
                schema.CommandText = sql;
                await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // Bootstrap path: V001 lands a version-1 database — upgrade it to this build's schema
            // through the hash-pinned, forward-only migration runner (V002, V003, …).
            if (!hasVersionTable && CurrentSchemaVersion > 1)
            {
                var runner = new SchemaMigrationRunner(CanonicalMigrations);
                await runner.UpgradeAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _writeGate.Release(); }
    }

    public async Task<T> ExecuteInWriteTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = CreateOpenConnection();
            using var transaction = connection.BeginTransaction();
            var result = await operation(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Produce a transactionally consistent encrypted SQLCipher snapshot into an
    /// application-controlled path, then verify the copied database with the same
    /// key before moving it into place. No plaintext database is ever written.
    /// </summary>
    public async Task CreateVerifiedEncryptedBackupAsync(string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string output = Path.GetFullPath(destinationPath);
        string source = Path.GetFullPath(_databasePath);
        if (string.Equals(output, source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The backup destination cannot be the active database.");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CreateVerifiedEncryptedBackupUnderWriteGateAsync(output, ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    // The caller holds _writeGate: initialization must not reacquire this semaphore.
    // The same encrypted online-backup and verification path is used for manual
    // backup and for mandatory pre-migration safety snapshots.
    private async Task CreateVerifiedEncryptedBackupUnderWriteGateAsync(
        string output, CancellationToken ct)
    {
        string folder = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(folder);
        string temp = output + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            ct.ThrowIfCancellationRequested();
            using (var primary = CreateOpenConnection())
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temp,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Password = _databaseKey,
                ForeignKeys = true,
                Pooling = false, // close actual file handle before checking / renaming
                DefaultTimeout = 15
            }.ConnectionString))
            {
                backup.Open();
                primary.BackupDatabase(backup);
            }

            ct.ThrowIfCancellationRequested();
            // Reopen under SQLCipher and verify BOTH the cipher header and SQL
            // integrity. This prevents falsely reporting a plaintext copy as safe.
            using (var verification = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temp,
                Mode = SqliteOpenMode.ReadOnly,
                Password = _databaseKey,
                Pooling = false, // no pooled connection may retain .pending on Windows
                DefaultTimeout = 15
            }.ConnectionString))
            {
                verification.Open();
                using var check = verification.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Encrypted backup integrity check failed.");
                check.CommandText = "SELECT COUNT(*) FROM SchemaVersions;";
                if (Convert.ToInt64(check.ExecuteScalar()) < 1)
                    throw new InvalidOperationException("Encrypted backup schema is incomplete.");
            }
            using (var raw = File.OpenRead(temp))
            {
                Span<byte> prefix = stackalloc byte[16];
                if (raw.Read(prefix) != prefix.Length)
                    throw new InvalidOperationException("Encrypted backup was unexpectedly empty.");
                if (prefix.SequenceEqual("SQLite format 3\0"u8))
                    throw new InvalidOperationException("Backup was not encrypted; refusing to publish.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, output, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* Cleanup is best-effort; pending files are never advertised as backups. */ }
        }
    }

    /// <summary>Read-only SQLCipher backup verification using the already opened
    /// installation key. No key is exported or passed to JavaScript.</summary>
    public Task<EncryptedBackupInspection> InspectEncryptedBackupAsync(string path,
        CancellationToken ct=default)
        => EncryptedBackupInspector.VerifyAsync(path,_databasePath,_databaseKey,ct);

    public bool HasValidIntegrity()
    {
        using var connection = CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return string.Equals(cmd.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _writeGate.Dispose();
}
