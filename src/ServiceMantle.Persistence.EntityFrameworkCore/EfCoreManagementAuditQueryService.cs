using Microsoft.EntityFrameworkCore;
using ServiceMantle.Audit;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// EF Core-based <see cref="IManagementAuditQueryService"/> providing bounded keyset pagination over
/// <c>service_audit_logs</c>. A continuation cursor binds the normalized query and carries the last
/// occurrence/id key, preventing cursor reuse with different filters or pagination parameters.
/// Continuations use ordinary keyset semantics rather than claiming a database snapshot: concurrent
/// backfilled rows may appear on a later page and counts may change between requests.
/// </summary>
public sealed class EfCoreManagementAuditQueryService<TDbContext> : IManagementAuditQueryService
    where TDbContext : DbContext
{
    private readonly TDbContext dbContext;

    /// <summary>
    /// Initializes a new management audit query service.
    /// </summary>
    /// <param name="dbContext">
    /// The business DbContext whose model includes <see cref="ManagementAuditModelBuilderExtensions.AddServiceMantleManagementAudit"/>.
    /// </param>
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

        var filtered = Filter(dbContext.Set<ManagementAuditLogEntity>().AsNoTracking(), query);
        var cursor = query.Cursor is null
            ? (ManagementAuditContinuationCursor?)null
            : ManagementAuditContinuationCursor.Decode(query.Cursor);

        if (cursor.HasValue && !cursor.Value.Matches(query))
        {
            throw new ManagementAuditException(
                "audit.query_cursor_invalid",
                "The audit query continuation cursor does not match the requested query.");
        }

        var totalCount = await filtered.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var pageSource = cursor.HasValue
            ? ApplyCursor(filtered, cursor.Value, query.SortOrder)
            : filtered;
        var ordered = Order(pageSource, query.SortOrder);

        // Persisted byte ceilings are evaluated as part of the same SQL statement that loads the
        // page: there is no separate precheck round trip for a concurrently written oversized row
        // to slip through. Required text columns cannot be null at the store level (their guards
        // would be pruned from the SQL anyway); anything else a dirty row contains is rejected by
        // ConvertToRecord below, which stays the authoritative validation boundary.
        List<ManagementAuditLogEntity> entities;
        try
        {
            var page = await ordered
                .Take(query.PageSize + 1)
                .Select(item => new
                {
                    item,
                    ViolatesPersistedLimits =
                        item.Id == Guid.Empty
                        || ManagementAuditDatabaseFunctions.TextByteLength(item.OperatorId)
                            > ManagementAuditEntityMapper.MaxPersistedTextByteLength(
                                ManagementAuditOperator.MaxOperatorIdLength)
                        || (item.OperatorDisplayName != null
                            && ManagementAuditDatabaseFunctions.TextByteLength(item.OperatorDisplayName)
                                > ManagementAuditEntityMapper.MaxPersistedTextByteLength(
                                    ManagementAuditOperator.MaxDisplayNameLength))
                        || ManagementAuditDatabaseFunctions.TextByteLength(item.OperatorSource)
                            > ManagementAuditEntityMapper.MaxPersistedTextByteLength(
                                ManagementAuditOperatorSource.MaxLength)
                        || ManagementAuditDatabaseFunctions.TextByteLength(item.Action)
                            > ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditAction.MaxLength)
                        || ManagementAuditDatabaseFunctions.TextByteLength(item.TargetType)
                            > ManagementAuditEntityMapper.MaxPersistedTextByteLength(ManagementAuditTargetType.MaxLength)
                        || ManagementAuditDatabaseFunctions.TextByteLength(item.TargetId)
                            > ManagementAuditEntityMapper.MaxPersistedTextByteLength(
                                ManagementAuditTarget.MaxTargetIdLength)
                        || (item.ClientIp != null
                            && ManagementAuditDatabaseFunctions.TextByteLength(item.ClientIp)
                                > ManagementAuditEntityMapper.MaxPersistedTextByteLength(
                                    ManagementAuditEvent.MaxClientIpLength))
                        || (item.CorrelationId != null
                            && ManagementAuditDatabaseFunctions.TextByteLength(item.CorrelationId)
                                > ManagementAuditEntityMapper.MaxPersistedTextByteLength(
                                    ManagementAuditEvent.MaxCorrelationIdLength))
                        || (item.SecurityDescription != null
                            && ManagementAuditDatabaseFunctions.TextByteLength(item.SecurityDescription)
                                > ManagementAuditEntityMapper.MaxPersistedTextByteLength(
                                    ManagementAuditEvent.MaxDescriptionLength))
                        || (item.MetadataJson != null
                            && ManagementAuditDatabaseFunctions.TextByteLength(item.MetadataJson)
                                > ManagementAuditEntityMapper.MaxMetadataJsonByteLength)
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in page)
            {
                if (row.ViolatesPersistedLimits)
                {
                    throw ManagementAuditEntityMapper.InvalidStoredEntity();
                }
            }

            entities = page.Select(row => row.item).ToList();
        }
        catch (FormatException exception)
        {
            throw ManagementAuditEntityMapper.InvalidStoredEntity(exception);
        }

        var hasNext = entities.Count > query.PageSize;
        if (hasNext)
        {
            entities.RemoveAt(query.PageSize);
        }

        var items = entities.Select(ManagementAuditEntityMapper.ConvertToRecord).ToList();
        var continuationCursor = hasNext && entities.Count > 0
            ? ManagementAuditContinuationCursor.Encode(ManagementAuditContinuationCursor.Create(
                query,
                new DateTimeOffset(DateTime.SpecifyKind(entities[^1].OccurredAtUtc, DateTimeKind.Utc)),
                entities[^1].Id))
            : null;

        return new ManagementAuditQueryResult(
            items,
            query.Page,
            query.PageSize,
            totalCount,
            continuationCursor);
    }

    private static IQueryable<ManagementAuditLogEntity> ApplyCursor(
        IQueryable<ManagementAuditLogEntity> source,
        ManagementAuditContinuationCursor cursor,
        ManagementAuditSortOrder sortOrder)
    {
        return sortOrder == ManagementAuditSortOrder.Oldest
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
