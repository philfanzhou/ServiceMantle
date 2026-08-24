namespace ServiceMantle.Audit;

/// <summary>
/// Bounded sort orders supported by management audit queries. Arbitrary column sorting is not
/// supported so query results stay predictable and injection-safe.
/// </summary>
public enum ManagementAuditSortOrder
{
    /// <summary>
    /// Most recent events first (default).
    /// </summary>
    Newest = 0,

    /// <summary>
    /// Oldest events first.
    /// </summary>
    Oldest = 1
}
