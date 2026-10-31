using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// PHASE 14 step 14.03 — systematic RBAC exercise of ALL 100 declared manifest actions,
/// independent of UI visibility (directive: "Exercise RBAC systematically on all 100 declared
/// manifest actions and all dangerous combinations, independent of UI visibility").
/// The dispatcher is the single server-side authorization gate; these tests drive it directly
/// with synthetic principals only (no DB, no real data) and assert allow/deny per role for
/// every action, plus the dangerous combinations (privilege escalation via payload, forged
/// nonces, unauthenticated access, replay, hostile envelopes).
/// </summary>
public sealed class BridgeRbacMatrixTests
{
    private const string NonceGa = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NonceOa = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string NonceSv = "cccccccccccccccccccccccccccccccc";
    private const string NonceUnknown = "dddddddddddddddddddddddddddddddd";

    private static readonly Dictionary<string, BridgePrincipal> PrincipalsByNonce = new(StringComparer.Ordinal)
    {
        [NonceGa] = new("TST-USER-GA", "tst-ga", UserRole.GeneralAdministration, NonceGa),
        [NonceOa] = new("TST-USER-OA", "tst-oa", UserRole.OrdinaryAdministration, NonceOa),
        [NonceSv] = new("TST-USER-SV", "tst-sv", UserRole.Supervisor, NonceSv),
    };

    private static Task<BridgePrincipal?> Authenticate(string nonce) =>
        Task.FromResult(PrincipalsByNonce.TryGetValue(nonce, out var p) ? p : null);

    private static WebView2BridgeDispatcher BuildAuditedDispatcher(out List<string> executedActions)
    {
        // Handlers are pure probes: they record that execution reached the handler and echo success.
        // RBAC must be decided entirely BEFORE this point; if a denied case ever records an entry,
        // the assertion fails with concrete evidence of the bypass.
        var executed = new List<string>();
        executedActions = executed;
        var registrations = ActionManifest.All.Select(meta => new BridgeActionRegistration(
            meta.Action,
            meta.OwningService,
            meta.AllowedRoles,
            IsMutating: false,               // RBAC matrix focus; replay is covered separately.
            meta.AllowedRoles.Contains("Unauthenticated", StringComparer.Ordinal),
            Handler: (req, principal, ct) =>
            {
                lock (executed) { executed.Add(req.Action); }
                return Task.FromResult(new BridgeResponse(req.RequestId, true, new JsonObject { ["rbacProbe"] = true }));
            })).ToList();
        return new WebView2BridgeDispatcher(registrations);
    }

    private static string Envelope(string action, string nonce, string requestId, JsonObject? payload = null)
    {
        var envelope = new JsonObject
        {
            ["requestId"] = requestId,
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = action,
            ["sessionNonce"] = nonce,
            ["payload"] = payload ?? new JsonObject()
        };
        return envelope.ToJsonString();
    }

    private const string ValidOrigin = LocalWebUiOrigin.BaseUrl;

    // ------------------------------------------------------------------ 14.03 core matrix

    [Theory]
    [MemberData(nameof(AllActionsByRole))]
    public async Task EveryManifestAction_RoleGateMatchesContract_IndependentOfUiVisibility(
        string action, string callerNonce, string[] expectedAllowedRoles)
    {
        var dispatcher = BuildAuditedDispatcher(out _);
        string callerRole = PrincipalsByNonce.ContainsKey(callerNonce)
            ? PrincipalsByNonce[callerNonce].Role.ToString()
            : "Unauthenticated";
        bool shouldAllow = expectedAllowedRoles.Contains(callerRole, StringComparer.Ordinal);

        BridgeDispatchResult result = await dispatcher.DispatchAsync(
            Envelope(action, callerNonce, $"RBAC-{Guid.NewGuid():N}"),
            ValidOrigin, isMainFrame: true, Authenticate);

        if (shouldAllow)
        {
            Assert.Equal(BridgeOutcomeKind.Success, result.Kind);
            Assert.True(result.Response.Success);
        }
        else
        {
            if (callerRole == "Unauthenticated")
            {
                Assert.Equal(BridgeOutcomeKind.Unauthorized, result.Kind);
                Assert.Equal(BridgeErrorCodes.SessionExpired, result.Response.ErrorCode);
            }
            else
            {
                Assert.Equal(BridgeOutcomeKind.Forbidden, result.Kind);
                Assert.Equal(BridgeErrorCodes.Unauthorized, result.Response.ErrorCode);
            }
            Assert.False(result.Response.Success);
        }
    }

