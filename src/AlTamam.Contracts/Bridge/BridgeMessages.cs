using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlTamam.Contracts.Events;

namespace AlTamam.Contracts.Bridge;

/// <summary>
/// Canonical C# <-> WebView2 UI Bridge Request and Response Envelopes:
/// request: {requestId:string, protocolVersion:1, action:string, sessionNonce:string, payload:object}
/// response: {requestId, success, code, messageAr, data?, errorCode?, userMessage?,
///            validationErrors?, conflictInfo?, auditEventId?, expectedVersion?, actualVersion?}.
/// Legacy errorCode/userMessage are retained as exact compatibility aliases.
/// </summary>
public sealed record BridgeRequest(
    string RequestId,
    int ProtocolVersion,
    string Action,
    string SessionNonce,
    JsonObject? Payload
);

public sealed record BridgeResponse(
    string RequestId,
    bool Success,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Data = null,
    string? ErrorCode = null,
    string? UserMessage = null,
    long? Revision = null,
    IReadOnlyList<UiInvalidationEventPayload>? InvalidationEvents = null
)
{
    // Additive compatibility properties; keep all existing constructors and
    // legacy JavaScript fields unchanged for already-shipped local pages.
    public string Code => Success ? "OK" : ErrorCode ?? BridgeErrorCodes.ValidationError;
    public string MessageAr => UserMessage ??
        (Success ? "تم تنفيذ الطلب بنجاح." : "تعذر إكمال العملية.");

    // Only real validation branches populate field errors. Never invent an
    // audit identifier, optimistic revision or conflict from a UI assertion.
    public JsonObject? ValidationErrors { get; init; }
    public JsonObject? ConflictInfo { get; init; }
    public string? AuditEventId { get; init; }
    public long? ExpectedVersion { get; init; }
    public long? ActualVersion { get; init; }
}
