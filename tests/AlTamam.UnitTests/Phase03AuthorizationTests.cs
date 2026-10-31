using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Domain.Policies;
using Xunit;

namespace AlTamam.UnitTests;

public sealed class Phase03AuthorizationTests
{
    private static readonly string[] Users =
    [
        "GetAllowedRoles", "ListUserAccounts", "SearchActivePersonsForAccount",
        "CreateUserAccount", "ChangeUserPassword", "DeactivateUserAccount", "ReactivateUserAccount"
    ];
    private static readonly string[] Privileged =
    [
        "UpdateAuditPresentation", "DeleteAuditPresentation",
        "QueryAuditPresentationChanges", "SetClosedDayCorrectionPolicy",
        "PreviewClosedDayCorrection", "CommitClosedDayCorrection",
        "PreviewAdministrativeAttendanceCatchUp", "CommitAdministrativeAttendanceCatchUp"
    ];

    [Fact]
    public void SupervisorAndGuest_CannotInvokeAnyUsersManagementCommand()
    {
        foreach (var action in Users)
        {
            var entry = Assert.Single(ActionManifest.All, x => x.Action == action);
            Assert.DoesNotContain("Supervisor", entry.AllowedRoles);
            Assert.DoesNotContain("Unauthenticated", entry.AllowedRoles);
            Assert.Contains("GeneralAdministration", entry.AllowedRoles);
            Assert.Contains("OrdinaryAdministration", entry.AllowedRoles);
        }
        Assert.False(RoleAuthorizationPolicy.CanAccessUsersManagement(UserRole.Supervisor));
    }

    [Fact]
    public void BothAdminRoles_NotSupervisor_CanEditAuditAndCorrectClosedDays()
    {
        foreach (var action in Privileged)
        {
            var entry = Assert.Single(ActionManifest.All, x => x.Action == action);
            Assert.DoesNotContain("Supervisor", entry.AllowedRoles);
            Assert.DoesNotContain("Unauthenticated", entry.AllowedRoles);
            Assert.Contains("GeneralAdministration", entry.AllowedRoles);
            Assert.Contains("OrdinaryAdministration", entry.AllowedRoles);
        }
        Assert.True(RoleAuthorizationPolicy.CanEditOrDeleteVisibleAuditLog(UserRole.GeneralAdministration));
        Assert.True(RoleAuthorizationPolicy.CanEditOrDeleteVisibleAuditLog(UserRole.OrdinaryAdministration));
        Assert.False(RoleAuthorizationPolicy.CanEditOrDeleteVisibleAuditLog(UserRole.Supervisor));
        Assert.True(RoleAuthorizationPolicy.CanExecuteClosedDayEmergencyOverride(UserRole.GeneralAdministration));
        Assert.True(RoleAuthorizationPolicy.CanExecuteClosedDayEmergencyOverride(UserRole.OrdinaryAdministration));
        Assert.False(RoleAuthorizationPolicy.CanExecuteClosedDayEmergencyOverride(UserRole.Supervisor));
    }

    [Fact]
    public void AnotherFounderCannotBeCreatedByAnyRole()
    {
        foreach (var role in Enum.GetValues<UserRole>())
            Assert.False(RoleAuthorizationPolicy.CanCreateAccountWithRole(role, UserRole.GeneralAdministration));
        Assert.True(RoleAuthorizationPolicy.CanCreateAccountWithRole(UserRole.GeneralAdministration, UserRole.OrdinaryAdministration));
        Assert.True(RoleAuthorizationPolicy.CanCreateAccountWithRole(UserRole.GeneralAdministration, UserRole.Supervisor));
        Assert.True(RoleAuthorizationPolicy.CanCreateAccountWithRole(UserRole.OrdinaryAdministration, UserRole.Supervisor));
        Assert.False(RoleAuthorizationPolicy.CanCreateAccountWithRole(UserRole.OrdinaryAdministration, UserRole.OrdinaryAdministration));
        Assert.False(RoleAuthorizationPolicy.CanCreateAccountWithRole(UserRole.Supervisor, UserRole.Supervisor));
    }

    [Theory]
    [InlineData("Abc123!secure", true)]
    [InlineData("Abc123", false)]
    [InlineData("Abc123456", false)]
    [InlineData("A b c 123!", false)]
    [InlineData("Abc١٢٣!secure", false)]
    public void PasswordStrength_IsEnforcedByDomainPolicy(string password, bool expected)
        => Assert.Equal(expected, PasswordPolicyValidator.Validate(password).IsValid);
}
