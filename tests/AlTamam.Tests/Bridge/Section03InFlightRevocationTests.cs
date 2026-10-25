using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Phase 03.10-03.12: logout revokes an in-flight request, not only future requests.</summary>
public sealed class Section03InFlightRevocationTests
{
    private const string Nonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Envelope(string action, string requestId) => new JsonObject
    {
        ["requestId"] = requestId,
        ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
        ["action"] = action,
        ["sessionNonce"] = Nonce,
        ["payload"] = new JsonObject()
    }.ToJsonString();

    private static WebView2BridgeDispatcher Dispatcher(BridgeActionHandler handler, bool mutating = false)
        => new([new BridgeActionRegistration(
            "GetCurrentSession", "AuthenticationService", ["GeneralAdministration"],
            mutating, false, handler)]);

    [Fact]
    public async Task RevokedSession_CancelsCooperativeInflightHandlerWithoutCompletingWork()
    {
        using var lifetime = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool completed = false;
        var principal = new BridgePrincipal("TST-GA", "owner", UserRole.GeneralAdministration, Nonce)
        { SessionRevocation = lifetime.Token };
        var dispatcher = Dispatcher(async (req, user, ct) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            completed = true;
            return new BridgeResponse(req.RequestId, true, new JsonObject { ["secret"] = "yes" });
        }, mutating: true);
        Task<BridgePrincipal?> Authenticate(string nonce) =>
            Task.FromResult<BridgePrincipal?>(lifetime.IsCancellationRequested ? null : principal);

        var pending = dispatcher.DispatchAsync(
            Envelope("GetCurrentSession", "TST-REVOKE-1"),
            LocalWebUiOrigin.BaseUrl, true, Authenticate);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lifetime.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BridgeOutcomeKind.Unauthorized, result.Kind);
        Assert.Equal(BridgeErrorCodes.SessionExpired, result.Response.ErrorCode);
        Assert.False(result.Response.Success);
        Assert.False(completed);
    }

    [Fact]
    public async Task RevokedSession_HidesSensitiveResponseEvenIfHandlerIgnoredCancellation()
    {
        using var lifetime = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var principal = new BridgePrincipal("TST-GA", "owner", UserRole.GeneralAdministration, Nonce)
        { SessionRevocation = lifetime.Token };
        var dispatcher = Dispatcher(async (req, user, ct) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return new BridgeResponse(req.RequestId, true, new JsonObject { ["privateRoster"] = "must-not-leak" });
        });
        Task<BridgePrincipal?> Authenticate(string nonce) =>
            Task.FromResult<BridgePrincipal?>(lifetime.IsCancellationRequested ? null : principal);

        var pending = dispatcher.DispatchAsync(
            Envelope("GetCurrentSession", "TST-REVOKE-2"),
            LocalWebUiOrigin.BaseUrl, true, Authenticate);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lifetime.Cancel();
        release.TrySetResult(true);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Response.Success);
        Assert.Equal(BridgeErrorCodes.SessionExpired, result.Response.ErrorCode);
        Assert.Null(result.Response.Data);
    }

    [Fact]
    public async Task RealSqlCipherSession_LogoutSignalsOldPrincipalBeforeReturning()
    {
        string path = SyntheticFixtures.CreateTempDatabasePath();
        try
        {
            using var db = new AlTamamDbContext(path, SyntheticFixtures.TestKeyHex);
            await db.InitializeAsync();
            var services = new BridgeSharedServices(db);
            var setup = await services.ProvisionFirstAdministratorAsync(
                "founderSessionTest", "test-password-123", CancellationToken.None);
            Assert.True(setup.Success, setup.UserMessage);
            var (session, failed) = await services.AuthenticateAsync(
                "founderSessionTest", "test-password-123", CancellationToken.None);
            Assert.NotNull(session);
            Assert.Null(failed);
            var principal = await services.ResolveSessionAsync(session!.SessionNonce);
            Assert.NotNull(principal);
            Assert.True(principal!.SessionRevocation.CanBeCanceled);
            await services.RevokeSessionAsync(session.SessionNonce);
            Assert.True(principal.SessionRevocation.IsCancellationRequested);
            Assert.Null(await services.ResolveSessionAsync(session.SessionNonce));
        }
        finally { SyntheticFixtures.DeleteTempDatabase(path); }
    }
}
