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
    string SessionNonce);

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
    private readonly ISet<string> _seenMutatingRequestIds = new HashSet<string>(StringComparer.Ordinal);
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

        // 6) Anti-replay: a mutating requestId may be accepted exactly once per session lifetime.
        if (registration.IsMutating)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_seenMutatingRequestIds.Add(request.RequestId))
                    return Fail(request.RequestId, BridgeErrorCodes.ConflictStale,
                        "تم تنفيذ هذا الطلب مسبقاً؛ رفض الطلب المكرر.", BridgeOutcomeKind.Conflict);
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

        // 8) Execute the owning handler with cancellation support.
        try
        {
            BridgeResponse response = await registration.Handler(request, principal, cancellationToken).ConfigureAwait(false);
            var shaped = response with { RequestId = request.RequestId };
            return new BridgeDispatchResult(shaped, shaped.Success ? BridgeOutcomeKind.Success : BridgeOutcomeKind.HandlerError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A canceled mutating call must release its replay slot so the user can retry safely.
            if (registration.IsMutating)
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { _seenMutatingRequestIds.Remove(request.RequestId); }
                finally { _gate.Release(); }
            }
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
