using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using Xunit;

namespace AlTamam.Tests.Security;

/// <summary>
/// Section 03.08 / 04.08 regression: the WebView2 bridge accepts only the
/// exact offline HTTPS virtual origin, never alternate ports or credentialed URIs.
/// Host checks run before authentication or dispatch to any privileged handler.
/// </summary>
public sealed class BridgeExactOriginTests
{
    [Theory]
    [InlineData("https://manger.adwas.example/", true)]
    [InlineData("https://manger.adwas.example/screens/Dashboard/index.html", true)]
    [InlineData("https://MANGER.ADWAS.EXAMPLE:443/index.html", true)]
    [InlineData("https://manger.adwas.example:444/", false)]
    [InlineData("https://manger.adwas.example:4443/index.html", false)]
    [InlineData("https://user@manger.adwas.example/", false)]
    [InlineData("https://user:password@manger.adwas.example/", false)]
    [InlineData("http://manger.adwas.example/", false)]
    [InlineData("https://manger.adwas.example.evil.test/", false)]
    [InlineData("https://manger.adwas.example./", false)]
    [InlineData("about:blank", false)]
    public void BridgeOrigin_IsRestrictedToCanonicalVirtualHost(string url, bool permitted)
    {
        Assert.Equal(permitted, WebView2BridgeDispatcher.IsAllowedOrigin(url));
    }

    [Theory]
    [InlineData("https://manger.adwas.example:444/index.html")]
    [InlineData("https://user@manger.adwas.example/index.html")]
    [InlineData("https://user:password@manger.adwas.example/index.html")]
    public async Task HostileOrigin_IsRejectedBeforeAuthenticationOrBusinessHandler(string url)
    {
        int authenticated = 0;
        int executed = 0;
        const string nonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var dispatcher = new WebView2BridgeDispatcher(new[]
        {
            new BridgeActionRegistration(
                "TestSensitiveCommand", "Security", new[] { "GeneralAdministration" },
                IsMutating: true, AllowUnauthenticated: false,
                Handler: (request, principal, ct) =>
                {
                    executed++;
                    return Task.FromResult(new BridgeResponse(request.RequestId, true, new JsonObject()));
                })
        });
        var envelope = new JsonObject
        {
            ["requestId"] = "STRICT-ORIGIN-1",
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = "TestSensitiveCommand",
            ["sessionNonce"] = nonce,
            ["payload"] = new JsonObject()
        };
        Task<BridgePrincipal?> Authenticate(string incoming)
        {
            authenticated++;
            return Task.FromResult<BridgePrincipal?>(new BridgePrincipal(
                "TST-GA", "test-admin", UserRole.GeneralAdministration, incoming));
        }

        var result = await dispatcher.DispatchAsync(
            envelope.ToJsonString(), url, true, Authenticate);

        Assert.False(result.Response.Success);
        Assert.Equal(BridgeOutcomeKind.Unauthorized, result.Kind);
        Assert.Equal(BridgeErrorCodes.Unauthorized, result.Response.ErrorCode);
        Assert.Equal(0, authenticated);
        Assert.Equal(0, executed);
    }
}
