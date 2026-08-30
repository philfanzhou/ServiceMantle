using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

        var entity = await LoadAsync(serviceId, cancellationToken).ConfigureAwait(false);

        return entity is null ? null : ServiceInstallationEntityStateMapper.ConvertToState(entity);
    }

    /// <inheritdoc />
    public async ValueTask<ServiceInstallationState> CreatePendingAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        cancellationToken.ThrowIfCancellationRequested();

        // This operation owns one SaveChanges when the row is absent. Detect explicitly so a caller
        // cannot hide a pending change by disabling automatic change detection.
        dbContext.ChangeTracker.DetectChanges();
        if (dbContext.ChangeTracker.Entries().Any(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            throw new ServiceInstallationStoreException(
                WellKnownSetupCodeErrorCodes.DirtyContext,
                "A clean DbContext is required to create a pending installation state.");
        }

        var existing = await LoadAsync(serviceId, cancellationToken).ConfigureAwait(false);

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
            Version = 1,
            SetupCodeGeneration = 0,
            SetupCodeDigest = null,
            SetupCodeIssuedAtUtc = null,
            SetupCodeExpiresAtUtc = null
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
        catch (DbUpdateException exception)
        {
            var failedInstallationInsert = IsFailedInstallationInsert(exception, pendingEntry);
            pendingEntry.State = EntityState.Detached;

            if (!failedInstallationInsert)
            {
                throw StorageFailure(exception);
            }

            ServiceInstallationEntity? current;
            try
            {
                current = await dbContext.ServiceInstallations
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception recoveryException)
            {
                throw StorageFailure(recoveryException);
            }

            if (current is not null)
            {
                return ServiceInstallationEntityStateMapper.ConvertToState(current);
            }

            throw StorageFailure(exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            pendingEntry.State = EntityState.Detached;
            throw StorageFailure(exception);
        }

        return ServiceInstallationEntityStateMapper.ConvertToState(pending);
    }

    private static bool IsFailedInstallationInsert(
        DbUpdateException exception,
        EntityEntry<ServiceInstallationEntity> pendingEntry)
    {
        if (exception.Entries.Count != 1)
        {
            return false;
        }

        var failedEntry = exception.Entries[0];
        return failedEntry.State == EntityState.Added
            && failedEntry.Metadata.ClrType == typeof(ServiceInstallationEntity)
            && ReferenceEquals(failedEntry.Entity, pendingEntry.Entity);
    }

    private static ServiceInstallationStoreException StorageFailure(Exception exception) =>
        new(
            "installation.storage_error",
            "Failed to create a pending installation state.",
            exception);

    private async ValueTask<ServiceInstallationEntity?> LoadAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.ServiceInstallations
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ServiceInstallationStoreException(
                "installation.storage_error",
                "Failed to read the service installation state.",
                exception);
        }
    }

    /// <summary>
    /// Returns completed installation state, or refuses to complete a pending installation.
    /// </summary>
    /// <remarks>
    /// This entry point is retained so that no unrelated public API is removed, but its behaviour is
    /// fixed: a pending row stably raises <c>installation.setup_code_required</c>, because completing
    /// a pending installation must go through
    /// <see cref="IServiceSetupCodeStore.StageConsumeAsync"/> so that the Setup Code is actually
    /// validated and consumed. An already completed row stays an idempotent read.
    /// </remarks>
    public async ValueTask<ServiceInstallationState> MarkCompletedAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = await LoadAsync(serviceId, cancellationToken).ConfigureAwait(false);

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

        throw new ServiceInstallationStoreException(
            WellKnownSetupCodeErrorCodes.SetupCodeRequired,
            "A pending installation must be completed by consuming its Setup Code.");
    }
}
