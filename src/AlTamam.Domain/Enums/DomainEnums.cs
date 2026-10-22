namespace AlTamam.Domain.Enums;

/// <summary>
/// Canonical three role tiers in "التمام اليومي".
/// </summary>
public enum UserRole
{
    GeneralAdministration = 1,   // الإدارة العامة
    OrdinaryAdministration = 2,  // الإدارة العادية
    Supervisor = 3               // مشرف
}

public static class UserRoleExtensions
{
    public static string ToArabicLabel(this UserRole role) => role switch
    {
        UserRole.GeneralAdministration => "الإدارة العامة",
        UserRole.OrdinaryAdministration => "الإدارة العادية",
        UserRole.Supervisor => "مشرف",
        _ => "غير معروف"
    };
}

public enum OrganizationalStatus
{
    Active = 1,                  // نشط ضمن القوة
    ExternallyTransferred = 2,   // منقول خارجياً
    Separated = 3,               // مفصول
    ArchivedFromForce = 4        // محذوف/مؤرشف من القوة الفعالة
}

public enum AttendanceSource
{
    ManualSingle = 1,
    ManualBulk = 2,
    Auto20hCarryForward = 3,
    SystemCatchUp20h = 4,
    EmergencyClosedDayOverride = 5
}

public static class AttendanceSourceExtensions
{
    public static string ToCode(this AttendanceSource source) => source switch
    {
        AttendanceSource.ManualSingle => "manual",
        AttendanceSource.ManualBulk => "bulk",
        AttendanceSource.Auto20hCarryForward => "20h-auto",
        AttendanceSource.SystemCatchUp20h => "20h-catchup",
        AttendanceSource.EmergencyClosedDayOverride => "emergency-override",
        _ => "manual"
    };
}

public enum MovementType
{
    InternalTransfer = 1,        // نقل داخلي
    ExternalTransfer = 2,        // نقل خارجي
    Separation = 3               // فصل
}

public enum MonthlyCellKind
{
    Attendance = 1,              // حالة تحضير معتمدة
    Unprepared = 2,              // غير محضر
    MovementOut = 3,             // تم نقله إلى ...
    MovementIn = 4,              // تم نقله من ...
    Separation = 5,              // مفصول
    Empty = 6                    // خارج فترة التبعية للموقع
}

public enum CustomFieldType
{
    Text = 1,
    Number = 2,
    Date = 3,
    Selection = 4
}
