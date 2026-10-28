using System.Collections.Concurrent;
using System.Security.Cryptography;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Native-host-only prerequisite for a future restore. Requires the CURRENT
/// live GeneralAdministration session, its password re-entered afresh, and
/// a byte-exact verified encrypted candidate. Issues one-use, 60-second,
/// private in-memory grants to STAGE a backup only, never to activate it.
/// This class is deliberately NOT registered as a WebView2 bridge handler.
/// </summary>
public sealed class NativeRestoreReauthenticationGate
{
    public sealed class Grant
    {
        internal Grant(string nonce) => Nonce = nonce;
        internal string Nonce { get; }
    }

    private sealed record Pending(string UserId, string SessionNonce,
        string CandidatePath, string Sha256, DateTimeOffset Expires);

    private readonly AlTamamDbContext _db;
    private readonly BridgeSharedServices _sessions;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public NativeRestoreReauthenticationGate(AlTamamDbContext db,
        BridgeSharedServices sessions, TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _time = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns an opaque native capability only after recent administrator
    /// authentication and validating the exact SHA-256 of a SQLCipher backup.
    /// No password or capability is sent to JavaScript or written to disk.
    /// </summary>
    public async Task<Grant?> AuthorizeAsync(BridgePrincipal actor, string? password,
        string? selectedBackupPath, string? expectedSha256, CancellationToken ct=default)
    {
        if(actor is null || actor.Role!=UserRole.GeneralAdministration ||
           string.IsNullOrEmpty(password) || password.Length>1024 ||
           string.IsNullOrWhiteSpace(selectedBackupPath) ||
           string.IsNullOrWhiteSpace(expectedSha256) ||
           expectedSha256.Length!=64 || !expectedSha256.All(Uri.IsHexDigit))
            return null;

        BridgePrincipal? live=await _sessions.ResolveSessionAsync(actor.SessionNonce,ct)
            .ConfigureAwait(false);
        if(live is null || live.Role!=UserRole.GeneralAdministration ||
           live.UserId!=actor.UserId || live.Username!=actor.Username)
            return null;

        if(!await RevalidatePasswordAsync(actor,password,ct).ConfigureAwait(false))
            return null;

        // A valid password is necessary but not sufficient. The actual bytes
        // must be SQLCipher-verifiable under the existing original key.
        string pinnedPath;
        try
        {
            var inspection=await _db.InspectEncryptedBackupAsync(selectedBackupPath,ct)
                .ConfigureAwait(false);
            if(inspection.SchemaVersion!=AlTamamDbContext.CurrentSchemaVersion ||
               !string.Equals(inspection.Sha256,expectedSha256,
                   StringComparison.OrdinalIgnoreCase))
                return null;
            pinnedPath=inspection.FullPath;
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or CryptographicException or
            SqliteException or InvalidOperationException)
        {
            return null;
        }

        // An administrator who logged out during the backup inspection cannot
        // receive an approval; nothing granted is durable across application runs.
        live=await _sessions.ResolveSessionAsync(actor.SessionNonce,ct).ConfigureAwait(false);
        if(live is null || live.UserId!=actor.UserId ||
           live.Role!=UserRole.GeneralAdministration)return null;

        foreach(var entry in _pending)
            if(entry.Value.Expires<=_time.GetUtcNow())
                _pending.TryRemove(entry.Key,out _);
        if(_pending.Count>=32)return null;

        string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var claim=new Pending(actor.UserId,actor.SessionNonce,pinnedPath,
            expectedSha256.ToUpperInvariant(),_time.GetUtcNow().AddSeconds(60));
        return _pending.TryAdd(token,claim)?new Grant(token):null;
    }

    /// <summary>
    /// Burns the opaque grant BEFORE checking it and returns a staged encrypted
    /// image plus a verified current-data rescue. No live database is switched.
    /// The original selected file and the session must still match exactly.
    /// </summary>
    public async Task<StagedRestorePreparation?> ConsumeAndStageAsync(Grant? approval,
        BridgePrincipal actor,CancellationToken ct=default)
    {
        if(approval is null || actor is null ||
           !_pending.TryRemove(approval.Nonce,out Pending? pending))return null;
        if(_time.GetUtcNow()>pending.Expires ||
           actor.Role!=UserRole.GeneralAdministration ||
           actor.UserId!=pending.UserId ||
           actor.SessionNonce!=pending.SessionNonce)return null;
        BridgePrincipal? live=await _sessions.ResolveSessionAsync(actor.SessionNonce,ct)
            .ConfigureAwait(false);
        if(live is null || live.UserId!=pending.UserId ||
           live.Role!=UserRole.GeneralAdministration)return null;

        try
        {
            var candidate=await _db.InspectEncryptedBackupAsync(pending.CandidatePath,ct)
                .ConfigureAwait(false);
            if(candidate.SchemaVersion!=AlTamamDbContext.CurrentSchemaVersion ||
               !string.Equals(candidate.Sha256,pending.Sha256,
                   StringComparison.OrdinalIgnoreCase))return null;

            // Existing staging validates the same key, schema and bytes again,
            // takes a WAL-consistent rescue, and never alters the live database.
            StagedRestorePreparation staged=await new EncryptedRestoreStagingService(_db)
                .PrepareAsync(pending.CandidatePath,ct).ConfigureAwait(false);
            if(!string.Equals(staged.StageSha256,pending.Sha256,
                   StringComparison.OrdinalIgnoreCase))
            {
                DiscardUnapprovedStage(staged.StageFilePath);
                return null;
            }
            live=await _sessions.ResolveSessionAsync(actor.SessionNonce,ct)
                .ConfigureAwait(false);
            if(live is null || live.UserId!=pending.UserId ||
               live.Role!=UserRole.GeneralAdministration)
            {
                DiscardUnapprovedStage(staged.StageFilePath);
                return null;
            }
            return staged;
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or CryptographicException or
            SqliteException or InvalidOperationException)
        {
            // No stage is activated. Existing verified rescues are never deleted.
            return null;
        }
    }

    private static void DiscardUnapprovedStage(string stage)
    {
        if(Path.GetFileName(stage).StartsWith(".altamam-restore-stage-",
            StringComparison.Ordinal) && File.Exists(stage))
            File.Delete(stage);
    }

    private async Task<bool> RevalidatePasswordAsync(BridgePrincipal actor,
        string password,CancellationToken ct)
    {
        return await _db.ExecuteInWriteTransactionAsync(async(connection,tx)=>
        {
            // Validate session and database role again *under the write gate*.
            // The presented role/username alone never grants native restore access.
            using var query=connection.CreateCommand();
            query.Transaction=tx;
            query.CommandText="""
                SELECT u.CredentialHash,u.PasswordSalt,u.FailedLoginCount,u.LockedUntilUtc
                FROM Users u JOIN UserSessions s ON s.UserId=u.UserId
                WHERE s.SessionNonce=$nonce AND s.RevokedFlag=0 AND
                    s.ExpiresAtUtc>$now AND
                    u.UserId=$user AND u.Username=$username AND u.Role=1 AND u.ActiveFlag=1;
                """;
            var now=_sessions.Time.GetUtcNow();
            query.Parameters.AddWithValue("$nonce",actor.SessionNonce);
            query.Parameters.AddWithValue("$now",now.ToString("O"));
            query.Parameters.AddWithValue("$user",actor.UserId);
            query.Parameters.AddWithValue("$username",actor.Username);
            using var reader=await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if(!await reader.ReadAsync(ct).ConfigureAwait(false))return false;
            var hash=reader.GetString(0);
            var salt=reader.GetString(1);
            int failures=reader.GetInt32(2);
            string? lockUntil=reader.IsDBNull(3)?null:reader.GetString(3);
            reader.Close();
            if(lockUntil is not null &&
               DateTimeOffset.TryParse(lockUntil,out var end) && end>now)return false;

            bool valid=PasswordSecurity.VerifyPassword(password,hash,salt);
            if(!valid)
            {
                bool expired=lockUntil is not null &&
                    DateTimeOffset.TryParse(lockUntil,out var lastEnd) && lastEnd<=now;
                int next=expired?1:Math.Min(5,failures+1);
                string? until=next>=5?now.AddMinutes(15).ToString("O"):null;
                using var failed=connection.CreateCommand();
                failed.Transaction=tx;
                failed.CommandText="""
                    UPDATE Users SET FailedLoginCount=$count,LockedUntilUtc=$until
                    WHERE UserId=$user;
                    INSERT INTO FailedLoginEvents(UsernameAttempted,AttemptedAtUtc,FailureReason)
                    VALUES($username,$now,'restore-reauth-bad-password');
                    """;
                failed.Parameters.AddWithValue("$count",next);
                failed.Parameters.AddWithValue("$until",(object?)until??DBNull.Value);
                failed.Parameters.AddWithValue("$user",actor.UserId);
                failed.Parameters.AddWithValue("$username",actor.Username);
                failed.Parameters.AddWithValue("$now",now.ToString("O"));
                await failed.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return false;
            }
            if(failures!=0 || lockUntil is not null)
            {
                using var reset=connection.CreateCommand();
                reset.Transaction=tx;
                reset.CommandText="""
                    UPDATE Users SET FailedLoginCount=0,LockedUntilUtc=NULL
                    WHERE UserId=$user AND Role=1;
                    """;
                reset.Parameters.AddWithValue("$user",actor.UserId);
                await reset.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return true;
        },ct).ConfigureAwait(false);
    }
}
