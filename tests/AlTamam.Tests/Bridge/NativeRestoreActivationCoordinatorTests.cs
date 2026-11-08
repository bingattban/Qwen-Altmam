using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Actual encrypted SQLCipher candidate, native reauthentication, host-quiescence
/// boundary, and disk replacement. No customer profile or application UI used.
/// </summary>
[Collection("SQLCipherGlobalPool")]
public sealed class NativeRestoreActivationCoordinatorTests : IDisposable
{
    private const string Password = "Synthetic123";
    private static readonly string Key = SyntheticFixtures.TestKeyHex;
    private readonly string _live = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _sessions = null!;
    private BridgePrincipal _founder = null!;
    private string _candidate = "";
    private string _sha = "";

    private async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_live, Key, useConnectionPooling: false);
        await _db.InitializeAsync();
        _sessions = new BridgeSharedServices(_db);
        var founded = await _sessions.ProvisionFirstAdministratorAsync(
            "restore-native-owner", Password, CancellationToken.None);
        Assert.True(founded.Success, founded.UserMessage);
        var (actor, error) = await _sessions.AuthenticateAsync(
            "restore-native-owner", Password, CancellationToken.None);
        Assert.Null(error);
        _founder = Assert.IsType<BridgePrincipal>(actor);
        _candidate = Path.Combine(Path.GetDirectoryName(_live)!,
            "restore-native-source.dbbackup");
        await _db.CreateVerifiedEncryptedBackupAsync(_candidate);
        _sha = (await _db.InspectEncryptedBackupAsync(_candidate)).Sha256;
        using var connection = _db.CreateOpenConnection();
        using var marker = connection.CreateCommand();
        marker.CommandText = """
            INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
            VALUES('NativeRestoreCutoverMarker','changed-after-backup','2026-11-08T00:00:00Z');
            """;
        await marker.ExecuteNonQueryAsync();
    }

    private string? ReadMarker(string path)
    {
        using var db = new AlTamamDbContext(path, Key, useConnectionPooling: false);
        using var connection = db.CreateOpenConnection();
        using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT SettingValue FROM AppSettings WHERE SettingKey='NativeRestoreCutoverMarker';";
        return query.ExecuteScalar()?.ToString();
    }

    [Fact]
    public async Task NativeApprovalAndDrain_ActivateOnlyAfterQuiescence_AndPreserveEncryptedRollback()
    {
        await InitializeAsync();
        bool reviewed = false, drained = false;
        var coordinator = new NativeRestoreActivationCoordinator(_db, _sessions);
        var outcome = await coordinator.TryActivateAsync(
            _founder, Password, _candidate, _sha, Key,
            (staged, ct) =>
            {
                Assert.True(File.Exists(staged.StageFilePath));
                Assert.True(File.Exists(staged.RescueFilePath));
                Assert.Equal("changed-after-backup", ReadMarker(_live));
                reviewed = true;
                return Task.FromResult(true);
            },
            ct =>
            {
                Assert.True(reviewed);
                drained = true;
                return Task.CompletedTask;
            });
        Assert.NotNull(outcome);
        Assert.True(drained);
        Assert.Null(ReadMarker(_live));
        Assert.Equal("changed-after-backup", ReadMarker(outcome!.RollbackPath));
        Assert.True(File.Exists(outcome.PreparationRescuePath));
        Assert.True(File.Exists(outcome.RollbackPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_live)!,
            ".altamam-restore-activation.pending.json")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task DeniedPasswordOrNativeConfirmation_NeverStopsHostOrReplacesDatabase(
        bool correctPassword, bool approval)
    {
        await InitializeAsync();
        bool drained = false;
        var coordinator = new NativeRestoreActivationCoordinator(_db, _sessions);
        var outcome = await coordinator.TryActivateAsync(
            _founder, correctPassword ? Password : "Invalid123",
            _candidate, _sha, Key,
            (_, _) => Task.FromResult(approval),
            _ => { drained = true; return Task.CompletedTask; });
        Assert.Null(outcome);
        Assert.False(drained);
        Assert.Equal("changed-after-backup", ReadMarker(_live));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_live)!,
            ".altamam-restore-activation.pending.json")));
    }

    [Fact]
    public async Task HostDrainFailure_NeverStartsIrreversibleSwitch()
    {
        await InitializeAsync();
        var coordinator = new NativeRestoreActivationCoordinator(_db, _sessions);
        await Assert.ThrowsAsync<IOException>(() => coordinator.TryActivateAsync(
            _founder, Password, _candidate, _sha, Key,
            (_, _) => Task.FromResult(true),
            _ => throw new IOException("synthetic bridge or scheduler still active")));
        Assert.Equal("changed-after-backup", ReadMarker(_live));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_live)!,
            ".altamam-restore-activation.pending.json")));
    }

    [Fact]
    public async Task ChangedCandidateAfterInspection_FailsClosedBeforeHostDrain()
    {
        await InitializeAsync();
        await File.AppendAllTextAsync(_candidate, "tampered");
        bool drainAttempted = false;
        var coordinator = new NativeRestoreActivationCoordinator(_db, _sessions);
        Assert.Null(await coordinator.TryActivateAsync(
            _founder, Password, _candidate, _sha, Key,
            (_, _) => Task.FromResult(true),
            _ => { drainAttempted = true; return Task.CompletedTask; }));
        Assert.False(drainAttempted);
        Assert.Equal("changed-after-backup", ReadMarker(_live));
    }

    [Fact]
    public async Task WrongActivationKey_IsRejectedBeforeFinalApprovalOrHostShutdown()
    {
        await InitializeAsync();
        bool reviewed = false, drained = false;
        var coordinator = new NativeRestoreActivationCoordinator(_db, _sessions);
        var outcome = await coordinator.TryActivateAsync(
            _founder, Password, _candidate, _sha, new string('D', 64),
            (_, _) => { reviewed = true; return Task.FromResult(true); },
            _ => { drained = true; return Task.CompletedTask; });
        Assert.Null(outcome);
        Assert.False(reviewed);
        Assert.False(drained);
        Assert.Equal("changed-after-backup", ReadMarker(_live));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_live)!,
            ".altamam-restore-activation.pending.json")));
    }

    [Fact]
    public async Task RegisteredEncryptedBackupWithMismatchedCatalogSha_CannotBeActivated()
    {
        await InitializeAsync();
        using (var connection = _db.CreateOpenConnection())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO BackupHistory(BackupId,BackupFilePath,Sha256Checksum,
                    SchemaVersion,IsAutomatic,VerificationPassed,KeyProtectionMode,
                    CreatedByUserId,CreatedAtUtc,NotesArabic)
                VALUES('TST-RESTORE-CATALOG-MISMATCH',$path,$digest,$version,0,1,
                    'SQLCipher','USR-SYSTEM','2026-11-08T00:00:00Z','فحص بصمة النسخة');
                """;
            insert.Parameters.AddWithValue("$path", Path.GetFullPath(_candidate));
            insert.Parameters.AddWithValue("$digest", new string('A', 64));
            insert.Parameters.AddWithValue("$version", AlTamamDbContext.CurrentSchemaVersion);
            await insert.ExecuteNonQueryAsync();
        }
        bool reviewed = false, drained = false;
        var coordinator = new NativeRestoreActivationCoordinator(_db, _sessions);
        var outcome = await coordinator.TryActivateAsync(
            _founder, Password, _candidate, _sha, Key,
            (_, _) => { reviewed = true; return Task.FromResult(true); },
            _ => { drained = true; return Task.CompletedTask; });
        Assert.Null(outcome);
        Assert.False(reviewed);
        Assert.False(drained);
        Assert.Equal("changed-after-backup", ReadMarker(_live));
    }

    [Fact]
    public async Task CanceledInFlightQuiescence_CannotHangOrStartDatabaseReplacement()
    {
        await InitializeAsync();
        using var canceled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverDrains = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new NativeRestoreActivationCoordinator(_db, _sessions);
        Task<NativeRestoreActivationCoordinator.Result?> restoring =
            coordinator.TryActivateAsync(
                _founder, Password, _candidate, _sha, Key,
                (_, _) => Task.FromResult(true),
                _ =>
                {
                    entered.SetResult();
                    return neverDrains.Task;
                },
                canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        canceled.Cancel();
        Task completed = await Task.WhenAny(restoring, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(restoring, completed); // RED if the native drain ignores cancellation
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restoring);
        Assert.Equal("changed-after-backup", ReadMarker(_live));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_live)!,
            ".altamam-restore-activation.pending.json")));
    }

    public void Dispose()
    {
        _db?.Dispose();
        SqliteConnection.ClearAllPools();
        SyntheticFixtures.DeleteTempDatabase(_live);
    }
}
