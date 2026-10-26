using System.Globalization;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Scheduling;

/// <summary>
/// Removes only preview tokens expired for more than 30 days and not referenced
/// anywhere else in the current database schema. History/evidence tables and
/// their contents are never changed. Failure means a full rollback.
/// </summary>
public sealed class PreviewTokenRetentionService
{
    private const string LastSweepSetting = "PreviewTokenRetentionLastSweepUtc";
    private const int MaxCandidatesPerSweep = 128;
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PreviewTokenRetentionService(AlTamamDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<int> RunIfDueAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
            {
                DateTimeOffset now = _time.GetUtcNow();
                using (var state = connection.CreateCommand())
                {
                    state.Transaction = tx;
                    state.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
                    state.Parameters.AddWithValue("$key", LastSweepSetting);
                    string? last = await state.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                    if (DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out DateTimeOffset previous) &&
                        now - previous < TimeSpan.FromDays(1))
                        return 0;
                }

                string cutoff = now.AddDays(-30).ToString("O", CultureInfo.InvariantCulture);
                var candidates = new List<string>();
                using (var expired = connection.CreateCommand())
                {
                    expired.Transaction = tx;
                    expired.CommandText = """
                        SELECT Token FROM PreviewTokens
                        WHERE ExpiresAtUtc < $cutoff AND IssuedAtUtc < $cutoff
                        ORDER BY ExpiresAtUtc, Token LIMIT $limit;
                        """;
                    expired.Parameters.AddWithValue("$cutoff", cutoff);
                    expired.Parameters.AddWithValue("$limit", MaxCandidatesPerSweep);
                    using var reader = await expired.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        candidates.Add(reader.GetString(0));
                }

                // Fail closed. Inspect the actual schema rather than assuming only
                // AuditEvents contains a token: new features may persist references
                // in JSON or custom text fields. Any token occurrence protects it.
                var referencePredicates = new List<string>();
                if (candidates.Count > 0)
                {
                    var tableNames = new List<string>();
                    using (var tables = connection.CreateCommand())
                    {
                        tables.Transaction = tx;
                        tables.CommandText = """
                            SELECT name FROM sqlite_master
                            WHERE type='table' AND name NOT LIKE 'sqlite_%'
                              AND name <> 'PreviewTokens' ORDER BY name;
                            """;
                        using var reader = await tables.ExecuteReaderAsync(ct).ConfigureAwait(false);
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                            tableNames.Add(reader.GetString(0));
                    }
                    foreach (string table in tableNames)
                    {
                        var columns = new List<string>();
                        using var schema = connection.CreateCommand();
                        schema.Transaction = tx;
                        schema.CommandText = "PRAGMA table_info(" + Quote(table) + ");";
                        using var reader = await schema.ExecuteReaderAsync(ct).ConfigureAwait(false);
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                            columns.Add(reader.GetString(1));
                        if (columns.Count == 0)
                            throw new InvalidOperationException("Cannot inspect reference table: " + table);
                        string predicate = string.Join(" OR ", columns.Select(column =>
                            "instr(CAST(" + Quote(column) + " AS TEXT), $token) > 0"));
                        referencePredicates.Add("SELECT EXISTS(SELECT 1 FROM " + Quote(table) +
                            " WHERE " + predicate + ");");
                    }
                }

                int removed = 0;
                foreach (string token in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    bool referenced = false;
                    foreach (string sql in referencePredicates)
                    {
                        using var check = connection.CreateCommand();
                        check.Transaction = tx;
                        check.CommandText = sql;
                        check.Parameters.AddWithValue("$token", token);
                        if (Convert.ToInt32(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
                        {
                            referenced = true;
                            break;
                        }
                    }
                    if (referenced) continue;
                    using var delete = connection.CreateCommand();
                    delete.Transaction = tx;
                    delete.CommandText = """
                        DELETE FROM PreviewTokens WHERE Token=$token
                          AND ExpiresAtUtc < $cutoff AND IssuedAtUtc < $cutoff;
                        """;
                    delete.Parameters.AddWithValue("$token", token);
                    delete.Parameters.AddWithValue("$cutoff", cutoff);
                    removed += await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                using (var checkpoint = connection.CreateCommand())
                {
                    checkpoint.Transaction = tx;
                    checkpoint.CommandText = """
                        INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                        VALUES($key,$now,$now)
                        ON CONFLICT(SettingKey) DO UPDATE SET
                        SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                        """;
                    checkpoint.Parameters.AddWithValue("$key", LastSweepSetting);
                    checkpoint.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
                    await checkpoint.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return removed;
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
