namespace ServiceMantle.Management;

/// <summary>
/// Defines the stable claim types that carry the ServiceMantle management operator identity.
/// </summary>
/// <remarks>
/// Both claim type names and claim values are matched as an exact ordinal wire contract. Any other
/// claim type is ignored and never contributes to the resolved identity.
/// </remarks>
public static class ManagementClaimTypes
{
    /// <summary>
    /// The claim type carrying the operator identifier.
    /// </summary>
    public const string OperatorId = "servicemantle.operator_id";

    /// <summary>
    /// The claim type carrying the operator identity source.
    /// </summary>
    public const string OperatorSource = "servicemantle.operator_source";

    /// <summary>
    /// The claim type carrying the operator display name.
    /// </summary>
    public const string OperatorDisplayName = "servicemantle.operator_display_name";

    /// <summary>
    /// The claim type carrying one management permission.
    /// </summary>
    public const string Permission = "servicemantle.permission";
}
