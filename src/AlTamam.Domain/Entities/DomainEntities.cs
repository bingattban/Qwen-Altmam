using AlTamam.Domain.Enums;
using AlTamam.Domain.ValueObjects;

namespace AlTamam.Domain.Entities;

public sealed record Person(
    PersonId PersonId,
    string CurrentVisibleName,
    string NormalizedName,
    string CurrentServiceNumberA,
    string NormalizedServiceNumberA,
    DateOnly FirstRecordedLocalDate,
    DateTimeOffset FirstRecordedAtUtc,
    OrganizationalStatus OrganizationalStatus,
    bool ActiveFlag,
    long RowVersion);

public sealed record PersonIdentityHistory(
    long HistoryId,
    PersonId PersonId,
    string NameSnapshot,
    string ServiceNumberASnapshot,
    DateOnly ValidFromLocalDate,
    DateOnly? ValidToLocalDate,
    UserId ChangedBy,
    DateTimeOffset RecordedAtUtc,
    string Reason);

public sealed record Site(
    SiteId SiteId,
    string VisibleName,
    string NormalizedName,
    bool IsSystemSite,
    bool ActiveFlag,
    DateTimeOffset CreatedAtUtc,
    long RowVersion);

public sealed record SiteNameHistory(
    long HistoryId,
    SiteId SiteId,
    string VisibleNameSnapshot,
    DateOnly ValidFromLocalDate,
    DateOnly? ValidToLocalDate,
    UserId ChangedBy,
    DateTimeOffset RecordedAtUtc);

public sealed record PersonAssignment(
    AssignmentId AssignmentId,
    PersonId PersonId,
    SiteId SiteId,
    DateOnly ValidFromLocalDate,
    DateOnly? ValidToLocalDate,
    MovementId? SourceMovementId,
    DateTimeOffset CreatedAtUtc);

public sealed record AttendanceStatusDefinition(
    StatusId StatusId,
    string VisibleName,
    string ShortSymbol,
    string ChosenColorHex,
    bool IsSystemSeeded,
    bool ActiveFlag,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    long RowVersion);

public sealed record AttendanceStatusNameHistory(
    long HistoryId,
    StatusId StatusId,
    string VisibleNameSnapshot,
    string ShortSymbolSnapshot,
    string ColorSnapshot,
    DateOnly ValidFromLocalDate,
    DateOnly? ValidToLocalDate,
    DateTimeOffset RecordedAtUtc);

public sealed record AttendanceRecord(
    long RecordId,
    PersonId PersonId,
    DateOnly AttendanceLocalDate,
    StatusId StatusId,
    string StatusNameSnapshot,
    string StatusSymbolSnapshot,
    string StatusColorSnapshot,
    AssignmentId AssignmentIdAtEffectiveDate,
    SiteId SiteIdAtEffectiveDate,
    string SiteNameSnapshot,
    AttendanceSource Source,
    UserId CreatedBy,
    DateTimeOffset CommittedAtUtc,
    int RevisionNumber,
    string IdempotencyKey);

public sealed record AttendanceRevision(
    long RevisionId,
    PersonId PersonId,
    DateOnly TargetLocalDate,
    StatusId BeforeStatusId,
    string BeforeStatusNameSnapshot,
    StatusId AfterStatusId,
    string AfterStatusNameSnapshot,
    string Reason,
    UserId ActorUserId,
    DateTimeOffset ExecutedAtUtc,
    string AuthorizationMethod);

public sealed record PersonnelMovement(
    MovementId MovementId,
    PersonId PersonId,
    string PersonNameSnapshot,
    string ServiceNumberASnapshot,
    MovementType MovementType,
    SiteId SourceSiteId,
    string SourceSiteNameSnapshot,
    SiteId? DestinationSiteId,
    string? DestinationSiteNameSnapshot,
    string? DestinationExternalName,
    string? SeparationReason,
    DateOnly EffectiveLocalDate,
    DateTimeOffset CommittedAtUtc,
    UserId ActorUserId,
    string IdempotencyKey);

public sealed record UserAccount(
    UserId UserId,
    PersonId? LinkedPersonId,
    string Username,
    string NormalizedUsername,
    UserRole Role,
    string CredentialHash,
    string PasswordSalt,
    bool RequiresFirstRunSetup,
    bool ActiveFlag,
    int FailedLoginCount,
    DateTimeOffset? LockedUntilUtc,
    UserId CreatedBy,
    string CreatedByDisplaySnapshot,
    DateTimeOffset CreatedAtUtc,
    long RowVersion);

public sealed record AuditEvent(
    AuditEventId AuditEventId,
    string OperationCode,
    string OperationArabicName,
    UserId ActorUserId,
    PersonId? ActorPersonId,
    string ActorUsernameSnapshot,
    string? ActorRosterFullNameSnapshot,
    string TargetEntityType,
    string TargetEntityId,
    string TargetDisplaySnapshot,
    string? OldValueJson,
    string? NewValueJson,
    string DefaultArabicDescription,
    string? Reason,
    DateOnly EffectiveLocalDate,
    DateTimeOffset ExecutedAtUtc,
    string CorrelationId,
    string EventHash,
    string PriorEventHash)
{
    /// <summary>
    /// Projects the mandatory "من قام بالتنفيذ" display value:
    /// For roster-linked accounts: "[اسم المستخدم] - [الاسم الكامل بحسب الكشف]" from immutable execution-time snapshots.
    /// For unlinked bootstrap or SYSTEM events: explicit honest indicator without fabricated roster names.
    /// </summary>
    public string ProjectActorDisplay()
    {
        if (ActorUserId == UserId.SystemActor || string.Equals(ActorUsernameSnapshot, "SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            return "النظام الآلي (SYSTEM)";
        }

        if (!string.IsNullOrWhiteSpace(ActorRosterFullNameSnapshot))
        {
            return $"{ActorUsernameSnapshot} - {ActorRosterFullNameSnapshot}";
        }

        return $"{ActorUsernameSnapshot} - (حساب تأسيسي غير مرتبط بالكشف)";
    }
}

public sealed record AuditLogPresentationEdit(
    long OverlayId,
    AuditEventId AuditEventId,
    int OverlayVersion,
    bool IsTombstoned,
    string? CorrectedDescription,
    string Reason,
    UserId EditedByUserId,
    string EditedByDisplaySnapshot,
    DateTimeOffset EditedAtUtc);

public sealed record CustomFieldDefinition(
    FieldDefinitionId FieldDefinitionId,
    string TechnicalKey,
    string ArabicCaption,
    CustomFieldType FieldType,
    int DisplayOrder,
    bool IsSystemProtected,
    bool VisibleFlag,
    bool ActiveFlag,
    DateTimeOffset CreatedAtUtc);

public sealed record AutoAttendanceRun(
    string JobKey,
    DateOnly BusinessDate,
    DateTimeOffset DueTimeLocal,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool IsCatchUpRun,
    int CopiedCount,
    int SkippedNoYesterdayCount,
    int SkippedTransferExemptCount,
    string SummaryArabic);

public sealed record BackupRecord(
    BackupId BackupId,
    string BackupFilePath,
    string Sha256Checksum,
    int SchemaVersion,
    bool IsAutomatic,
    bool VerificationPassed,
    string KeyProtectionMode,
    UserId CreatedBy,
    DateTimeOffset CreatedAtUtc,
    string NotesArabic);
