using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Actual SQLCipher credential + session + backup staging integration.
/// Native-only gate: never exercises the destructive restore bridge action.</summary>
public sealed class NativeRestoreReauthenticationGateTests : IAsyncLifetime, IDisposable
{
    private const string Password = "Synthetic#RestoreStrong123";
    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _sessions = null!;
    private BridgePrincipal _principal = null!;
    private string _backup = "";
    private string _sha = "";

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _sessions = new BridgeSharedServices(_db);
        var setup = await _sessions.ProvisionFirstAdministratorAsync(
            "test-restore-native-ga", Password, CancellationToken.None);
        Assert.True(setup.Success, setup.UserMessage);
        var (principal, error) = await _sessions.AuthenticateAsync(
            "test-restore-native-ga", Password, CancellationToken.None);
        Assert.Null(error);
        _principal = Assert.IsType<BridgePrincipal>(principal);
        _backup = Path.Combine(Path.GetDirectoryName(_dbPath)!, "native-restore-input.dbbackup");
        await _db.CreateVerifiedEncryptedBackupAsync(_backup);
        _sha = (await _db.InspectEncryptedBackupAsync(_backup)).Sha256;
        using var db = _db.CreateOpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AppSettings(SettingKey, SettingValue, UpdatedAtUtc)
            VALUES('NativeRestoreSentinel','current-live-record','2026-10-28T00:00:00Z');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private string ReadLiveMarker()
    {
        using var db = _db.CreateOpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey='NativeRestoreSentinel';";
        return Assert.IsType<string>(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task FreshGeneralAdminPassword_StagesVerifiedBackupWithoutSwitchingLiveDb()
    {
        var gate = new NativeRestoreReauthenticationGate(_db, _sessions);
        var grant = await gate.AuthorizeAsync(_principal, Password, _backup, _sha);
        Assert.NotNull(grant);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, ".altamam-restore-stage-*.dbbackup"));
        Assert.Equal("current-live-record", ReadLiveMarker());

        StagedRestorePreparation? staged = await gate.ConsumeAndStageAsync(grant, _principal);
        Assert.NotNull(staged);
        Assert.Equal(_sha, staged!.StageSha256, ignoreCase: true);
        Assert.True(File.Exists(staged.StageFilePath));
        Assert.True(File.Exists(staged.RescueFilePath));
        Assert.Equal("current-live-record", ReadLiveMarker());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dbPath)!,
            ".altamam-restore-activation.pending.json")));
        Assert.Null(await gate.ConsumeAndStageAsync(grant, _principal)); // one-shot grant
    }

    [Fact]
    public async Task WrongPassword_CannotGenerateStageOrApproval()
    {
        var gate = new NativeRestoreReauthenticationGate(_db, _sessions);
        Assert.Null(await gate.AuthorizeAsync(_principal, "Wrong#Password987", _backup, _sha));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, ".altamam-restore-stage-*.dbbackup"));
        Assert.Equal("current-live-record", ReadLiveMarker());
    }

    [Fact]
    public async Task ForgedRoleOrUnknownDigest_RejectsBeforeAnyBackupSideEffects()
    {
        var gate = new NativeRestoreReauthenticationGate(_db, _sessions);
        var forged = new BridgePrincipal(_principal.UserId, _principal.Username,
            UserRole.Supervisor, _principal.SessionNonce);
        Assert.Null(await gate.AuthorizeAsync(forged, Password, _backup, _sha));
        Assert.Null(await gate.AuthorizeAsync(_principal, Password, _backup, new string('F', 64)));
        Assert.Null(await gate.AuthorizeAsync(_principal, Password, _backup, "not-sha256"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, ".altamam-restore-stage-*.dbbackup"));
        Assert.Equal("current-live-record", ReadLiveMarker());
    }

    [Fact]
    public async Task RevokedSession_CannotUsePreviouslyIssuedNativeGrant()
    {
        var gate = new NativeRestoreReauthenticationGate(_db, _sessions);
        var grant = await gate.AuthorizeAsync(_principal, Password, _backup, _sha);
        Assert.NotNull(grant);
        await _sessions.RevokeSessionAsync(_principal.SessionNonce);
        Assert.Null(await gate.ConsumeAndStageAsync(grant, _principal));
        Assert.Equal("current-live-record", ReadLiveMarker());
    }

    [Fact]
    public async Task ExpiredGrant_CannotStage_AndIsBurnedOnFirstAttempt()
    {
        var clock = new MoveableClock();
        var gate = new NativeRestoreReauthenticationGate(_db, _sessions, clock);
        var grant = await gate.AuthorizeAsync(_principal, Password, _backup, _sha);
        Assert.NotNull(grant);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await gate.ConsumeAndStageAsync(grant, _principal));
        Assert.Null(await gate.ConsumeAndStageAsync(grant, _principal));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, ".altamam-restore-stage-*.dbbackup"));
    }

    [Fact]
    public async Task FiveBadPasswordAttempts_LockOutRestoreReauthLikeNormalLogin()
    {
        var gate = new NativeRestoreReauthenticationGate(_db, _sessions);
        for(var attempt=0;attempt<5;attempt++)
            Assert.Null(await gate.AuthorizeAsync(_principal,
                "Incorrect#Pass0001", _backup, _sha));
        Assert.Null(await gate.AuthorizeAsync(_principal, Password, _backup, _sha));
        using var db = _db.CreateOpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT FailedLoginCount FROM Users WHERE UserId=$id;";
        cmd.Parameters.AddWithValue("$id", _principal.UserId);
        Assert.Equal(5L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task EditedBackupAfterApproval_CannotBecomeStagedBackup()
    {
        var gate = new NativeRestoreReauthenticationGate(_db, _sessions);
        var grant = await gate.AuthorizeAsync(_principal, Password, _backup, _sha);
        Assert.NotNull(grant);
        await File.AppendAllTextAsync(_backup, "corrupted-after-approval");
        Assert.Null(await gate.ConsumeAndStageAsync(grant, _principal));
        Assert.Equal("current-live-record", ReadLiveMarker());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, ".altamam-restore-stage-*.dbbackup"));
    }

    private sealed class MoveableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        if(File.Exists(_backup)) File.Delete(_backup);
        SyntheticFixtures.DeleteTempDatabase(_dbPath);
    }
}
