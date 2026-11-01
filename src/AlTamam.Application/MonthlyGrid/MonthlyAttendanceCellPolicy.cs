namespace AlTamam.Application.MonthlyGrid;

/// <summary>
/// A saved attendance cell belongs to its effective historical site. Closing a
/// source assignment on a transfer/separation date does not erase a real status
/// committed earlier that same day; its source movement proves that exception.
/// Prevents exposing pre-admission and wrong-destination attendance as completed.
/// </summary>
internal static class MonthlyAttendanceCellPolicy
{
    internal const string TemporalMembershipSql = """
        AND ar.AttendanceLocalDate >= (
            SELECT p.FirstRecordedLocalDate FROM Persons p WHERE p.PersonId=ar.PersonId)
        AND (
            EXISTS (
                SELECT 1 FROM PersonAssignments a
                WHERE a.PersonId=ar.PersonId AND a.SiteId=ar.SiteIdAtEffectiveDate
                  AND a.ValidFromLocalDate <= ar.AttendanceLocalDate
                  AND (a.ValidToLocalDate IS NULL
                       OR a.ValidToLocalDate > ar.AttendanceLocalDate))
            OR EXISTS (
                SELECT 1 FROM PersonnelMovements m
                WHERE m.PersonId=ar.PersonId
                  AND m.EffectiveLocalDate=ar.AttendanceLocalDate
                  AND m.SourceSiteId=ar.SiteIdAtEffectiveDate
                  AND m.MovementType IN (1,2,3))
        )
        """;
}
