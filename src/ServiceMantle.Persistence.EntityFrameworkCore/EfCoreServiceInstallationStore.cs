using Microsoft.EntityFrameworkCore;
using ServiceMantle.Installation;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// EF Core-based implementation for shared service installation state.
/// </summary>
public sealed class EfCoreServiceInstallationStore<TDbContext> : IServiceInstallationStore
    where TDbContext : DbContext, IServiceMantleDbContext
{
    private readonly TDbContext dbContext;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Initializes a new installation store.
    /// </summary>
    /// <param name="dbContext">The business DbContext implementing <see cref="IServiceMantleDbContext"/>.</param>
    /// <param name="timeProvider">Optional time provider for deterministic operations.</param>
    public EfCoreServiceInstallationStore(TDbContext dbContext, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        this.dbContext = dbContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Finds service installation state.
    /// </summary>
    public async ValueTask<ServiceInstallationState?> FindAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = await dbContext.ServiceInstallations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ServiceInstallationEntityStateMapper.ConvertToState(entity);
    }

    /// <summary>
    /// Creates a pending installation record if absent and returns current state.
    /// </summary>
    public async ValueTask<ServiceInstallationState> CreatePendingAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await dbContext.ServiceInstallations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return ServiceInstallationEntityStateMapper.ConvertToState(existing);
        }

        var pending = new ServiceInstallationEntity
        {
            ServiceId = serviceId.Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            CompletedAtUtc = null,
            Version = 1
        };
        var pendingEntry = dbContext.ServiceInstallations.Add(pending);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pendingEntry.State = EntityState.Detached;
            throw;
        }
        catch (DbUpdateException)
        {
            pendingEntry.State = EntityState.Detached;

            var current = await dbContext.ServiceInstallations
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
                .ConfigureAwait(false);

            if (current is not null)
            {
                return ServiceInstallationEntityStateMapper.ConvertToState(current);
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            pendingEntry.State = EntityState.Detached;
            throw new ServiceInstallationStoreException(
                "installation.storage_error",
                "Failed to create a pending installation state.",
                exception);
        }

        return ServiceInstallationEntityStateMapper.ConvertToState(pending);
    }

    /// <summary>
    /// Marks installation as completed when pending.
    /// </summary>
    public async ValueTask<ServiceInstallationState> MarkCompletedAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = await dbContext.ServiceInstallations
            .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            throw new ServiceInstallationStoreException(
                "installation.not_found",
                "The installation state does not exist.");
        }

        ServiceInstallationEntityStateMapper.Validate(entity);

        if (entity.Status == InstallationStatus.Completed)
        {
            return ServiceInstallationEntityStateMapper.ConvertToState(entity);
        }

        if (entity.Status != InstallationStatus.PendingSetup)
        {
            throw new ServiceInstallationStoreException(
                "installation.state_unsupported",
                "The installation state cannot be completed.");
        }

        var completedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (completedAtUtc < entity.CreatedAtUtc)
        {
            throw new ServiceInstallationStoreException(
                "installation.state_invariant_violation",
                "The installation completion time is before creation time.");
        }

        if (entity.Version == int.MaxValue)
        {
            throw new ServiceInstallationStoreException(
                "installation.state_invariant_violation",
                "The installation version cannot be incremented safely.");
        }

        var nextVersion = entity.Version + 1;
        entity.Status = InstallationStatus.Completed;
        entity.CompletedAtUtc = completedAtUtc;
        entity.Version = nextVersion;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.Entry(entity).State = EntityState.Detached;

            var finalState = await dbContext.ServiceInstallations
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
                .ConfigureAwait(false);

            if (finalState is null)
            {
                throw;
            }

            var final = ServiceInstallationEntityStateMapper.ConvertToState(finalState);
            if (final.IsCompleted)
            {
                return final;
            }

            throw new ServiceInstallationStoreException(
                "installation.concurrency_conflict",
                "The installation state was concurrently updated.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            throw new ServiceInstallationStoreException(
                "installation.storage_error",
                "Failed to mark installation as completed.",
                exception);
        }

        return ServiceInstallationEntityStateMapper.ConvertToState(entity);
    }
}
