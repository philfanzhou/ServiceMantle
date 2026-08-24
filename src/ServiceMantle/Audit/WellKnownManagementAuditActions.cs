namespace ServiceMantle.Audit;

/// <summary>
/// Common management audit actions reusable across consuming services for installation,
/// administrative login, and configuration change events. Consuming services may define additional
/// actions with <see cref="ManagementAuditAction.Parse(string)"/>.
/// </summary>
public static class WellKnownManagementAuditActions
{
    /// <summary>
    /// Initial service installation completed.
    /// </summary>
    public static ManagementAuditAction InstallationCompleted { get; } =
        ManagementAuditAction.Parse("installation.completed");

    /// <summary>
    /// An administrator successfully authenticated to the management surface.
    /// </summary>
    public static ManagementAuditAction AdminLoginSucceeded { get; } =
        ManagementAuditAction.Parse("admin_login.succeeded");

    /// <summary>
    /// An administrator login attempt failed.
    /// </summary>
    public static ManagementAuditAction AdminLoginFailed { get; } =
        ManagementAuditAction.Parse("admin_login.failed");

    /// <summary>
    /// An administrator ended a management session.
    /// </summary>
    public static ManagementAuditAction AdminLogout { get; } =
        ManagementAuditAction.Parse("admin_login.logout");

    /// <summary>
    /// A service configuration value or configuration set was changed.
    /// </summary>
    public static ManagementAuditAction ConfigurationChanged { get; } =
        ManagementAuditAction.Parse("configuration.changed");
}
