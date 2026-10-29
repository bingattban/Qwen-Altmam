using System.Text.Json;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;

namespace AlTamam.Application.Bridge;

/// <summary>Authenticated principal attached to the current WebView2 session nonce.</summary>
public sealed record BridgePrincipal(
    string UserId,
    string Username,
    UserRole Role,
    string SessionNonce)
{
    // Set by the server's live-session resolver; not supplied by WebView2.
    // Logout cancels long-running handlers without trusting JS claims.
    public CancellationToken SessionRevocation { get; init; }
}

/// <summary>Result of a validated bridge dispatch: canonical response plus the outcome category.</summary>
public enum BridgeOutcomeKind { Success, ProtocolError, Unauthorized, Forbidden, NotFound, Conflict, Canceled, HandlerError }

public sealed record BridgeDispatchResult(BridgeResponse Response, BridgeOutcomeKind Kind);

/// <summary>Handler contract: one typed async handler per canonical action (per-action ownership enforced by the dispatcher).</summary>
public delegate Task<BridgeResponse> BridgeActionHandler(BridgeRequest request, BridgePrincipal? principal, CancellationToken cancellationToken);

/// <summary>
/// Typed WebView2 bridge dispatcher. Validates every envelope against the canonical contract BEFORE
/// any handler runs: schema/size (via <see cref="BridgeRequestParser"/>), protocol version, known action,
/// origin/frame (host-provided), session authentication (nonce → principal), role authorization from the
/// canonical action manifest, anti-replay of mutating request IDs, and optimistic-concurrency revision checks.
/// </summary>
public sealed class WebView2BridgeDispatcher
{
    private readonly IReadOnlyDictionary<string, BridgeActionRegistration> _actions;
    // Never evict live replay IDs: eviction would allow executing an old mutation twice.
    // Keep separately bounded nonce-scoped sets and fail closed at their limits.
    private const int MaxRequestsPerSession = 32768;
    private const int MaxTrackedSessions = 32;
    private sealed class ReplaySession(CancellationToken revocation)
    {
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        public readonly CancellationToken Revocation = revocation;
    }
    private readonly Dictionary<string, ReplaySession> _replaySessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WebView2BridgeDispatcher(IEnumerable<BridgeActionRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var map = new Dictionary<string, BridgeActionRegistration>(StringComparer.Ordinal);
        foreach (var reg in registrations)
        {
            if (!map.TryAdd(reg.Action, reg))
                throw new InvalidOperationException($"Duplicate handler registration for action '{reg.Action}'.");
        }
        _actions = map;
    }

    /// <summary>Registered action names (must mirror the canonical action manifest).</summary>
    public IReadOnlyCollection<string> RegisteredActions => (IReadOnlyCollection<string>)_actions.Keys;

    /// <summary>
    /// Dispatches one raw JSON message. Never throws on hostile input — every failure becomes a canonical
    /// <see cref="BridgeResponse"/> with a stable errorCode.
    /// </summary>
    /// <param name="rawJson">Raw postMessage payload from the WebView.</param>
    /// <param name="originUri">Frame URI reported by WebView2 (must be the bundled virtual host).</param>
    /// <param name="isMainFrame">True only when the sender is the top-level document of the virtual host.</param>
    /// <param name="authenticate">Resolves a session nonce to the server-side principal (null when unknown/expired).</param>
    /// <param name="cancellationToken">Cancels both queueing and in-flight handler work.</param>
    public async Task<BridgeDispatchResult> DispatchAsync(
        string rawJson,
        string? originUri,
        bool isMainFrame,
        Func<string, Task<BridgePrincipal?>> authenticate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticate);

        // 1) Origin / frame guard (defence-in-depth on top of the Deny host-resource mapping).
        if (!IsAllowedOrigin(originUri) || !isMainFrame)
            return Fail("", BridgeErrorCodes.Unauthorized, "مصدر الطلب غير معتمد.", BridgeOutcomeKind.Unauthorized);

        // 2) Canonical envelope parsing + schema/size validation.
        BridgeRequest request;
        try
        {
            request = BridgeRequestParser.Parse(rawJson);
        }
        catch (BridgeProtocolException ex)
        {
            return Fail(TryExtractRequestId(rawJson), ex.ErrorCode, ex.UserMessage, BridgeOutcomeKind.ProtocolError);
        }

        // 3) Known action + per-action ownership.
        if (!_actions.TryGetValue(request.Action, out var registration))
            return Fail(request.RequestId, BridgeErrorCodes.ValidationError,
                $"إجراء غير معروف: '{request.Action}'.", BridgeOutcomeKind.NotFound);

