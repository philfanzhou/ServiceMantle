namespace ServiceMantle.Audit;

/// <summary>
/// Common audit target types reusable across consuming services. Consuming services may define
/// additional target types with <see cref="ManagementAuditTargetType.Parse(string)"/>.
/// </summary>
public static class WellKnownManagementAuditTargetTypes
{
    /// <summary>
    /// The target is the service deployment itself.
    /// </summary>
    public static ManagementAuditTargetType Service { get; } = ManagementAuditTargetType.Parse("service");

    /// <summary>
    /// The target is an administrative session (login/logout events).
    /// </summary>
    public static ManagementAuditTargetType AdminSession { get; } =
        ManagementAuditTargetType.Parse("admin_session");

    /// <summary>
    /// The target is a service configuration value or configuration set.
    /// </summary>
    public static ManagementAuditTargetType Configuration { get; } =
        ManagementAuditTargetType.Parse("configuration");
}
