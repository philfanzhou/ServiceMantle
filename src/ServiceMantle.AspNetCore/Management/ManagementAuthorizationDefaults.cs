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
}