        // 4) Authentication: the session nonce must resolve to a live server-side session.
        BridgePrincipal? principal = await authenticate(request.SessionNonce).ConfigureAwait(false);
        bool requiresAuth = !registration.AllowUnauthenticated;
        if (requiresAuth && principal is null)
            return Fail(request.RequestId, BridgeErrorCodes.SessionExpired,
                "انتهت صلاحية الجلسة، يجب تسجيل الدخول من جديد.", BridgeOutcomeKind.Unauthorized);
        if (principal is not null && !string.Equals(principal.SessionNonce, request.SessionNonce, StringComparison.Ordinal))
            return Fail(request.RequestId, BridgeErrorCodes.Unauthorized,
                "رمز الجلسة غير مطابق للجلسة النشطة.", BridgeOutcomeKind.Unauthorized);

        // 5) Authorization: role checked ONLY from the server-side principal — never from the payload.
        if (registration.AllowedRoles.Count > 0)
        {
            string roleLabel = principal?.Role.ToArabicLabel() ?? "Unauthenticated";
            bool allowed = principal is not null
                ? registration.AllowedRoles.Contains(principal.Role.ToString(), StringComparer.Ordinal)
                : registration.AllowedRoles.Contains("Unauthenticated", StringComparer.Ordinal);
            if (!allowed)
                return Fail(request.RequestId, BridgeErrorCodes.Unauthorized,
                    $"صلاحية '{roleLabel}' لا تسمح بتنفيذ '{request.Action}'.", BridgeOutcomeKind.Forbidden);
        }

        // Owner-approved Section 02.14 rule: reject old WebView2 clients that
        // omit an operationId for ANY business/state mutation. This is checked
        // AFTER server authentication/RBAC and BEFORE request replay tracking or
        // handler execution, so neither JS manipulation nor a stale client can
        // bypass the rule or consume a replay slot with an invalid request.
        if (registration.IsMutating
            && ActionManifest.Find(request.Action) is { IsMutating: true }
            && ActionManifest.RequiresOperationId(request.Action, true)
            && !MutationIdempotency.HasValidOperationId(request.Payload))
            return Fail(request.RequestId, BridgeErrorCodes.ValidationError,
                "معرّف العملية operationId إلزامي وصيغته 16–128 حرفاً لاتينياً أو رقماً أو - أو _.",
                BridgeOutcomeKind.ProtocolError);

        // 6) Anti-replay: a requestId is accepted only once per authenticated
        // session, or once per unauthenticated login flow. Prune ONLY revoked
        // sessions; NEVER evict a still-valid request ID or use a short TTL.
        if (registration.IsMutating)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (string dead in _replaySessions
                    .Where(pair => pair.Value.Revocation.IsCancellationRequested)
                    .Select(pair => pair.Key).ToArray())
                    _replaySessions.Remove(dead);

