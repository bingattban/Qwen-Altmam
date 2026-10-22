using System;
using System.Collections.Generic;

namespace AlTamam.Contracts.Dtos;

/// <summary>
/// Stable data transfer contracts for business operations and the future desktop interface.
/// </summary>
public sealed record PersonCurrentDto(
    string PersonId,
    string DisplayName,
    string ServiceNumberA,
    string SiteId,
    string SiteName,
    string OrganizationalState,
    long EntityVersion
);

public sealed record PersonHistoricIdentityDto(
    string PersonId,
    string ValidFrom,
    string? ValidTo,
    string NameSnapshot,
    string ServiceNumberASnapshot
);

public sealed record StatusDto(
    string StatusId,
    string DisplayName,
    string ShortSymbol,
    string Color,
    bool Active,
    long Version
);

public sealed record DailyAttendanceDto(
    string PersonId,
    string BusinessDate,
    string? StatusId,
    string? TodayStatusCaption,
    string? YesterdayStatusId,
    bool IsUnprepared,
    bool IsMovementExempt,
    long Revision,
    string AssignmentId
);

public sealed record MovementDto(
    string MovementId,
    string PersonId,
    string Type,
    string SourceSiteId,
    string? DestinationSiteId,
    string? DestinationExternalName,
    string SourceLabelSnapshot,
    string DestLabelSnapshot,
    string EffectiveLocalDate,
    string CommittedAtUtc,
    string ActorId
);

public sealed record MonthlyCellDto(
    string PersonId,
    string SiteId,
    string Date,
    string Kind, // attendance | unprepared | movementOut | movementIn | separation | empty
    string? StatusId,
    string? Symbol,
    string? Color,
    string? DisplayLabelSnapshot,
    string? DetailsRef
);

public sealed record UsedStatusCountDto(
    string StatusId,
    string StatusCaption,
    int ApprovedDayCount
);

public sealed record LifetimeSummaryDto(
    string PersonId,
    string CurrentName,
    string CurrentNumber,
    string EnrollmentDate,
    string AsOfUtc,
    IReadOnlyList<UsedStatusCountDto> UsedStatusCounts,
    int InternalTransferCount,
    string HistoryQueryToken
);

public sealed record AuditPresentationDto(
    string AuditEventId,
    string ImmutableEventCode,
    string DisplayedDescription,
    int OverlayVersion,
    bool Tombstoned,
    string ActorDisplay,
    string? ActorUserId,
    string? ActorUsernameSnapshot,
    string? ActorRosterFullNameSnapshot,
    string EffectiveDate,
    string ExecutionTime
);
