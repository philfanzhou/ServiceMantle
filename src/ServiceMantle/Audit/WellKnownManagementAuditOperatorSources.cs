namespace ServiceMantle.Audit;

/// <summary>
/// Common operator identity sources reusable across consuming services. Consuming services may
/// define additional sources with <see cref="ManagementAuditOperatorSource.Parse(string)"/>.
/// </summary>
public static class WellKnownManagementAuditOperatorSources
{
    /// <summary>
    /// The operator is the service itself, with no human operator involved (for example installation).
    /// </summary>
    public static ManagementAuditOperatorSource System { get; } = ManagementAuditOperatorSource.Parse("system");

    /// <summary>
    /// The operator is a human administrator acting through an interactive session.
    /// </summary>
    public static ManagementAuditOperatorSource InteractiveAdmin { get; } =
        ManagementAuditOperatorSource.Parse("interactive_admin");

    /// <summary>
    /// The operator is a non-interactive service account or automated process.
    /// </summary>
    public static ManagementAuditOperatorSource ServiceAccount { get; } =
        ManagementAuditOperatorSource.Parse("service_account");

    /// <summary>
    /// The operator's identity could not be established.
    /// </summary>
    public static ManagementAuditOperatorSource Anonymous { get; } =
        ManagementAuditOperatorSource.Parse("anonymous");
}
