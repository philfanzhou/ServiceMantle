using Microsoft.EntityFrameworkCore;
using ServiceMantle.Audit;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// EF Core-based <see cref="IManagementAuditQueryService"/> providing bounded keyset pagination over
/// <c>service_audit_logs</c>. A continuation cursor carries the snapshot boundary and the last
/// occurrence/id key, so inserts after the first page cannot shift later pages and duplicate records.
/// </summary>
public sealed class EfCoreManagementAuditQueryService<TDbContext> : IManagementAuditQueryService
    where TDbContext : DbContext, IServiceMantleAuditDbContext
{
    private readonly TDbContext dbContext;

    /// <summary>
    /// Initializes a new management audit query service.
    /// </summary>
    /// <param name="dbContext">The business DbContext implementing <see cref="IServiceMantleAuditDbContext"/>.</param>
    public EfCoreManagementAuditQueryService(TDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        this.dbContext = dbContext;
    }

    /// <inheritdoc />
    public async ValueTask<ManagementAuditQueryResult> QueryAsync(
        ManagementAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (query.Cursor is null && query.Page != 1)
        {
            throw new ManagementAuditException(
                "audit.query_cursor_required",
                "A continuation cursor is required for pages after the first page.");
        }

        var filtered = Filter(dbContext.ServiceAuditLogs.AsNoTracking(), query);
        var cursor = query.Cursor is null
            ? (ManagementAuditContinuationCursor?)null
            : ManagementAuditContinuationCursor.Decode(query.Cursor);

        if (cursor.HasValue && cursor.Value.SortOrder != query.SortOrder)
        {
            throw new ManagementAuditException(
                "audit.query_cursor_invalid",
                "The audit query continuation cursor does not match the requested sort order.");
        }

        var snapshot = cursor ?? await FindSnapshotAsync(filtered, query.SortOrder, cancellationToken)
            .ConfigureAwait(false);

        if (!snapshot.HasValue)
        {
            return new ManagementAuditQueryResult([], query.Page, query.PageSize, 0);
        }

        var snapshotFiltered = ApplySnapshot(filtered, snapshot.Value);
        var totalCount = await snapshotFiltered.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var pageSource = cursor.HasValue
            ? ApplyCursor(snapshotFiltered, cursor.Value)
            : snapshotFiltered;
        var ordered = Order(pageSource, query.SortOrder);
        var entities = await ordered
            .Take(query.PageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasNext = entities.Count > query.PageSize;
        if (hasNext)
        {
            entities.RemoveAt(query.PageSize);
        }

        var items = entities.Select(ManagementAuditEntityMapper.ConvertToRecord).ToList();
        var continuationCursor = hasNext && entities.Count > 0
            ? ManagementAuditContinuationCursor.Encode(
                new ManagementAuditContinuationCursor(
                    snapshot.Value.SnapshotOccurredAtUtc,
                    snapshot.Value.SnapshotId,
                    new DateTimeOffset(DateTime.SpecifyKind(entities[^1].OccurredAtUtc, DateTimeKind.Utc)),
                    entities[^1].Id,
                    query.SortOrder))
            : null;

        return new ManagementAuditQueryResult(
            items,
            query.Page,
            query.PageSize,
            totalCount,
            continuationCursor);
    }

    private static async Task<ManagementAuditContinuationCursor?> FindSnapshotAsync(
        IQueryable<ManagementAuditLogEntity> filtered,
        ManagementAuditSortOrder sortOrder,
        CancellationToken cancellationToken)
    {
        var latest = await filtered
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenByDescending(item => item.Id)
            .Select(item => new { item.OccurredAtUtc, item.Id })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return latest is null
            ? null
            : new ManagementAuditContinuationCursor(
                new DateTimeOffset(DateTime.SpecifyKind(latest.OccurredAtUtc, DateTimeKind.Utc)),
                latest.Id,
                default,
                Guid.Empty,
                sortOrder);
    }

    private static IQueryable<ManagementAuditLogEntity> ApplySnapshot(
        IQueryable<ManagementAuditLogEntity> source,
        ManagementAuditContinuationCursor cursor)
    {
        return source.Where(item => item.OccurredAtUtc < cursor.SnapshotOccurredAtUtc.UtcDateTime
            || (item.OccurredAtUtc == cursor.SnapshotOccurredAtUtc.UtcDateTime
                && item.Id.CompareTo(cursor.SnapshotId) <= 0));
    }

    private static IQueryable<ManagementAuditLogEntity> ApplyCursor(
        IQueryable<ManagementAuditLogEntity> source,
        ManagementAuditContinuationCursor cursor)
    {
        return cursor.SortOrder == ManagementAuditSortOrder.Oldest
            ? source.Where(item => item.OccurredAtUtc > cursor.LastOccurredAtUtc.UtcDateTime
                || (item.OccurredAtUtc == cursor.LastOccurredAtUtc.UtcDateTime
                    && item.Id.CompareTo(cursor.LastId) > 0))
            : source.Where(item => item.OccurredAtUtc < cursor.LastOccurredAtUtc.UtcDateTime
                || (item.OccurredAtUtc == cursor.LastOccurredAtUtc.UtcDateTime
                    && item.Id.CompareTo(cursor.LastId) < 0));
    }

    private static IOrderedQueryable<ManagementAuditLogEntity> Order(
        IQueryable<ManagementAuditLogEntity> source,
        ManagementAuditSortOrder sortOrder) =>
        sortOrder == ManagementAuditSortOrder.Oldest
            ? source.OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.Id)
            : source.OrderByDescending(item => item.OccurredAtUtc).ThenByDescending(item => item.Id);

    private static IQueryable<ManagementAuditLogEntity> Filter(
        IQueryable<ManagementAuditLogEntity> source,
        ManagementAuditQuery query)
    {
        if (query.Action is not null)
        {
            source = source.Where(item => item.Action == query.Action.Value);
        }

        if (query.TargetType is not null)
        {
            source = source.Where(item => item.TargetType == query.TargetType.Value);
        }

        if (query.TargetId is not null)
        {
            source = source.Where(item => item.TargetId == query.TargetId);
        }

        if (query.OperatorId is not null)
        {
            source = source.Where(item => item.OperatorId == query.OperatorId);
        }

        if (query.FromUtc.HasValue)
        {
            var fromUtc = query.FromUtc.Value.UtcDateTime;
            source = source.Where(item => item.OccurredAtUtc >= fromUtc);
        }

        if (query.ToUtc.HasValue)
        {
            var toUtc = query.ToUtc.Value.UtcDateTime;
            source = source.Where(item => item.OccurredAtUtc <= toUtc);
        }

        return source;
    }
}
