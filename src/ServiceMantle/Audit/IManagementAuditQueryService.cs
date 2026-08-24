namespace ServiceMantle.Audit;

/// <summary>
/// Provides bounded keyset-paginated queries over persisted management audit records.
/// </summary>
public interface IManagementAuditQueryService
{
    /// <summary>
    /// Executes a validated audit query.
    /// </summary>
    /// <param name="query">The bounded filter and paging request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<ManagementAuditQueryResult> QueryAsync(
        ManagementAuditQuery query,
        CancellationToken cancellationToken = default);
}
