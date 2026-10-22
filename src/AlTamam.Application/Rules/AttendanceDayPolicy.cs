namespace AlTamam.Application.Rules;

/// <summary>Core business-date gate; authorization to override closed periods is a separate permission.</summary>
public static class AttendanceDayPolicy
{
    public static void EnsureDayCanBePrepared(DateOnly businessDate, DateOnly targetDate,
        bool movementOccursOnTargetDate, bool isAuthorizedClosedDayCorrection)
    {
        if (movementOccursOnTargetDate)
            throw new InvalidOperationException("لا يُعتمد التحضير في يوم النقل الداخلي.");
        if (businessDate != targetDate && !isAuthorizedClosedDayCorrection)
            throw new InvalidOperationException("التحضير العادي مسموح لليوم الحالي فقط.");
    }
}
