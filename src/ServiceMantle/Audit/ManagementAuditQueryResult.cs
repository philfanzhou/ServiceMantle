namespace ServiceMantle.Audit;

/// <summary>
/// A bounded page of management audit records. The continuation cursor provides ordinary keyset
/// pagination rather than a database snapshot; <see cref="TotalCount"/> is observed per request and
/// may change when rows are inserted or deleted concurrently.
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
    /// Gets the opaque keyset cursor for the next page, or null when there are no more records.
    /// </summary>
    public string? ContinuationCursor { get; }

    /// <summary>
    /// Gets a value indicating whether a subsequent page contains further records.
    /// </summary>
    public bool HasNextPage => ContinuationCursor is not null
        || (ContinuationCursor is null && (long)Page * PageSize < TotalCount);

    public ManagementAuditQueryResult(
        IReadOnlyList<ManagementAuditRecord> items,
        int page,
        int pageSize,
        long totalCount,
        string? continuationCursor = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
        ContinuationCursor = continuationCursor;
    }
}
