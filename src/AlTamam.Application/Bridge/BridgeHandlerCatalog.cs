using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Domain.Enums;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Builds the complete dispatcher registration set from the canonical action manifest.
/// Phase 04 wires authentic handlers for the shared-screen actions (provisioning, authentication,
/// session, sites, statuses, person search, drafts). Every other canonical action is registered with
/// its manifest ownership metadata and a truthful "not yet implemented" response — so unknown actions
/// are rejected while known-but-unwired actions fail honestly instead of pretending success.
/// </summary>
public static class BridgeHandlerCatalog
{
    public static WebView2BridgeDispatcher Create(BridgeSharedServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registrations = new List<BridgeActionRegistration>(ActionManifest.All.Length);

        foreach (var meta in ActionManifest.All)
        {
            bool allowUnauthenticated = meta.AllowedRoles.Contains("Unauthenticated", StringComparer.Ordinal);
            BridgeActionHandler handler = BuildHandler(meta.Action, services);
            registrations.Add(new BridgeActionRegistration(
                meta.Action, meta.OwningService, meta.AllowedRoles, meta.IsMutating, allowUnauthenticated, handler));
        }
        return new WebView2BridgeDispatcher(registrations);
    }

    private static BridgeActionHandler BuildHandler(string action, BridgeSharedServices services) => action switch
    {
        "GetProvisioningState" => async (req, principal, ct) =>
        {
            bool provisioned = await services.IsProvisionedAsync(ct).ConfigureAwait(false);
            return Done(req, new JsonObject { ["provisioned"] = provisioned });
        },
        "AuthenticateLocalUser" => async (req, principal, ct) =>
        {
            string username = req.Payload?["username"]?.GetValue<string>() ?? "";
            // Provisioning bootstrap: when no account exists yet, the first call creates the administrator.
            if (!await services.IsProvisionedAsync(ct).ConfigureAwait(false))
            {
                string password0 = req.Payload?["password"]?.GetValue<string>() ?? "";
                BridgeResponse created = await services.ProvisionFirstAdministratorAsync(username, password0, ct).ConfigureAwait(false);
                if (!created.Success) return Fail(req, created);
            }
            string password = req.Payload?["password"]?.GetValue<string>() ?? "";
            var (authPrincipal, failure) = await services.AuthenticateAsync(username, password, ct).ConfigureAwait(false);
            if (authPrincipal is null) return Fail(req, failure!);
            // Authentication creates a session; it must not broadcast SessionEnded.
            return Done(req, SessionData(authPrincipal));
        },
        "GetCurrentSession" => async (req, principal, ct) =>
            principal is null
                ? Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.SessionExpired, "لا توجد جلسة نشطة."))
                : Done(req, SessionData(principal)),
        "Logout" => async (req, principal, ct) =>
        {
            await services.RevokeSessionAsync(req.SessionNonce, ct).ConfigureAwait(false);
            return Done(req, new JsonObject { ["loggedOut"] = true }, Invalidation(UiInvalidationEvents.SessionEnded));
        },
        // Real read-only projections: all SQL executes locally against SQLCipher.
        "GetDashboardSummary" => async (req, principal, ct) =>
        {
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.DashboardSummaryAsync(req.Payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "QueryDailyRoster" => async (req, principal, ct) =>
        {
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.DailyRosterAsync(req.Payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "GetAvailableArchivePeriods" => async (req, principal, ct) =>
        {
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.ArchivePeriodsAsync(ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "QueryMonthlyGrid" => async (req, principal, ct) =>
        {
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.MonthlyGridAsync(req.Payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "QuerySites" => async (req, principal, ct) =>
        {
            BridgeResponse inner = await services.ListSitesAsync(includeInactive: principal?.Role != UserRole.Supervisor, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "GetActiveAttendanceStatuses" or "QueryAttendanceStatuses" => async (req, principal, ct) =>
        {
            BridgeResponse inner = await services.ListActiveStatusesAsync(ct).ConfigureAwait(false);
            return Done(req, inner.Data);
        },
        "SearchPersons" or "QueryPersons" or "SearchActivePersonsForAccount" => async (req, principal, ct) =>
        {
            string term = req.Payload?["term"]?.GetValue<string>() ?? "";
            int take = TryInt(req.Payload?["take"], 25);
            BridgeResponse inner = await services.SearchPersonsAsync(term, take, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "ListAttendanceDrafts" => async (req, principal, ct) =>
        {
            BridgeResponse inner = await services.ListDraftsAsync(principal!.UserId, ct).ConfigureAwait(false);
            return Done(req, inner.Data);
        },
        "SaveAttendanceDraft" => async (req, principal, ct) =>
        {
            if (req.Payload is not JsonObject payload)
                return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.ValidationError, "بيانات المسودة مطلوبة."));
            BridgeResponse inner = await services.SaveDraftAsync(principal!.UserId, payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "DeleteAttendanceDraft" => async (req, principal, ct) =>
        {
            if (req.Payload is not JsonObject payload)
                return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.ValidationError, "بيانات المسودة مطلوبة."));
            BridgeResponse inner = await services.DeleteDraftAsync(principal!.UserId, payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "GetApplicationVersion" => (req, principal, ct) =>
            Task.FromResult(Done(req, new JsonObject
            {
                ["productNameAr"] = "التمام اليومي",
                ["version"] = "0.4.0",
                ["offline"] = true,
                ["bridgeProtocol"] = BridgeProtocol.ProtocolVersion
            })),
        // ---------------- Phase 11: UserAuditService — audit portion (immutable evidence + overlays)
        "QueryAuditLog" => async (req, principal, ct) =>
        {
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.QueryAuditLogAsync(req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "GetAuditEntryDetails" => async (req, principal, ct) =>
        {
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.GetAuditEntryDetailsAsync(req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "UpdateAuditPresentation" => async (req, principal, ct) =>
        {
            if (req.Payload is not JsonObject payload)
                return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.MissingData, "حمولة التعديل مطلوبة."));
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.UpdateAuditPresentationAsync(payload, principal!, ct).ConfigureAwait(false);
            return inner.Success
                ? Done(req, inner.Data, Invalidation(UiInvalidationEvents.VisibleAuditOverlayChanged))
                : Fail(req, inner);
        },
        "DeleteAuditPresentation" => async (req, principal, ct) =>
        {
            if (req.Payload is not JsonObject payload)
                return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.MissingData, "حمولة الحذف الظاهري مطلوبة."));
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.DeleteAuditPresentationAsync(payload, principal!, ct).ConfigureAwait(false);
            return inner.Success
                ? Done(req, inner.Data, Invalidation(UiInvalidationEvents.VisibleAuditOverlayChanged))
                : Fail(req, inner);
        },
        "QueryAuditPresentationChanges" => async (req, principal, ct) =>
        {
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.QueryAuditPresentationChangesAsync(req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        _ => (req, principal, ct) => Task.FromResult(Fail(req, BridgeSharedServices.Error(
                BridgeErrorCodes.MissingData,
                $"الإجراء '{req.Action}' معتمد في العقد لكنه يُنفَّذ في مراحله المخصصة؛ لا تتوفر بياناته بعد.")))
    };

    private static int TryInt(JsonNode? node, int fallback)
    {
        try { return node?.GetValue<int>() ?? fallback; }
        catch { return fallback; }
    }

    private static object SessionData(BridgePrincipal principal) => new JsonObject
    {
        ["userId"] = principal.UserId,
        ["username"] = principal.Username,
        ["role"] = principal.Role.ToString(),
        ["roleLabelAr"] = principal.Role.ToArabicLabel(),
        ["sessionNonce"] = principal.SessionNonce
    };

    private static BridgeResponse Done(BridgeRequest req, object? data, IReadOnlyList<Contracts.Events.UiInvalidationEventPayload>? events = null)
        => new(req.RequestId, true, data, null, null, null, events);

    private static BridgeResponse Fail(BridgeRequest req, BridgeResponse inner)
        => new(req.RequestId, false, null, inner.ErrorCode ?? BridgeErrorCodes.ValidationError, inner.UserMessage ?? "فشلت العملية.");

    private static IReadOnlyList<Contracts.Events.UiInvalidationEventPayload> Invalidation(string eventName) =>
        new[] { new Contracts.Events.UiInvalidationEventPayload(eventName, Array.Empty<string>(), DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), 1) };
}