                string replaySessionKey = principal is null ? "__unauthenticated__" : request.SessionNonce;
                if (!_replaySessions.TryGetValue(replaySessionKey, out var tracked))
                {
                    // Some sessions expire through the five-minute idle deadline
                    // without a Logout cancellation signal. Recheck database
                    // authority only when at capacity; do not discard any nonce
                    // that the host still considers authenticated.
                    if (_replaySessions.Count >= MaxTrackedSessions)
                    {
                        foreach (string oldNonce in _replaySessions.Keys.ToArray())
                        {
                            if (oldNonce == "__unauthenticated__" || oldNonce == replaySessionKey) continue;
                            if (await authenticate(oldNonce).ConfigureAwait(false) is null)
                                _replaySessions.Remove(oldNonce);
                        }
                    }
                    if (_replaySessions.Count >= MaxTrackedSessions)
                        return Fail(request.RequestId, BridgeErrorCodes.ConflictStale,
                            "بلغت حماية الجلسات حدها الآمن. سجّل الخروج وأعد الدخول.", BridgeOutcomeKind.Conflict);
                    tracked = new ReplaySession(principal?.SessionRevocation ?? CancellationToken.None);
                    _replaySessions.Add(replaySessionKey, tracked);
                }
                if (tracked.Seen.Contains(request.RequestId))
                    return Fail(request.RequestId, BridgeErrorCodes.ConflictStale,
                        "تم تنفيذ هذا الطلب مسبقاً؛ رفض الطلب المكرر.", BridgeOutcomeKind.Conflict);
                if (tracked.Seen.Count >= MaxRequestsPerSession)
                    return Fail(request.RequestId, BridgeErrorCodes.ConflictStale,
                        "بلغت الجلسة الحد الآمن للعمليات؛ سجّل الخروج ثم ادخل بجلسة جديدة.",
                        BridgeOutcomeKind.Conflict);
                tracked.Seen.Add(request.RequestId);
            }
            finally { _gate.Release(); }
        }

        // 7) Stale-revision guard: if the payload carries expectedRevision it must match baseRevision.
        if (registration.IsMutating
            && request.Payload is not null
            && TryGetLong(request.Payload, "expectedRevision", out long expected)
            && TryGetLong(request.Payload, "baseRevision", out long actual)
            && expected != actual)
        {
            return Fail(request.RequestId, BridgeErrorCodes.ConflictStale,
                "نسخة البيانات قديمة، أعد التحميل قبل الحفظ.", BridgeOutcomeKind.Conflict);
        }

        // 8) Tie the request's lifetime to the SERVER's nonce revocation.
        // A Logout action is itself exempt so it can persist the revocation.
        using var linked = principal is not null && request.Action != "Logout" &&
                           principal.SessionRevocation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, principal.SessionRevocation)
            : null;
        CancellationToken executionToken = linked?.Token ?? cancellationToken;
        try
        {
            executionToken.ThrowIfCancellationRequested();
            BridgeResponse response = await registration.Handler(
                request, principal, executionToken).ConfigureAwait(false);
            // A deferred read or handler that ignored cancellation must NOT
            // return the old user's sensitive response after logout/expiry.
            if (requiresAuth && request.Action != "Logout" &&
                (principal!.SessionRevocation.IsCancellationRequested ||
                 await authenticate(request.SessionNonce).ConfigureAwait(false) is null))
                return Fail(request.RequestId, BridgeErrorCodes.SessionExpired,
                    "انتهت صلاحية الجلسة، يجب تسجيل الدخول من جديد.", BridgeOutcomeKind.Unauthorized);
            // Explicit logout revokes the old nonce, so its replay history may be
            // discarded. A successful login does not clear anonymous history.
            if (response.Success && request.Action == "Logout")
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    _replaySessions.Remove(request.SessionNonce);
                    // A new unauthenticated login after explicit logout is a
                    // new flow. Never clear its replay cache on mere login success.
                    _replaySessions.Remove("__unauthenticated__");
                }
                finally { _gate.Release(); }
            }
            var shaped = response with { RequestId = request.RequestId };
            return new BridgeDispatchResult(shaped, shaped.Success ? BridgeOutcomeKind.Success : BridgeOutcomeKind.HandlerError);
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            // A canceled mutating call must release its replay slot so the user can retry safely.
            if (registration.IsMutating)
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    string key = principal is null ? "__unauthenticated__" : request.SessionNonce;
                    if (_replaySessions.TryGetValue(key, out var tracked))
                        tracked.Seen.Remove(request.RequestId);
                }
                finally { _gate.Release(); }
            }
            if (principal?.SessionRevocation.IsCancellationRequested == true)
                return Fail(request.RequestId, BridgeErrorCodes.SessionExpired,
                    "أُلغيت العملية لانتهاء صلاحية الجلسة.", BridgeOutcomeKind.Unauthorized);
            return Fail(request.RequestId, BridgeErrorCodes.StorageBusy,
                "أُلغي الطلب قبل اكتماله.", BridgeOutcomeKind.Canceled);
        }
        catch (BridgeProtocolException ex)
        {
            return Fail(request.RequestId, ex.ErrorCode, ex.UserMessage, BridgeOutcomeKind.ProtocolError);
        }
        catch (Exception)
        {
            return Fail(request.RequestId, BridgeErrorCodes.ValidationError,
                "تعذر إكمال العملية بسبب خطأ غير متوقع.", BridgeOutcomeKind.HandlerError);
        }
    }

    /// <summary>Only the exact bundled virtual host (and its pages) may drive the bridge.</summary>
    public static bool IsAllowedOrigin(string? uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString)) return false;
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out Uri? uri)) return false;
        // A virtual host is an origin (scheme + host + port), not merely a
        // hostname. Reject nonstandard HTTPS ports and credentialed URLs before
        // authentication, even if an injected frame forges a matching host.
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.Host, LocalWebUiOrigin.Host, StringComparison.OrdinalIgnoreCase)
               && uri.IsDefaultPort
               && uri.UserInfo.Length == 0;
    }

    private static bool TryGetLong(JsonObject payload, string key, out long value)
    {
        value = 0;
        if (!payload.TryGetPropertyValue(key, out JsonNode? node) || node is null) return false;
        try { value = node.GetValue<long>(); return true; }
        catch { return false; }
    }

    private static string TryExtractRequestId(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(BridgeProtocol.RequestIdField, out var id)
                && id.ValueKind == JsonValueKind.String)
                return id.GetString() ?? "";
        }
        catch (JsonException) { /* malformed frames echo an empty correlation id */ }
        return "";
    }

    private static BridgeDispatchResult Fail(string requestId, string code, string message, BridgeOutcomeKind kind)
        => new(new BridgeResponse(requestId, false, null, code, message), kind);
}

/// <summary>Server-side registration of one canonical action (ownership metadata comes from action-manifest.json).</summary>
public sealed record BridgeActionRegistration(
    string Action,
    string OwningService,
    IReadOnlyList<string> AllowedRoles,
    bool IsMutating,
    bool AllowUnauthenticated,
    BridgeActionHandler Handler);
