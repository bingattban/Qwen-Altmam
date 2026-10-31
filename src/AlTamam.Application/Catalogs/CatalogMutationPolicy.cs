using AlTamam.Domain.Enums;
using AlTamam.Domain.Policies;

namespace AlTamam.Application.Catalogs;

/// <summary>
/// C# service-boundary catalog authorization. WebView2 input cannot supply
/// trusted role claims; catalog mutators receive authenticated BridgePrincipal
/// and reject undefined/unauthenticated privilege levels independently.
/// </summary>
public static class CatalogMutationPolicy
{
    public static bool Allows(UserRole role) =>
        RoleAuthorizationPolicy.CanPerformStandardOperations(role);
}
