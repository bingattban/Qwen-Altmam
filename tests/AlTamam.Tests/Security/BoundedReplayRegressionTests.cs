using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using Xunit;

namespace AlTamam.Tests.Security;

public sealed class BoundedReplayRegressionTests
{
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string Origin = LocalWebUiOrigin.BaseUrl;

    private static string Envelope(string nonce, string id) => new JsonObject
    {
        ["requestId"] = id, ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
        ["action"] = "SubmitAttendance", ["sessionNonce"] = nonce,
        ["payload"] = new JsonObject { ["operationId"] = "bounded-replay-" + id }
    }.ToJsonString();

    [Fact]
    public async Task ReplayIsBlockedWithoutRejectingSameRequestIdInDifferentValidSessions()
    {
        using var revoked = new CancellationTokenSource();
        var primary = new BridgePrincipal("person-one", "user1", UserRole.Supervisor, First)
        { SessionRevocation = revoked.Token };
        var secondary = new BridgePrincipal("person-two", "user2", UserRole.Supervisor, Second);
        int executions = 0;
        var dispatcher = new WebView2BridgeDispatcher(new[]
        {
            new BridgeActionRegistration("SubmitAttendance", "AttendanceCommitService",
                ["Supervisor"], IsMutating: true, AllowUnauthenticated: false,
                Handler: (req, p, ct) =>
                {
                    Interlocked.Increment(ref executions);
                    return Task.FromResult(new BridgeResponse(req.RequestId, true));
                })
        });
        Task<BridgePrincipal?> Authenticate(string nonce) =>
            Task.FromResult(nonce == First && !revoked.IsCancellationRequested
                ? primary : nonce == Second ? secondary : null);

        var accepted = await dispatcher.DispatchAsync(Envelope(First, "same-request"), Origin, true, Authenticate);
        var duplicate = await dispatcher.DispatchAsync(Envelope(First, "same-request"), Origin, true, Authenticate);
        Assert.True(accepted.Response.Success);
        Assert.Equal(BridgeOutcomeKind.Conflict, duplicate.Kind);
        Assert.Equal(1, executions);

        revoked.Cancel();
        var oldSession = await dispatcher.DispatchAsync(Envelope(First, "new-operation"), Origin, true, Authenticate);
        Assert.Equal(BridgeOutcomeKind.Unauthorized, oldSession.Kind);

        var newSession = await dispatcher.DispatchAsync(Envelope(Second, "same-request"), Origin, true, Authenticate);
        Assert.True(newSession.Response.Success);
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task CapacityLimitFailsClosedRatherThanEvictingAReusableRequestId()
    {
        var principal = new BridgePrincipal("person-three", "user3", UserRole.Supervisor, First);
        int executions = 0;
        var dispatcher = new WebView2BridgeDispatcher(new[]
        {
            new BridgeActionRegistration("SubmitAttendance", "AttendanceCommitService",
                ["Supervisor"], IsMutating: true, AllowUnauthenticated: false,
                Handler: (req, p, ct) =>
                {
                    Interlocked.Increment(ref executions);
                    return Task.FromResult(new BridgeResponse(req.RequestId, true));
                })
        });
        Task<BridgePrincipal?> Auth(string _) => Task.FromResult<BridgePrincipal?>(principal);
        for (int i = 0; i < 32768; i++)
        {
            var result = await dispatcher.DispatchAsync(
                Envelope(First, "bounded-" + i.ToString("D6")), Origin, true, Auth);
            Assert.True(result.Response.Success, "Failure at " + i);
        }

        var atLimit = await dispatcher.DispatchAsync(Envelope(First, "bounded-new"), Origin, true, Auth);
        Assert.Equal(BridgeOutcomeKind.Conflict, atLimit.Kind);
        Assert.Equal(32768, executions);
        var replay = await dispatcher.DispatchAsync(Envelope(First, "bounded-000000"), Origin, true, Auth);
        Assert.Equal(BridgeOutcomeKind.Conflict, replay.Kind);
        Assert.Equal(32768, executions);
    }
}
