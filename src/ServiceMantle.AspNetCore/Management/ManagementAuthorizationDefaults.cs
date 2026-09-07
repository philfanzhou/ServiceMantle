namespace ServiceMantle.Management;

/// <summary>
/// Fixed defaults of the ServiceMantle management authorization surface.
/// </summary>
public static class ManagementAuthorizationDefaults
{
    /// <summary>
    /// The name of the policy that requires a legitimate current operator holding
    /// <see cref="ManagementPermission.Admin"/>.
    /// </summary>
    public const string AdminPolicyName = "ServiceMantle.ManagementAdmin";

    /// <summary>
    /// The name of the policy that requires a legitimate current operator authenticated through the
    /// fixed management cookie scheme, without requiring any specific permission.
    /// </summary>
    /// <remarks>
    /// The policy exists for the shared management entries that any signed-in operator may reach.
    /// It is stricter than <c>RequireAuthenticatedUser</c> because an authenticated principal whose
    /// ServiceMantle claims do not resolve to exactly one legitimate operator is rejected, and it is
    /// weaker than <see cref="AdminPolicyName"/> because it requires no permission.
    /// </remarks>
    public const string SessionPolicyName = "ServiceMantle.ManagementSession";
}
