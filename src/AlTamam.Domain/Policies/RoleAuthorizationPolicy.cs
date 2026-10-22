using AlTamam.Domain.Enums;

namespace AlTamam.Domain.Policies;

/// <summary>
/// Central operation-level RBAC matrix enforced in C# application services.
/// Never rely on hiding UI buttons alone.
/// </summary>
public static class RoleAuthorizationPolicy
{
    public static bool CanAccessUsersManagement(UserRole role) =>
        role is UserRole.GeneralAdministration or UserRole.OrdinaryAdministration;

    public static bool CanCreateAccountWithRole(UserRole actorRole, UserRole targetRole) =>
        actorRole switch
        {
            UserRole.GeneralAdministration => targetRole is UserRole.OrdinaryAdministration or UserRole.Supervisor,
            UserRole.OrdinaryAdministration => targetRole is UserRole.Supervisor,
            _ => false
        };

    public static bool CanModifyAccount(UserRole actorRole, UserRole targetAccountRole) =>
        actorRole switch
        {
            UserRole.GeneralAdministration => true,
            UserRole.OrdinaryAdministration => targetAccountRole == UserRole.Supervisor,
            _ => false
        };

    public static bool CanArchivePersonsFromActiveForce(UserRole role) =>
        role is UserRole.GeneralAdministration or UserRole.OrdinaryAdministration;

    /// <summary>
    /// Mandatory user override: BOTH GeneralAdministration and OrdinaryAdministration
    /// can edit and delete visible entries in "سجل العمليات". Supervisor is strictly denied.
    /// </summary>
    public static bool CanEditOrDeleteVisibleAuditLog(UserRole role) =>
        role is UserRole.GeneralAdministration or UserRole.OrdinaryAdministration;

    public static bool CanReviewAuditOverlayHistory(UserRole role) =>
        role is UserRole.GeneralAdministration or UserRole.OrdinaryAdministration;

    /// <summary>
    /// Exclusive safety restriction: only GeneralAdministration may execute "حذف جميع بيانات البرنامج".
    /// </summary>
    public static bool CanExecuteFullDatabaseReset(UserRole role) =>
        role == UserRole.GeneralAdministration;

    public static bool CanExecuteClosedDayEmergencyOverride(UserRole role) =>
        role == UserRole.GeneralAdministration;

    public static bool CanPerformStandardOperations(UserRole role) =>
        role is UserRole.GeneralAdministration or UserRole.OrdinaryAdministration or UserRole.Supervisor;
}
