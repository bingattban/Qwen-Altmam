using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Shared envelope factories for authentic bridge services (Phase 05+). Mirrors the internal
/// helpers of <see cref="BridgeSharedServices"/> so every service emits identical canonical shapes.
/// </summary>
internal static class BridgeHelpers
{
    internal static BridgeResponse Ok(object? data, long? revision = null,
        IReadOnlyList<UiInvalidationEventPayload>? events = null)
        => new("", true, data, null, null, revision, events);

    internal static BridgeResponse Error(string code, string message, string? field = null)
        => new BridgeResponse("", false, null, code, message)
        {
            ValidationErrors = field is null ? null : new JsonObject { [field] = message }
        };

    internal static IReadOnlyList<UiInvalidationEventPayload> InvalidationEvent(
        string eventName, string effectiveLocalDate, IEnumerable<string> affectedEntityIds)
        => new[]
        {
            new UiInvalidationEventPayload(eventName,
                affectedEntityIds is string[] arr ? arr : affectedEntityIds.ToArray(),
                effectiveLocalDate, 1)
        };
}
