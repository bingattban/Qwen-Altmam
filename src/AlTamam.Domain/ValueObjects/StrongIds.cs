namespace AlTamam.Domain.ValueObjects;

/// <summary>
/// Central Surrogate ID Types mandated by canonical contract:
/// PersonId, SiteId, StatusId, AssignmentId, MovementId, UserId, AuditEventId,
/// ImportBatchId, ReportJobId, BackupId, and FieldDefinitionId.
/// Never use name, الرقم (ع), site display name, or status caption as primary DB relation key.
/// </summary>
public readonly record struct PersonId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct SiteId(string Value)
{
    public static readonly SiteId Unspecified = new("SITE-UNSPECIFIED");
    public override string ToString() => Value;
}

public readonly record struct StatusId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct AssignmentId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct MovementId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct UserId(string Value)
{
    public static readonly UserId SystemActor = new("USR-SYSTEM");
    public override string ToString() => Value;
}

public readonly record struct AuditEventId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ImportBatchId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ReportJobId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct BackupId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct FieldDefinitionId(string Value)
{
    public override string ToString() => Value;
}
