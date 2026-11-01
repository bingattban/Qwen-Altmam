using System.Globalization;
using AlTamam.Domain.Enums;
using AlTamam.Domain.Policies;

namespace AlTamam.Application.Attendance.ClosedDayCorrections;

/// <summary>
/// One policy boundary for the administrators permitted to correct a closed
/// attendance day and for local-date historical eligibility. The encrypted
/// attendance service additionally verifies persisted assignment windows,
/// transfer/dismissal events, and saved period closure in its transaction.
/// </summary>
public static class ClosedDayCorrectionEligibilityPolicy
{
    public static bool AuthorizedRole(UserRole role) =>
        RoleAuthorizationPolicy.CanExecuteClosedDayEmergencyOverride(role);

    public static bool IsHistorical(string localDate,DateTimeOffset localNow) =>
        DateOnly.TryParseExact(localDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,
            DateTimeStyles.None,out var day) &&
        day<DateOnly.FromDateTime(localNow.DateTime);
}
