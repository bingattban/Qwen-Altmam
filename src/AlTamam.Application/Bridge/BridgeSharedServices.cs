using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Shared, host-agnostic bridge services: provisioning detection, local authentication with server-side
/// session nonces, role-checked action authorization driven by the canonical manifest metadata, and the
/// Phase-04 basic data reads/writes used by the shared screens (sites, statuses, persons search, drafts).
/// Roles are resolved exclusively from the stored user row — a client-supplied role is never trusted.
/// </summary>
public sealed class BridgeSharedServices
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public BridgeSharedServices(AlTamamDbContext database, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _db = database;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True when no active privileged user exists yet (first-run provisioning required).</summary>
    public async Task<bool> IsProvisionedAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM Users WHERE ActiveFlag = 1);";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    /// <summary>Creates the very first administrator account. Refuses to run once provisioning exists.</summary>
    public async Task<BridgeResponse> ProvisionFirstAdministratorAsync(string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Error(BridgeErrorCodes.ValidationError, "اسم المستخدم وكلمة المرور مطلوبان.");
        if (password.Length < 8)
            return Error(BridgeErrorCodes.ValidationError, "يجب ألا تقل كلمة المرور عن 8 أحرف.");
        if (!await IsProvisionedAsync(ct).ConfigureAwait(false))
            return await CreateUserInternalAsync(username.Trim(), password, UserRole.GeneralAdministration, requiresSetup: false, ct).ConfigureAwait(false);
        return Error(BridgeErrorCodes.ConflictStale, "تمت تهيئة البرنامج مسبقاً؛ لا يمكن إنشاء مدير أول آخر.");
    }

    /// <summary>Inserts one local user with an Argon2id-hashed credential (never trusts a client-supplied role).</summary>
    public async Task<BridgeResponse> CreateUserInternalAsync(
        string username, string password, UserRole role, bool requiresSetup, CancellationToken ct)
    {
        string normalized = NormalizeUsername(username);
        var (hash, salt) = PasswordSecurity.HashPassword(password);
        string userId = "USR-" + Guid.NewGuid().ToString("N");
        DateTimeOffset nowUtc = _time.GetUtcNow();
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            using var dup = connection.CreateCommand();
            dup.Transaction = tx;
            dup.CommandText = "SELECT EXISTS(SELECT 1 FROM Users WHERE NormalizedUsername = $u);";
            dup.Parameters.AddWithValue("$u", normalized);
            if (Convert.ToInt32(await dup.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1)
                return Error(BridgeErrorCodes.DuplicateUsername, "اسم المستخدم مستخدم بالفعل.");
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"INSERT INTO Users (UserId, LinkedPersonId, Username, NormalizedUsername, Role,
                                CredentialHash, PasswordSalt, RequiresFirstRunSetup, ActiveFlag, FailedLoginCount,
                                CreatedByUserId, CreatedByDisplaySnapshot, CreatedAtUtc, RowVersion)
                                VALUES ($id, NULL, $name, $norm, $role, $hash, $salt, $setup, 1, 0, 'USR-SYSTEM', 'النظام', $now, 1);";
            insert.Parameters.AddWithValue("$id", userId);
            insert.Parameters.AddWithValue("$name", username);
            insert.Parameters.AddWithValue("$norm", normalized);
            insert.Parameters.AddWithValue("$role", (int)role);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$salt", salt);
            insert.Parameters.AddWithValue("$setup", requiresSetup ? 1 : 0);
            insert.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return Ok(new JsonObject { ["userId"] = userId, ["username"] = username, ["role"] = role.ToString() });
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Authenticates against Argon2id hashes and issues a fresh random session nonce.</summary>
    public async Task<(BridgePrincipal? Principal, BridgeResponse? Failure)> AuthenticateAsync(
        string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return (null, Error(BridgeErrorCodes.ValidationError, "اسم المستخدم وكلمة المرور مطلوبان."));

        string normalized = NormalizeUsername(username);
        DateTimeOffset nowUtc = _time.GetUtcNow();
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            using var find = connection.CreateCommand();
            find.Transaction = tx;
            find.CommandText = @"SELECT UserId, Username, Role, CredentialHash, PasswordSalt, ActiveFlag, LockedUntilUtc
                                 FROM Users WHERE NormalizedUsername = $u;";
            find.Parameters.AddWithValue("$u", normalized);
            using SqliteDataReader reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return ((BridgePrincipal?)null, Error(BridgeErrorCodes.Unauthorized, "بيانات الدخول غير صحيحة."));

            var userId = reader.GetString(0);
            var storedName = reader.GetString(1);
            var role = (UserRole)reader.GetInt32(2);
            var hash = reader.GetString(3);
            var salt = reader.GetString(4);
            bool active = Convert.ToInt32(reader.GetValue(5)) == 1;
            string? lockedUntil = reader.IsDBNull(6) ? null : reader.GetString(6);
            reader.Close();

            if (!active)
                return (null, Error(BridgeErrorCodes.Unauthorized, "الحساب معطّل."));
            if (lockedUntil is not null && DateTimeOffset.TryParse(lockedUntil, out DateTimeOffset lockEnd) && lockEnd > nowUtc)
                return (null, Error(BridgeErrorCodes.Unauthorized, "الحساب مقفل مؤقتاً بعد محاولات فاشلة متكررة."));

            if (!PasswordSecurity.VerifyPassword(password, hash, salt))
            {
                using var fail = connection.CreateCommand();
                fail.Transaction = tx;
                fail.CommandText = @"UPDATE Users SET FailedLoginCount = FailedLoginCount + 1,
                                    LockedUntilUtc = CASE WHEN FailedLoginCount + 1 >= 5
                                        THEN STRFTIME('%Y-%m-%dT%H:%M:%fZ','now','+15 minutes') ELSE LockedUntilUtc END
                                    WHERE UserId = $id;
                                    INSERT INTO FailedLoginEvents (UsernameAttempted, AttemptedAtUtc, FailureReason)
                                    VALUES ($u, $now, 'bad-password');";
                fail.Parameters.AddWithValue("$id", userId);
                fail.Parameters.AddWithValue("$u", normalized);
                fail.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
                await fail.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return (null, Error(BridgeErrorCodes.Unauthorized, "بيانات الدخول غير صحيحة."));
            }

            string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            DateTimeOffset expiry = nowUtc.AddMinutes(480);
            using var session = connection.CreateCommand();
            session.Transaction = tx;
            session.CommandText = @"INSERT INTO UserSessions (SessionNonce, UserId, Role, IssuedAtUtc, ExpiresAtUtc, RevokedFlag)
                                    VALUES ($n, $id, $role, $now, $exp, 0);
                                    UPDATE Users SET FailedLoginCount = 0, LockedUntilUtc = NULL WHERE UserId = $id;";
            session.Parameters.AddWithValue("$n", nonce);
            session.Parameters.AddWithValue("$id", userId);
            session.Parameters.AddWithValue("$role", (int)role);
            session.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
            session.Parameters.AddWithValue("$exp", expiry.ToString("O"));
            await session.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return (new BridgePrincipal(userId, storedName, role, nonce), (BridgeResponse?)null);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a live, unexpired, unrevoked session nonce to its principal (server-side truth).</summary>
    public async Task<BridgePrincipal?> ResolveSessionAsync(string sessionNonce, CancellationToken ct = default)
    {
        if (!BridgeProtocol.IsValidSessionNonceFormat(sessionNonce)) return null;
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT u.UserId, u.Username, s.Role
                            FROM UserSessions s JOIN Users u ON u.UserId = s.UserId
                            WHERE s.SessionNonce = $n AND s.RevokedFlag = 0 AND s.ExpiresAtUtc > $now AND u.ActiveFlag = 1;";
        cmd.Parameters.AddWithValue("$n", sessionNonce);
        cmd.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var principal = new BridgePrincipal(reader.GetString(0), reader.GetString(1), (UserRole)reader.GetInt32(2), sessionNonce);
        return principal;
    }

    /// <summary>Revokes the presenting session (Logout / SessionEnded).</summary>
    public async Task RevokeSessionAsync(string sessionNonce, CancellationToken ct = default)
    {
        await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE UserSessions SET RevokedFlag = 1 WHERE SessionNonce = $n;";
            cmd.Parameters.AddWithValue("$n", sessionNonce);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ reads

    public async Task<BridgeResponse> ListSitesAsync(bool includeInactive, CancellationToken ct)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = includeInactive
            ? "SELECT SiteId, VisibleName, ActiveFlag, RowVersion FROM Sites ORDER BY VisibleName;"
            : "SELECT SiteId, VisibleName, ActiveFlag, RowVersion FROM Sites WHERE ActiveFlag = 1 ORDER BY VisibleName;";
        var sites = new JsonArray();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            sites.Add(new JsonObject
            {
                ["siteId"] = reader.GetString(0),
                ["name"] = reader.GetString(1),
                ["active"] = Convert.ToInt32(reader.GetValue(2)) == 1,
                ["rowVersion"] = reader.GetInt64(3)
            });
        return Ok(sites);
    }

    public async Task<BridgeResponse> ListActiveStatusesAsync(CancellationToken ct)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT StatusId, VisibleName, ShortSymbol, ChosenColorHex, ActiveFlag, RowVersion FROM AttendanceStatusDefinitions WHERE ActiveFlag = 1 ORDER BY SortOrder;";
        var statuses = new JsonArray();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            statuses.Add(new JsonObject
            {
                ["statusId"] = reader.GetString(0),
                ["displayName"] = reader.GetString(1),
                ["shortSymbol"] = reader.GetString(2),
                ["color"] = reader.GetString(3),
                ["active"] = Convert.ToInt32(reader.GetValue(4)) == 1,
                ["version"] = reader.GetInt64(5)
            });
        return Ok(statuses);
    }

    public async Task<BridgeResponse> SearchPersonsAsync(string term, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Error(BridgeErrorCodes.MissingData, "أدخل اسماً أو رقماً للبحث.");
        take = Math.Clamp(take <= 0 ? 25 : take, 1, 200);
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT p.PersonId, p.CurrentVisibleName, p.CurrentServiceNumberA, p.OrganizationalStatus, p.RowVersion,
                                   COALESCE((SELECT s.VisibleName FROM PersonAssignments a JOIN Sites s ON s.SiteId = a.SiteId
                                             WHERE a.PersonId = p.PersonId AND a.ValidToLocalDate IS NULL), 'غير محدد')
                            FROM Persons p
                            WHERE p.NormalizedName LIKE $like OR p.NormalizedServiceNumberA LIKE $like2
                            ORDER BY p.CurrentVisibleName LIMIT $take;";
        string like = "%" + NormalizeUsername(term) + "%";
        cmd.Parameters.AddWithValue("$like", like);
        cmd.Parameters.AddWithValue("$like2", like);
        cmd.Parameters.AddWithValue("$take", take);
        var rows = new JsonArray();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new JsonObject
            {
                ["personId"] = reader.GetString(0),
                ["displayName"] = reader.GetString(1),
                ["serviceNumberA"] = reader.GetString(2),
                ["organizationalStatus"] = reader.GetInt32(3),
                ["revision"] = reader.GetInt64(4),
                ["siteName"] = reader.GetString(5)
            });
        return Ok(new JsonObject { ["items"] = rows, ["count"] = rows.Count });
    }

    public async Task<BridgeResponse> ListDraftsAsync(string userId, CancellationToken ct)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DraftId, BusinessDate, SiteId, TargetStatusId, UpdatedAtUtc FROM AttendanceDrafts WHERE CreatedByUserId = $u ORDER BY UpdatedAtUtc DESC;";
        cmd.Parameters.AddWithValue("$u", userId);
        var rows = new JsonArray();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new JsonObject
            {
                ["draftId"] = reader.GetString(0),
                ["businessDate"] = reader.GetString(1),
                ["siteId"] = reader.IsDBNull(2) ? null : reader.GetString(2),
                ["targetStatusId"] = reader.GetString(3),
                ["updatedAtUtc"] = reader.GetString(4)
            });
        return Ok(rows);
    }

    public async Task<BridgeResponse> SaveDraftAsync(string userId, JsonObject payload, CancellationToken ct)
    {
        string businessDate = GetRequired(payload, "businessDate");
        string targetStatusId = GetRequired(payload, "targetStatusId");
        JsonArray selected = payload["selectedPersonIds"] as JsonArray
            ?? throw new BridgeProtocolException(BridgeErrorCodes.ValidationError, "قائمة الأفراد المطلوبة مفقودة.");
        string draftId = payload.TryGetPropertyValue("draftId", out var d) && d is JsonNode ds && ds.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? ds.GetValue<string>()
            : "DRF-" + Guid.NewGuid().ToString("N");
        string json = selected.ToJsonString();
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO AttendanceDrafts (DraftId, BusinessDate, SiteId, TargetStatusId, SelectedPersonIdsJson, CreatedByUserId, UpdatedAtUtc)
                                VALUES ($id, $date, $site, $status, $json, $user, $now)
                                ON CONFLICT(DraftId) DO UPDATE SET BusinessDate=$date, SiteId=$site, TargetStatusId=$status,
                                                                   SelectedPersonIdsJson=$json, UpdatedAtUtc=$now;";
            cmd.Parameters.AddWithValue("$id", draftId);
            cmd.Parameters.AddWithValue("$date", businessDate);
            cmd.Parameters.AddWithValue("$site", payload["siteId"]?.GetValue<string>() ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$status", targetStatusId);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$user", userId);
            cmd.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return Ok(new JsonObject { ["draftId"] = draftId, ["savedAtUtc"] = _time.GetUtcNow().ToString("O") });
        }, ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> DeleteDraftAsync(string userId, JsonObject payload, CancellationToken ct)
    {
        string draftId = GetRequired(payload, "draftId");
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM AttendanceDrafts WHERE DraftId = $id AND CreatedByUserId = $user;";
            cmd.Parameters.AddWithValue("$id", draftId);
            cmd.Parameters.AddWithValue("$user", userId);
            int removed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return removed > 0
                ? Ok(new JsonObject { ["draftId"] = draftId })
                : Error(BridgeErrorCodes.MissingData, "المسودة غير موجودة.");
        }, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- helpers

    internal static string NormalizeUsername(string value) => value.Trim().ToLowerInvariant();

    private static string GetRequired(JsonObject payload, string key)
        => payload.TryGetPropertyValue(key, out JsonNode? node) && node is not null && node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : throw new BridgeProtocolException(BridgeErrorCodes.ValidationError, $"الحقل '{key}' مطلوب في الحمولة.");

    internal static BridgeResponse Ok(object? data, long? revision = null, IReadOnlyList<Contracts.Events.UiInvalidationEventPayload>? events = null)
        => new("", true, data, null, null, revision, events);

    internal static BridgeResponse Error(string code, string message)
        => new("", false, null, code, message);
}
