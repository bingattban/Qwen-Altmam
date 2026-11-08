using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Native-host-only orchestration of an explicitly approved offline restore.
/// This is NOT an action handler and must never be invoked inside an active
/// WebView2 bridge lease: a bridge request awaiting its own drain deadlocks.
/// The host must supply its independently authenticated WPF confirmation and
/// an awaited shutdown callback under its existing single-instance mutex.
/// </summary>
public sealed class NativeRestoreActivationCoordinator
{
    public sealed record Result(string RollbackPath, string PreparationRescuePath);

    private readonly AlTamamDbContext _database;
    private readonly BridgeSharedServices _sessions;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _exclusive = new(1, 1);

    public NativeRestoreActivationCoordinator(AlTamamDbContext database,
        BridgeSharedServices sessions, TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns null before shutdown on any denied or abandoned approval.
    /// Once shutdown starts, an exception MUST terminate the host; no old
    /// bridge or SQLCipher context may be resumed until startup reconciliation.
    /// Never send the password, original database key or native grant to JS.
    /// </summary>
    public async Task<Result?> TryActivateAsync(
        BridgePrincipal actor, string password, string selectedEncryptedBackup,
        string expectedSha256, string existingDatabaseKeyHex,
        Func<StagedRestorePreparation, CancellationToken, Task<bool>> nativeFinalApproval,
        Func<CancellationToken, Task> stopBridgeSchedulersAndDatabase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nativeFinalApproval);
        ArgumentNullException.ThrowIfNull(stopBridgeSchedulersAndDatabase);
        if (actor is null || actor.Role != UserRole.GeneralAdministration ||
            string.IsNullOrWhiteSpace(existingDatabaseKeyHex))
            return null;

        await _exclusive.WaitAsync(cancellationToken).ConfigureAwait(false);
        StagedRestorePreparation? prepared = null;
        bool enteredShutdown = false;
        try
        {
            var reauthentication = new NativeRestoreReauthenticationGate(
                _database, _sessions, _time);
            var grant = await reauthentication.AuthorizeAsync(actor, password,
                selectedEncryptedBackup, expectedSha256, cancellationToken)
                .ConfigureAwait(false);
            if (grant is null) return null;

            prepared = await reauthentication.ConsumeAndStageAsync(
                grant, actor, cancellationToken).ConfigureAwait(false);
            if (prepared is null) return null;

            // The offline key comes exclusively from the Windows native key
            // store. Refuse a wrong/replaced key BEFORE requesting the final
            // confirmation and before stopping an otherwise healthy host.
            if (!await OfflineRestoreSwitch.CanUsePreparedKeyAsync(
                    _database.DatabasePath, existingDatabaseKeyHex,
                    prepared, cancellationToken).ConfigureAwait(false))
                return null;

            // Stage/rescue were verified by SQLCipher. The native host now
            // presents their counts and SHA to the same WPF administrator.
            // A final warning must be explicitly accepted, never inferred
            // from a browser-controlled boolean in an action payload.
            DateTimeOffset approvalIssued = _time.GetUtcNow();
            if (!await nativeFinalApproval(prepared, cancellationToken)
                .ConfigureAwait(false)) return null;
            if (_time.GetUtcNow() - approvalIssued > TimeSpan.FromSeconds(60))
                return null;

            var current = await _sessions.ResolveSessionAsync(actor.SessionNonce,
                cancellationToken).ConfigureAwait(false);
            if (current is null ||
                current.UserId != actor.UserId ||
                current.Username != actor.Username ||
                current.Role != UserRole.GeneralAdministration)
                return null;

            // The original selected encrypted file is still byte-identical.
            // Stage validation alone cannot approve a changed user selection.
            var candidate = await _database.InspectEncryptedBackupAsync(
                selectedEncryptedBackup, cancellationToken).ConfigureAwait(false);
            if (candidate.SchemaVersion != AlTamamDbContext.CurrentSchemaVersion ||
                !string.Equals(candidate.Sha256, expectedSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(prepared.StageSha256, candidate.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                return null;

            cancellationToken.ThrowIfCancellationRequested();

            // This callback MUST stop the native UI admission, await all
            // in-flight WebView operations and scheduled tasks, and dispose
            // all SQLCipher contexts. Never invoke from a bridge handler.
            enteredShutdown = true;
            await stopBridgeSchedulersAndDatabase(cancellationToken)
                .ConfigureAwait(false);

            // No cancellation is allowed after the irreversible boundary.
            // OfflineRestoreSwitch writes a durable crash-recovery intent,
            // checkpoints WAL, makes a fresh encrypted rescue and atomically
            // replaces the database. Reconciliation occurs on next startup.
            string rollback = await new OfflineRestoreSwitch().ActivateOfflineAsync(
                _database.DatabasePath, existingDatabaseKeyHex, prepared,
                ct: CancellationToken.None).ConfigureAwait(false);
            return new Result(rollback, prepared.RescueFilePath);
        }
        finally
        {
            // A cancelled/denied operation is never an activation. Remove
            // only its temporary stage; retain all verified encrypted rescues.
            // After shutdown, preserve the stage and recovery journal because
            // a crash may have interrupted the atomic replacement.
            if (!enteredShutdown && prepared is not null)
            {
                string stage = prepared.StageFilePath;
                if (Path.GetFileName(stage).StartsWith(".altamam-restore-stage-",
                    StringComparison.Ordinal) &&
                    string.Equals(Path.GetDirectoryName(stage),
                        Path.GetDirectoryName(_database.DatabasePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    try { if (File.Exists(stage)) File.Delete(stage); }
                    catch (IOException) { /* Never mask the real refusal. */ }
                    catch (UnauthorizedAccessException) { /* Keep rescue intact. */ }
                }
            }
            _exclusive.Release();
        }
    }
}
