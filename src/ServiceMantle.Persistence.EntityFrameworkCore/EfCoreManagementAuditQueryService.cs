using Microsoft.EntityFrameworkCore;
using ServiceMantle.Audit;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// EF Core-based <see cref="IManagementAuditQueryService"/> providing stable, bounded pagination over
/// <c>service_audit_logs</c>. Results are ordered by occurrence time with the record identifier as a
/// tiebreaker so pages stay stable even when multiple records share the same timestamp.
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

        var filtered = Filter(dbContext.ServiceAuditLogs.AsNoTracking(), query);

        var totalCount = await filtered.LongCountAsync(cancellationToken).ConfigureAwait(false);

        var ordered = query.SortOrder == ManagementAuditSortOrder.Oldest
            ? filtered.OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.Id)
            : filtered.OrderByDescending(item => item.OccurredAtUtc).ThenByDescending(item => item.Id);

        var skip = (query.Page - 1) * query.PageSize;
        var entities = await ordered
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = entities.Select(ManagementAuditEntityMapper.ConvertToRecord).ToList();

        return new ManagementAuditQueryResult(items, query.Page, query.PageSize, totalCount);
    }

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
