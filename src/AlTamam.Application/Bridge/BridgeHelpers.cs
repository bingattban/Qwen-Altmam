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

    internal static BridgeResponse Error(string code, string message)
        => new("", false, null, code, message);

    internal static IReadOnlyList<UiInvalidationEventPayload> InvalidationEvent(
        string eventName, string effectiveLocalDate, IEnumerable<string> affectedEntityIds)
        => new[]
        {
            new UiInvalidationEventPayload(eventName,
                affectedEntityIds is string[] arr ? arr : affectedEntityIds.ToArray(),
                effectiveLocalDate, 1)
        };
}
