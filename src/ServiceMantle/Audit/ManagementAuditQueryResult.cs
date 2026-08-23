namespace ServiceMantle.Audit;

/// <summary>
/// A stable, bounded page of management audit records.
/// </summary>
public sealed record ManagementAuditQueryResult
{
    /// <summary>
    /// Gets the records for the requested page, in the requested sort order.
    /// </summary>
    public IReadOnlyList<ManagementAuditRecord> Items { get; }

    /// <summary>
    /// Gets the one-based page number this result represents.
    /// </summary>
    public int Page { get; }

    /// <summary>
    /// Gets the page size used to produce this result.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets the total number of records matching the query across all pages.
    /// </summary>
    public long TotalCount { get; }

    /// <summary>
    /// Gets a value indicating whether a subsequent page contains further records.
    /// </summary>
    public bool HasNextPage => (long)Page * PageSize < TotalCount;

    public ManagementAuditQueryResult(
        IReadOnlyList<ManagementAuditRecord> items,
        int page,
        int pageSize,
        long totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
    }
}
