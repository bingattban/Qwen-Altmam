using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class IdleAndSharedOutputTests : IAsyncLifetime,IDisposable
{
    private readonly string _dbPath=SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _output=Path.Combine(Path.GetTempPath(),
        "altamam-shared-output-"+Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db=null!;
    private readonly MovingTime _time=new();
    private BridgeSharedServices _services=null!;
    private BridgePrincipal _actor=null!;
    private sealed class MovingTime : TimeProvider
    {
        private DateTimeOffset _utc=new(2026,10,24,10,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>_utc;
        public void Forward(TimeSpan span)=>_utc=_utc.Add(span);
    }

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_dbPath,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _services=new BridgeSharedServices(_db,_time);
        var provision=await _services.ProvisionFirstAdministratorAsync(
            "tester-idle","test-password-123",CancellationToken.None);
        Assert.True(provision.Success,provision.UserMessage);
        var (user,error)=await _services.AuthenticateAsync(
            "tester-idle","test-password-123",CancellationToken.None);
        Assert.Null(error);
        _actor=Assert.IsType<BridgePrincipal>(user);
        Directory.CreateDirectory(_output);
    }

    [Fact]
    public async Task ServerSideIdleDeadline_ExpiresAndCannotBeRevived()
    {
        var initial=await _services.ResolveSessionAsync(_actor.SessionNonce);
        Assert.NotNull(initial);
        _time.Forward(TimeSpan.FromSeconds(299));
        Assert.NotNull(await _services.ResolveSessionAsync(_actor.SessionNonce));
        await _services.RefreshSessionIdleDeadlineAsync(_actor.SessionNonce);
        _time.Forward(TimeSpan.FromSeconds(299));
        Assert.NotNull(await _services.ResolveSessionAsync(_actor.SessionNonce));
        _time.Forward(TimeSpan.FromSeconds(1));
        Assert.Null(await _services.ResolveSessionAsync(_actor.SessionNonce));
        await _services.RefreshSessionIdleDeadlineAsync(_actor.SessionNonce);
        Assert.Null(await _services.ResolveSessionAsync(_actor.SessionNonce));
    }

    [Fact]
    public async Task ConcurrentLoginBlockedUntilLogout_AndRestartRevokesOldSessions()
    {
        var (other, denied)=await _services.AuthenticateAsync(
            "tester-idle","test-password-123",default);
        Assert.Null(other);
        Assert.NotNull(denied);
        await _services.RevokeSessionAsync(_actor.SessionNonce);
        Assert.Null(await _services.ResolveSessionAsync(_actor.SessionNonce));
        var (fresh, error)=await _services.AuthenticateAsync(
            "tester-idle","test-password-123",default);
        Assert.Null(error);
        Assert.NotNull(fresh);
        Assert.NotEqual(_actor.SessionNonce,fresh!.SessionNonce);
        await _services.RevokeOrphanedSessionsOnStartupAsync();
        Assert.Null(await _services.ResolveSessionAsync(fresh.SessionNonce));
        var (reopened, reopenError)=await _services.AuthenticateAsync(
            "tester-idle","test-password-123",default);
        Assert.Null(reopenError);
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task NativeUserInputRenewsOnlyStillActiveSessions()
    {
        _time.Forward(TimeSpan.FromSeconds(299));
        await _services.RefreshActiveSessionFromNativeInputAsync();
        _time.Forward(TimeSpan.FromSeconds(299));
        Assert.NotNull(await _services.ResolveSessionAsync(_actor.SessionNonce));
        _time.Forward(TimeSpan.FromSeconds(1));
        Assert.Null(await _services.ResolveSessionAsync(_actor.SessionNonce));
        await _services.RefreshActiveSessionFromNativeInputAsync();
        Assert.Null(await _services.ResolveSessionAsync(_actor.SessionNonce));
    }

    [Fact]
    public void SubsequentPasswordsAcceptExactEightCharacterMinimumWithArgon2id()
    {
        Assert.True(AlTamam.Domain.Policies.PasswordPolicyValidator.Validate("Abc123!z").IsValid);
        Assert.False(AlTamam.Domain.Policies.PasswordPolicyValidator.Validate("Abc123!").IsValid);
        Assert.False(AlTamam.Domain.Policies.PasswordPolicyValidator.Validate("Ab123!!z").IsValid);
        Assert.False(AlTamam.Domain.Policies.PasswordPolicyValidator.Validate("Abc12!!z").IsValid);
        var (digest,salt)=AlTamam.Infrastructure.Security.PasswordSecurity.HashPassword("Abc123!z");
        Assert.True(AlTamam.Infrastructure.Security.PasswordSecurity.VerifyPassword("Abc123!z",digest,salt));
        Assert.False(AlTamam.Infrastructure.Security.PasswordSecurity.VerifyPassword("Abc123!x",digest,salt));
        Assert.DoesNotContain("Abc123!z",digest);
    }

    [Fact]
    public async Task FoundingAdmin_UsesRoleLabelAndCannotBeRecreatedAfterDisable()
    {
        using(var c=_db.CreateOpenConnection())
        {
            using var cmd=c.CreateCommand();
            cmd.CommandText="""
                SELECT ActorRosterFullNameSnapshot FROM AuditEvents
                WHERE OperationCode='FOUNDING_ADMIN_CREATED' LIMIT 1;
                """;
            Assert.Equal("الإدارة العامة", Convert.ToString(cmd.ExecuteScalar()));
        }
        Assert.False((await _services.ProvisionFirstAdministratorAsync(
            "second-founder","test-password-123",CancellationToken.None)).Success);
        using(var c=_db.CreateOpenConnection())
        {
            using var cmd=c.CreateCommand();
            cmd.CommandText="UPDATE Users SET ActiveFlag=0 WHERE UserId=$id;";
            cmd.Parameters.AddWithValue("$id",_actor.UserId);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.True(await _services.IsProvisionedAsync());
        Assert.False((await _services.ProvisionFirstAdministratorAsync(
            "reopened-founder","test-password-123",CancellationToken.None)).Success);
    }

    [Fact]
    public async Task SharedOutputDirectory_ValidatesPersistsAndCreatesEncryptedBackup()
    {
        var settings=new OutputDirectorySettings(_db);
        var invalid=await settings.SaveAsync("..\\relative",_actor,CancellationToken.None);
        Assert.False(invalid.Success);
        var liveDb=await settings.SaveAsync(Path.GetDirectoryName(_dbPath),_actor,CancellationToken.None);
        Assert.False(liveDb.Success);
        var chosen=await settings.SaveAsync(_output,_actor,CancellationToken.None);
        Assert.True(chosen.Success,chosen.UserMessage);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_output)),
            await settings.GetAsync());
        var preview=await new SettingsReadBackupService(_db).OverviewAsync(_actor,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        Assert.Equal(await settings.GetAsync(),
            Assert.IsType<JsonObject>(preview.Data)["backupDirectory"]!.GetValue<string>());
        Assert.True(Assert.IsType<JsonObject>(preview.Data)["defaultDriveAvailable"]!.GetValue<bool>());
        var result=await new SettingsReadBackupService(_db).BackupAsync(_actor,CancellationToken.None);
        Assert.True(result.Success,result.UserMessage);
        var data=Assert.IsType<JsonObject>(result.Data);
        Assert.True(data["verificationPassed"]!.GetValue<bool>());
        string filename=data["path"]!.GetValue<string>();
        Assert.Equal(Path.GetFullPath(_output),Path.GetDirectoryName(filename));
        Assert.True(File.Exists(filename));
        Assert.Contains("dbbackup",Path.GetExtension(filename));
        using var restored=new AlTamamDbContext(filename,SyntheticFixtures.TestKeyHex);
        Assert.True(restored.HasValidIntegrity());
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_dbPath);
        try{Directory.Delete(_output,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
