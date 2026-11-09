using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Security;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Non-destructive recovery-key EXPORT only. Invoked from a physical trusted
/// WPF command, never from the untrusted WebView2 action catalog. GA requires
/// a currently active native session and freshly verified SQLCipher password.
/// Raw SQLCipher key and passphrase are never returned to JavaScript.
/// </summary>
public sealed class NativePortableRecoveryExportService
{
    private readonly AlTamamDbContext _db;
    private readonly BridgeSharedServices _sessions;
    private readonly WindowsDatabaseKeyStore _keyStore;

    public NativePortableRecoveryExportService(AlTamamDbContext database,
        BridgeSharedServices sessions, WindowsDatabaseKeyStore keyStore)
    {
        _db=database??throw new ArgumentNullException(nameof(database));
        _sessions=sessions??throw new ArgumentNullException(nameof(sessions));
        _keyStore=keyStore??throw new ArgumentNullException(nameof(keyStore));
    }

    /// <summary>
    /// Returns false for denied credentials/role/passphrase; native file errors
    /// propagate to trusted WPF, with no fallback, overwrite or DB mutation.
    /// The package is AES-GCM encrypted with a separate 16–1024 character
    /// passphrase; an active Windows DPAPI key and valid live DB are mandatory.
    /// </summary>
    public async Task<bool> TryExportAsync(BridgePrincipal? actor,
        string? currentAdminPassword, string? recoveryPassphrase,
        string destinationPath, CancellationToken ct=default)
    {
        if(actor is null || actor.Role!=UserRole.GeneralAdministration ||
           string.IsNullOrWhiteSpace(currentAdminPassword) ||
           string.IsNullOrWhiteSpace(recoveryPassphrase) ||
           recoveryPassphrase.Length<16 || recoveryPassphrase.Length>1024 ||
           string.Equals(currentAdminPassword,recoveryPassphrase,StringComparison.Ordinal))
            return false;
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        // Never write protected key material to a UNC share or a remote URI.
        string destination=Path.GetFullPath(destinationPath);
        if(destination.StartsWith(@"\\",StringComparison.Ordinal) ||
           !string.Equals(Path.GetExtension(destination),".altrecovery",
               StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a local .altrecovery file.",nameof(destinationPath));

        var authorization=new NativeRestoreReauthenticationGate(_db,_sessions);
        if(!await authorization.VerifyCurrentGeneralAdministratorPasswordAsync(
            actor,currentAdminPassword,ct).ConfigureAwait(false))
            return false;
        var stillActive=await _sessions.ResolveSessionAsync(actor.SessionNonce,ct)
            .ConfigureAwait(false);
        if(stillActive is null || stillActive.Role!=UserRole.GeneralAdministration ||
           stillActive.UserId!=actor.UserId || stillActive.Username!=actor.Username)
            return false;

        ct.ThrowIfCancellationRequested();
        // Original Windows-user DPAPI key: no silent creation, no overwrite,
        // full SQLCipher integrity check and atomic new-file creation.
        _keyStore.ExportPortableRecoveryKey(
            _db.DatabasePath,destination,recoveryPassphrase);
        return true;
    }
}
