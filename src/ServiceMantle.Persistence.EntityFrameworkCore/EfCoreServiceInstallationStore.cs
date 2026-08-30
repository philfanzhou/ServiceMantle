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

        var entity = await dbContext.ServiceInstallations
            .AsNoTracking()
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

        throw new ServiceInstallationStoreException(
            WellKnownSetupCodeErrorCodes.SetupCodeRequired,
            "A pending installation must be completed by consuming its Setup Code.");
    }
}
