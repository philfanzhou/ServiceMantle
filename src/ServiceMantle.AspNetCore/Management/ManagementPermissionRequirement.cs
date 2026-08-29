using Microsoft.AspNetCore.Authorization;

namespace ServiceMantle.Management;

/// <summary>
/// Requires a legitimate current management operator holding one specific permission.
/// </summary>
public sealed class ManagementPermissionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Initializes the requirement.
    /// </summary>
    /// <param name="permission">The required permission.</param>
    /// <exception cref="ArgumentOutOfRangeException">The permission is not a defined value.</exception>
    public ManagementPermissionRequirement(ManagementPermission permission)
    {
        if (!ManagementPermissions.IsDefined(permission))
        {
            throw new ArgumentOutOfRangeException(nameof(permission));
        }

        Permission = permission;
    }

    /// <summary>
    /// Gets the required permission.
    /// </summary>
    public ManagementPermission Permission { get; }

    /// <summary>
    /// Returns a safe projection.
    /// </summary>
    public override string ToString() =>
        $"ManagementPermissionRequirement(Permission={Permission})";
}
