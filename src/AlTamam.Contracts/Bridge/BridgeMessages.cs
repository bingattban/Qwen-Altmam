using System.Collections.Generic;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Events;

namespace AlTamam.Contracts.Bridge;

/// <summary>
/// Canonical C# <-> WebView2 UI Bridge Request and Response Envelopes:
/// request: {requestId:string, protocolVersion:1, action:string, sessionNonce:string, payload:object}
/// response: {requestId:string, success:boolean, data?:object, errorCode?:string, userMessage?:string, revision?:number}
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
    object? Data = null,
    string? ErrorCode = null,
    string? UserMessage = null,
    long? Revision = null,
    IReadOnlyList<UiInvalidationEventPayload>? InvalidationEvents = null
);
