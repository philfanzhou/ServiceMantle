namespace ServiceMantle.Audit;

/// <summary>
/// Classifies the security-relevant result of an audited management action. This is the bounded
/// result vocabulary shared by high-risk conventions such as installation, administrative login, and
/// configuration change events.
/// </summary>
public enum ManagementAuditOutcome
{
    /// <summary>
    /// The outcome was not classified by the caller.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The action completed as intended.
    /// </summary>
    Success = 1,

    /// <summary>
    /// The action was attempted but did not complete as intended.
    /// </summary>
    Failure = 2,

    /// <summary>
    /// The action was refused by policy or authorization checks.
    /// </summary>
    Denied = 3
}
