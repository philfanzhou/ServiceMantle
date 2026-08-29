namespace ServiceMantle.Management;

/// <summary>
/// The closed first-version set of management permissions.
/// </summary>
/// <remarks>
/// Unknown values are never usable as extension permissions. Adding a permission requires changing
/// this enum, its wire mapping in <see cref="ManagementPermissions"/>, and the contract tests.
/// The declaration order is the fixed emission order of permission claims.
/// </remarks>
public enum ManagementPermission
{
    /// <summary>
    /// Read management state.
    /// </summary>
    Read = 0,

    /// <summary>
    /// Change management state.
    /// </summary>
    Write = 1,

    /// <summary>
    /// Administer the management surface.
    /// </summary>
    Admin = 2,
}