    /// <summary>103 actions × 4 caller identities (GA/OA/SV/unauthenticated) = 412 cases.</summary>
    public static IEnumerable<object[]> AllActionsByRole()
    {
        foreach (var meta in ActionManifest.All)
        {
            yield return new object[] { meta.Action, NonceGa, meta.AllowedRoles };
            yield return new object[] { meta.Action, NonceOa, meta.AllowedRoles };
            yield return new object[] { meta.Action, NonceSv, meta.AllowedRoles };
            yield return new object[] { meta.Action, NonceUnknown, meta.AllowedRoles };
        }
    }

    [Fact]
    public void ManifestMirror_ContainsAll103DeclaredActions()
    {
        Assert.Equal(103, ActionManifest.All.Length);
        Assert.Equal(103, ActionManifest.All.Select(a => a.Action).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Dispatcher_RegistersEveryManifestAction_NoUiOnlyGating()
    {
        // The dispatcher set is built from the manifest alone; hidden screens cannot shrink or widen it.
        var dispatcher = BuildAuditedDispatcher(out _);
        Assert.Equal(
            ActionManifest.All.Select(a => a.Action).OrderBy(a => a, StringComparer.Ordinal),
            dispatcher.RegisteredActions.OrderBy(a => a, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ dangerous combinations

    [Theory]
    [InlineData(NonceSv)]      // supervisor forging GA in payload
    [InlineData(NonceOa)]      // ordinary admin forging GA in payload
    [InlineData(NonceGa)]      // GA session swapped to an unknown nonce
    public async Task PayloadForgedRole_IsNeverTrusted_ServerRoleWins(string callerNonce)
    {
        var dispatcher = BuildAuditedDispatcher(out List<string> executed);
        var payload = new JsonObject
        {
            ["role"] = "GeneralAdministration",
            ["allowedRoles"] = new JsonArray("GeneralAdministration")
        };
        // For SV/OA the forged role field must change nothing → Forbidden.
        // For GA we send an UNKNOWN session nonce while the role would allow → authentication gate denies.
        string nonceUsed = callerNonce == NonceGa ? NonceUnknown : callerNonce;
        BridgeDispatchResult result = await dispatcher.DispatchAsync(
            Envelope("CommitFullReset", nonceUsed, "FORGE-1", payload),
            ValidOrigin, isMainFrame: true, Authenticate);

        if (callerNonce == NonceGa)
            Assert.Equal(BridgeOutcomeKind.Unauthorized, result.Kind);
        else
        {
            Assert.Equal(BridgeOutcomeKind.Forbidden, result.Kind);
            Assert.Equal(BridgeErrorCodes.Unauthorized, result.Response.ErrorCode);
        }
        Assert.Empty(executed);
    }

    [Theory]
    [InlineData("CommitFullReset")]
    [InlineData("PreviewFullReset")]
    [InlineData("RestoreDatabaseAtomically")]
    [InlineData("CreateUserAccount")]
    [InlineData("ChangeUserPassword")]
    [InlineData("DeactivateUserAccount")]
    [InlineData("ReactivateUserAccount")]
    [InlineData("DeleteAuditPresentation")]
    [InlineData("UpdateAuditPresentation")]
    [InlineData("ArchivePersons")]
    [InlineData("CommitSeparation")]
    [InlineData("CommitExternalTransfer")]
    [InlineData("RemoveSiteAndReassignCurrentPersons")]
    [InlineData("UpdateBackupSchedule")]
    public async Task DangerousActions_SupervisorFollowsCanonicalRoles_UnauthenticatedAlwaysDenied(string action)
    {
        var dispatcher = BuildAuditedDispatcher(out List<string> executed);
        var manifestAction = ActionManifest.All.Single(a => a.Action == action);
        bool supervisorAllowed = manifestAction.AllowedRoles.Contains("Supervisor", StringComparer.Ordinal);

        BridgeDispatchResult supervisor = await dispatcher.DispatchAsync(
            Envelope(action, NonceSv, $"DNG-{action}-SV"),
            ValidOrigin, isMainFrame: true, Authenticate);
        Assert.Equal(supervisorAllowed ? BridgeOutcomeKind.Success : BridgeOutcomeKind.Forbidden, supervisor.Kind);
        Assert.Equal(supervisorAllowed, supervisor.Response.Success);
        Assert.Equal(supervisorAllowed ? new[] { action } : Array.Empty<string>(), executed);

        BridgeDispatchResult unknownSession = await dispatcher.DispatchAsync(
            Envelope(action, NonceUnknown, $"DNG-{action}-UNKNOWN"),
            ValidOrigin, isMainFrame: true, Authenticate);
        Assert.Equal(BridgeOutcomeKind.Unauthorized, unknownSession.Kind);
        Assert.Equal(BridgeErrorCodes.SessionExpired, unknownSession.Response.ErrorCode);
        Assert.False(unknownSession.Response.Success);
        Assert.Equal(supervisorAllowed ? new[] { action } : Array.Empty<string>(), executed);
    }

    [Fact]
    public async Task GaOnlyActions_OrdinaryAdministration_Denied_GeneralAdministration_Allowed()
    {
        // Contract §2.9 exclusive restrictions re-proven at the gate level.
        var dispatcher = BuildAuditedDispatcher(out List<string> executed);
        foreach (string action in new[] { "PreviewFullReset", "CommitFullReset" })
        {
            BridgeDispatchResult oa = await dispatcher.DispatchAsync(
                Envelope(action, NonceOa, $"GAONLY-{action}-OA"), ValidOrigin, true, Authenticate);
            Assert.Equal(BridgeOutcomeKind.Forbidden, oa.Kind);
        }
        BridgeDispatchResult ga = await dispatcher.DispatchAsync(
            Envelope("PreviewFullReset", NonceGa, "GAONLY-PreviewFullReset-GA"), ValidOrigin, true, Authenticate);
        Assert.Equal(BridgeOutcomeKind.Success, ga.Kind);
        Assert.Equal(new[] { "PreviewFullReset" }, executed);
    }

    [Fact]
    public async Task UserHierarchy_CrossTierCreation_DeniedByManifestRoles()
    {
        // Supervisor must not reach user management at all; OA may list/create but GetAllowedRoles
        // response semantics (which roles the ACTOR may mint) belong to the handler contract —
        // here we prove the gate: SV forbidden on every UserAuditService account action.
        var dispatcher = BuildAuditedDispatcher(out List<string> executed);
        foreach (string action in new[] { "ListUserAccounts", "SearchActivePersonsForAccount", "GetAllowedRoles",
                                          "CreateUserAccount", "ChangeUserPassword", "DeactivateUserAccount", "ReactivateUserAccount" })
        {
            BridgeDispatchResult sv = await dispatcher.DispatchAsync(
                Envelope(action, NonceSv, $"HIER-{action}-SV"), ValidOrigin, true, Authenticate);
            Assert.Equal(BridgeOutcomeKind.Forbidden, sv.Kind);
            BridgeDispatchResult oa = await dispatcher.DispatchAsync(
                Envelope(action, NonceOa, $"HIER-{action}-OA"), ValidOrigin, true, Authenticate);
            Assert.Equal(BridgeOutcomeKind.Success, oa.Kind);
        }
        Assert.Equal(7, executed.Count);
    }

    [Fact]
    public async Task UnknownAction_IsRejected_ForEveryRole()
    {
        var dispatcher = BuildAuditedDispatcher(out List<string> executed);
        foreach (string nonce in new[] { NonceGa, NonceOa, NonceSv, NonceUnknown })
        {
            BridgeDispatchResult result = await dispatcher.DispatchAsync(
                Envelope("EscalatePrivilegesNow", nonce, $"UNK-{nonce[..4]}"), ValidOrigin, true, Authenticate);
            Assert.Equal(BridgeOutcomeKind.NotFound, result.Kind);
            Assert.Equal(BridgeErrorCodes.ValidationError, result.Response.ErrorCode);
        }
        Assert.Empty(executed);
    }

    [Fact]
    public async Task RoleClaimInsideEnvelope_TopLevelExtraField_CannotBypassGate()
    {
        // Even a schema-valid-looking extra member is ignored: authorization reads only the
        // server-side principal. Unauthenticated caller + injected role claim stays denied.
        var dispatcher = BuildAuditedDispatcher(out List<string> executed);
        var envelope = new JsonObject
        {
            ["requestId"] = "INJ-1",
            ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = "CommitFullReset",
            ["sessionNonce"] = NonceUnknown,
            ["role"] = "GeneralAdministration",
            ["payload"] = new JsonObject()
        };
        BridgeDispatchResult result = await dispatcher.DispatchAsync(
            envelope.ToJsonString(), ValidOrigin, true, Authenticate);
        Assert.Equal(BridgeOutcomeKind.Unauthorized, result.Kind);
        Assert.Empty(executed);
    }

    public static IEnumerable<object[]> AllMutationsExceptLogout() =>
        ActionManifest.All.Where(a => a.IsMutating && a.Action != "Logout")
            .Select(a => new object[] { a.Action });

    [Theory]
    [MemberData(nameof(AllMutationsExceptLogout))]
    public async Task EveryDomainMutation_MissingOrMalformedOperationId_FailsBeforeHandler(string action)
    {
        int executed=0;
        var registration=new BridgeActionRegistration(action,"SyntheticHandler",
            new[]{"GeneralAdministration"},true,false,
            (req,p,ct)=>{
                Interlocked.Increment(ref executed);
                return Task.FromResult(new BridgeResponse(req.RequestId,true));
            });
        var dispatcher=new WebView2BridgeDispatcher(new[]{registration});
        foreach(var value in new string?[]{null,"short","illegal space in identifier"})
        {
            var payload=new JsonObject();
            if(value is not null)payload["operationId"]=value;
            var result=await dispatcher.DispatchAsync(
                Envelope(action,NonceGa,Guid.NewGuid().ToString("N"),payload),
                ValidOrigin,true,Authenticate);
            Assert.False(result.Response.Success);
            Assert.Equal(BridgeErrorCodes.ValidationError,result.Response.ErrorCode);
        }
        Assert.Equal(0,executed);
        var accepted=await dispatcher.DispatchAsync(
            Envelope(action,NonceGa,Guid.NewGuid().ToString("N"),
                new JsonObject{["operationId"]="section02-required-intent-20261029"}),
            ValidOrigin,true,Authenticate);
        Assert.Equal(BridgeOutcomeKind.Success,accepted.Kind);
        Assert.Equal(1,executed);
    }

    [Fact]
    public async Task MutatingReplay_SameRequestIdSecondAttempt_ConflictsWithNoSecondExecution()
    {
        int executions = 0;
        var registration = new BridgeActionRegistration(
            "SubmitAttendance", "AttendanceService",
            new[] { "GeneralAdministration", "OrdinaryAdministration", "Supervisor" },
            IsMutating: true, AllowUnauthenticated: false,
            Handler: (req, principal, ct) =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new BridgeResponse(req.RequestId, true));
            });
        var dispatcher = new WebView2BridgeDispatcher(new[] { registration });

        BridgeDispatchResult first = await dispatcher.DispatchAsync(
            Envelope("SubmitAttendance", NonceSv, "REPLAY-1",
                new JsonObject{["operationId"]="section02-replay-unique-intent"}), ValidOrigin, true, Authenticate);
        BridgeDispatchResult second = await dispatcher.DispatchAsync(
            Envelope("SubmitAttendance", NonceSv, "REPLAY-1",
                new JsonObject{["operationId"]="section02-replay-unique-intent"}), ValidOrigin, true, Authenticate);

        Assert.Equal(BridgeOutcomeKind.Success, first.Kind);
        Assert.Equal(BridgeOutcomeKind.Conflict, second.Kind);
        Assert.Equal(BridgeErrorCodes.ConflictStale, second.Response.ErrorCode);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task StaleRevisionGuard_RejectsConcurrentOverwrite_BeforeHandler()
    {
        var registration = new BridgeActionRegistration(
            "ReviseTodayAttendance", "AttendanceService",
            new[] { "GeneralAdministration", "OrdinaryAdministration", "Supervisor" },
            IsMutating: true, AllowUnauthenticated: false,
            Handler: (req, principal, ct) => Task.FromResult(new BridgeResponse(req.RequestId, true)));
        var dispatcher = new WebView2BridgeDispatcher(new[] { registration });
        var payload = new JsonObject { ["expectedRevision"] = 7, ["baseRevision"] = 5,
            ["operationId"]="section02-stale-write-intent" };

        BridgeDispatchResult result = await dispatcher.DispatchAsync(
            Envelope("ReviseTodayAttendance", NonceSv, "REV-1", payload), ValidOrigin, true, Authenticate);

        Assert.Equal(BridgeOutcomeKind.Conflict, result.Kind);
        Assert.Equal(BridgeErrorCodes.ConflictStale, result.Response.ErrorCode);
    }

    [Fact]
    public async Task OriginAndFrameGuards_FireBeforeAuthentication_AndDenyEverything()
    {
        var dispatcher = BuildAuditedDispatcher(out List<string> executed);
        string msg = Envelope("CommitFullReset", NonceGa, "ORIGIN-1");

        foreach (string? badOrigin in new string?[]
        {
            null, "", "http://manger.adwas.example/", "https://evil.example/",
            "https://manger.adwas.example.evil.test/", "file:///C:/windows/index.html",
            "https://ADWAS.example/"
        })
        {
            BridgeDispatchResult result = await dispatcher.DispatchAsync(msg, badOrigin, true, Authenticate);
            Assert.Equal(BridgeOutcomeKind.Unauthorized, result.Kind);
        }
        // Sub-frames never reach the gate even from the pinned origin.
        BridgeDispatchResult iframe = await dispatcher.DispatchAsync(msg, ValidOrigin, isMainFrame: false, Authenticate);
        Assert.Equal(BridgeOutcomeKind.Unauthorized, iframe.Kind);
        Assert.Empty(executed);
    }

    [Fact]
    public async Task MalformedHostileEnvelopes_NeverThrow_AndNeverReachHandlers()
    {
        var dispatcher = BuildAuditedDispatcher(out List<string> executed);
        string[] hostile =
        {
            "",
            "   ",
            "not json at all {{{",
            "{\"requestId\":123}",                                   // wrong types
            "[1,2,3]",                                               // array root
            "\"a string root\"",                                     // string root
            "null",                                                  // JSON null root
            "{\"requestId\":\"X\",\"protocolVersion\":99,\"action\":\"CommitFullReset\",\"sessionNonce\":\"" + NonceGa + "\",\"payload\":{}}", // version mismatch
            "{\"requestId\":\"X\",\"protocolVersion\":1,\"action\":\"CommitFullReset\",\"sessionNonce\":\"short\",\"payload\":{}}",           // bad nonce format
            "{\"requestId\":\"" + new string('R', 65) + "\",\"protocolVersion\":1,\"action\":\"GetCurrentSession\",\"sessionNonce\":\"" + NonceGa + "\",\"payload\":{}}", // oversized id
            "{\"requestId\":\"X\",\"protocolVersion\":1,\"action\":\"GetCurrentSession\",\"sessionNonce\":\"" + NonceGa + "\",\"payload\":" + new string('[', 40) + "{}" + new string(']', 40) + "}", // deep nesting probe
            "\0\u0001\u0002 binary garbage \u0000",
        };
        foreach (string raw in hostile)
        {
            BridgeDispatchResult result = await dispatcher.DispatchAsync(raw, ValidOrigin, true, Authenticate);
            Assert.NotEqual(BridgeOutcomeKind.Success, result.Kind);
            Assert.False(result.Response.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Response.ErrorCode));
        }
        Assert.Empty(executed);
    }

    [Fact]
    public async Task OversizedPayload_RejectedByHardByteLimit()
    {
        var dispatcher = BuildAuditedDispatcher(out List<string> executed);
        string huge = Envelope("GetCurrentSession", NonceGa, "SIZE-1",
            new JsonObject { ["blob"] = new string('x', BridgeProtocol.MaxRequestBytes + 1024) });
        BridgeDispatchResult result = await dispatcher.DispatchAsync(huge, ValidOrigin, true, Authenticate);
        Assert.Equal(BridgeOutcomeKind.ProtocolError, result.Kind);
        Assert.Equal(BridgeErrorCodes.ValidationError, result.Response.ErrorCode);
        Assert.Empty(executed);
    }

    [Fact]
    public async Task HandlerCrash_AgainstTrustedAction_DoesNotLeakDetailsOrEscapeEnvelope()
    {
        var registration = new BridgeActionRegistration(
            "QuerySites", "ConfigurationService",
            new[] { "GeneralAdministration", "OrdinaryAdministration", "Supervisor" },
            IsMutating: false, AllowUnauthenticated: false,
            Handler: (req, principal, ct) => throw new InvalidOperationException("secret internal detail"));
        var dispatcher = new WebView2BridgeDispatcher(new[] { registration });

        BridgeDispatchResult result = await dispatcher.DispatchAsync(
            Envelope("QuerySites", NonceGa, "CRASH-1"), ValidOrigin, true, Authenticate);

        Assert.Equal(BridgeOutcomeKind.HandlerError, result.Kind);
        Assert.DoesNotContain("secret internal detail", result.Response.UserMessage ?? "");
    }
}
