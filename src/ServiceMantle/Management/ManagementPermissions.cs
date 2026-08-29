namespace ServiceMantle.Management;

/// <summary>
/// Maps <see cref="ManagementPermission"/> values to their stable claim wire values.
/// </summary>
public static class ManagementPermissions
{
    /// <summary>
    /// The wire value of <see cref="ManagementPermission.Read"/>.
    /// </summary>
    public const string ReadValue = "management.read";

    /// <summary>
    /// The wire value of <see cref="ManagementPermission.Write"/>.
    /// </summary>
    public const string WriteValue = "management.write";

    /// <summary>
    /// The wire value of <see cref="ManagementPermission.Admin"/>.
    /// </summary>
    public const string AdminValue = "management.admin";

    /// <summary>
    /// Gets every defined permission in the fixed emission order.
    /// </summary>
    public static IReadOnlyList<ManagementPermission> All { get; } =
    [
        ManagementPermission.Read,
        ManagementPermission.Write,
        ManagementPermission.Admin,
    ];

    /// <summary>
    /// Gets the stable wire value of a defined permission.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The permission is not a defined value.</exception>
    public static string ToWireValue(ManagementPermission permission) => permission switch
    {
        ManagementPermission.Read => ReadValue,
        ManagementPermission.Write => WriteValue,
        ManagementPermission.Admin => AdminValue,
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    /// <summary>
    /// Attempts to map a claim value to a defined permission.
    /// </summary>
    /// <remarks>
    /// The comparison is exact and ordinal: whitespace, casing, and Unicode-equivalent variants are
    /// rejected rather than normalized.
    /// </remarks>
    public static bool TryParse(string? value, out ManagementPermission permission)
    {
        switch (value)
        {
            case ReadValue:
                permission = ManagementPermission.Read;
                return true;
            case WriteValue:
                permission = ManagementPermission.Write;
                return true;
            case AdminValue:
                permission = ManagementPermission.Admin;
                return true;
            default:
                permission = default;
                return false;
        }
    }

    /// <summary>
    /// Determines whether a value is a defined permission.
    /// </summary>
    public static bool IsDefined(ManagementPermission permission) =>
        permission is ManagementPermission.Read
            or ManagementPermission.Write
            or ManagementPermission.Admin;
}
