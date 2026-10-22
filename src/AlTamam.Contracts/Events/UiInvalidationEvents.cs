namespace AlTamam.Contracts.Events;

/// <summary>
/// All 13 canonical UI invalidation events emitted after committed domain mutations.
/// Each event contains stable surrogate IDs and effective local date to refresh only affected views.
/// </summary>
public static class UiInvalidationEvents
{
    public const string AttendanceCommitted = "AttendanceCommitted";
    public const string AttendanceRevised = "AttendanceRevised";
    public const string PersonnelMoved = "PersonnelMoved";
    public const string PersonnelArchived = "PersonnelArchived";
    public const string SiteCreated = "SiteCreated";
    public const string SiteRenamed = "SiteRenamed";
    public const string StatusDefinitionCreated = "StatusDefinitionCreated";
    public const string StatusDefinitionChanged = "StatusDefinitionChanged";
    public const string PersonIdentityUpdated = "PersonIdentityUpdated";
    public const string VisibleAuditOverlayChanged = "VisibleAuditOverlayChanged";
    public const string UserAccountChanged = "UserAccountChanged";
    public const string BackupRestored = "BackupRestored";
    public const string SessionEnded = "SessionEnded";

    public static readonly string[] AllCanonicalEvents = new[]
    {
        AttendanceCommitted, AttendanceRevised, PersonnelMoved, PersonnelArchived,
        SiteCreated, SiteRenamed, StatusDefinitionCreated, StatusDefinitionChanged,
        PersonIdentityUpdated, VisibleAuditOverlayChanged, UserAccountChanged,
        BackupRestored, SessionEnded
    };
}

public sealed record UiInvalidationEventPayload(
    string EventName,
    string[] AffectedEntityIds,
    string EffectiveLocalDate,
    long Revision
);
