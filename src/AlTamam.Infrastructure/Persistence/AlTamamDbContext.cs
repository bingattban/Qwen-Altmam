using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AlTamam.Infrastructure.Persistence;

/// <summary>Encrypted SQLite connection management and atomic initialization of schema V001.</summary>
public sealed class AlTamamDbContext : IDisposable
{
    private const int CurrentSchemaVersion = 1;
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
                if (version != CurrentSchemaVersion)
                    throw new InvalidOperationException($"Database schema version {version} is incompatible with {CurrentSchemaVersion}.");
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

    public bool HasValidIntegrity()
    {
        using var connection = CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return string.Equals(cmd.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _writeGate.Dispose();
}
