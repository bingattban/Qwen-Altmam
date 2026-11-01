using System.Text.Json.Nodes;
using AlTamam.Infrastructure.Audit;

namespace AlTamam.Application.Audit;

/// <summary>Immutable before/after visible audit state for a chained meta-event.</summary>
internal readonly record struct AuditPresentationState(
    long OverlayVersion,bool IsTombstoned,string DisplayDescription)
{
    internal static AuditPresentationState From(AuditPresentationEvidence row) =>
        new(row.OverlayVersion,row.IsTombstoned,
            string.IsNullOrWhiteSpace(row.CorrectedDescription)
                ? row.RawDescription : row.CorrectedDescription);
    internal static AuditPresentationState Next(
        AuditPresentationState previous,bool hide,string newDescription) =>
        new(previous.OverlayVersion+1,hide,
            hide?previous.DisplayDescription:newDescription);
    internal JsonObject ToJson() => new()
    {
        ["overlayVersion"]=OverlayVersion,
        ["isTombstoned"]=IsTombstoned,
        ["displayDescription"]=DisplayDescription
    };
}
