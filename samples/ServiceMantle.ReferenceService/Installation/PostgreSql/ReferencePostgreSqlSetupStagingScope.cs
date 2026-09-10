using Microsoft.EntityFrameworkCore;
using ServiceMantle.Installation;
using ServiceMantle.ReferenceService.Database.PostgreSql;

namespace ServiceMantle.ReferenceService.Installation.PostgreSql;

/// <summary>
/// The minimum unit-of-work surface setup orchestration needs, over the caller's own PostgreSQL
/// context.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HasPendingChanges"/> reports what the caller's change tracker holds after an explicit
/// <c>DetectChanges</c>: an <c>Added</c>, <c>Modified</c>, or <c>Deleted</c> entry. Unchanged
/// entries are not pending work.
/// <see cref="DiscardPendingChangesAsync"/> clears that tracker and nothing else.
/// </para>
/// <para>
/// This adapter never saves, never begins, commits, or rolls back a transaction, and never disposes
/// the context. Clearing the tracker discards staged work only: rows the caller has already
/// persisted are untouched, and nothing here can undo a database operation the caller has already
/// committed.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlSetupStagingScope : IServiceSetupStagingScope
{
    private readonly ReferencePostgreSqlDbContext context;

    /// <summary>Creates the scope over the caller's own context.</summary>
    /// <param name="context">The caller's context. Its lifetime stays with the caller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public ReferencePostgreSqlSetupStagingScope(ReferencePostgreSqlDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    /// <inheritdoc />
    public bool HasPendingChanges
    {
        get
        {
            context.ChangeTracker.DetectChanges();
            return context.ChangeTracker.Entries().Any(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        }
    }

    /// <inheritdoc />
    /// <remarks>Clears the caller's change tracker. It saves, commits, and rolls back nothing.</remarks>
    public ValueTask DiscardPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.ChangeTracker.Clear();
        return ValueTask.CompletedTask;
    }
}
