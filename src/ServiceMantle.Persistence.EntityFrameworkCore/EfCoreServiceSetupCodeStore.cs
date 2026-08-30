using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ServiceMantle.Installation;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// EF Core adapter for the one-time Setup Code attached to a pending installation row.
/// </summary>
/// <remarks>
/// The adapter extends the existing <c>service_installations</c> mapping and never introduces a
/// second Pending/Completed authority. See <see cref="IServiceSetupCodeStore"/> for the transaction
/// ownership, clean-context, and post-rollback DbContext rules.
/// </remarks>
public sealed class EfCoreServiceSetupCodeStore<TDbContext> : IServiceSetupCodeStore
    where TDbContext : DbContext, IServiceMantleDbContext
{
    private readonly TDbContext dbContext;
    private readonly TimeProvider timeProvider;
    private readonly SetupCodeLifetime lifetime;

    /// <summary>
    /// Initializes a new Setup Code store.
    /// </summary>
    /// <param name="dbContext">The business DbContext implementing <see cref="IServiceMantleDbContext"/>.</param>
    /// <param name="timeProvider">Optional time provider for deterministic operations.</param>
    /// <param name="lifetime">Optional Setup Code lifetime; defaults to 30 minutes.</param>
    public EfCoreServiceSetupCodeStore(
        TDbContext dbContext,
        TimeProvider? timeProvider = null,
        SetupCodeLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        this.dbContext = dbContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.lifetime = lifetime ?? SetupCodeLifetime.Default;
    }

    /// <inheritdoc />
    public ValueTask<SetupCodeIssueResult> CreateAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default) =>
        IssueAsync(serviceId, rotate: false, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SetupCodeIssueResult> RotateAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default) =>
        IssueAsync(serviceId, rotate: true, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<SetupCodeValidationResult> ValidateAsync(
        ServiceId serviceId,
        string candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        // Validation is entirely read only: it neither tracks the row nor changes state or version.
        var entity = await LoadAsync(serviceId, tracked: false, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return SetupCodeValidationResult.Rejected(
                WellKnownSetupCodeErrorCodes.InstallationNotFound);
        }

        var rejection = EvaluateCandidate(entity, candidate, out _);
        return rejection is null
            ? SetupCodeValidationResult.Valid()
            : SetupCodeValidationResult.Rejected(rejection);
    }

    /// <inheritdoc />
    public async ValueTask<SetupCodeConsumptionResult> StageConsumeAsync(
        ServiceId serviceId,
        string candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        // Unrelated dirty entries are allowed so that consumption can join the caller's unit of work,
        // but the target row must not already carry uncommitted caller changes.
        DetectChanges();
        if (dbContext.ChangeTracker
            .Entries<ServiceInstallationEntity>()
            .Any(entry => entry.State is EntityState.Added
                    or EntityState.Modified
                    or EntityState.Deleted
                && string.Equals(entry.Entity.ServiceId, serviceId.Value, StringComparison.Ordinal)))
        {
            return SetupCodeConsumptionResult.Rejected(WellKnownSetupCodeErrorCodes.DirtyContext);
        }

        var entity = await LoadAsync(serviceId, tracked: true, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return SetupCodeConsumptionResult.Rejected(
                WellKnownSetupCodeErrorCodes.InstallationNotFound);
        }

        if (!CanIncrementVersion(entity))
        {
            return SetupCodeConsumptionResult.Rejected(
                WellKnownSetupCodeErrorCodes.StateInvariantViolation);
        }

        var rejection = EvaluateCandidate(entity, candidate, out var nowUtc);
        if (rejection is not null)
        {
            return SetupCodeConsumptionResult.Rejected(rejection);
        }

        entity.SetupCodeDigest = null;
        entity.SetupCodeIssuedAtUtc = null;
        entity.SetupCodeExpiresAtUtc = null;
        entity.Status = InstallationStatus.Completed;
        entity.CompletedAtUtc = nowUtc;
        entity.Version += 1;

        return SetupCodeConsumptionResult.Staged(
            ServiceInstallationEntityStateMapper.ConvertToState(entity));
    }

    /// <summary>
    /// Brings entry states up to date before a precondition reads them.
    /// </summary>
    /// <remarks>
    /// <see cref="ChangeTracker.Entries()"/> reports the last detected state, not the current one. A
    /// caller that sets <see cref="ChangeTracker.AutoDetectChangesEnabled"/> to false and then
    /// modifies a tracked entity leaves it reported as <see cref="EntityState.Unchanged"/>, which
    /// would let a pending change slip past the clean-context precondition and be committed by the
    /// save this operation owns, or let consumption validate against uncommitted caller values.
    /// Detecting explicitly is a read of current state; it does not change the caller's setting.
    /// </remarks>
    private void DetectChanges() => dbContext.ChangeTracker.DetectChanges();

    private async ValueTask<SetupCodeIssueResult> IssueAsync(
        ServiceId serviceId,
        bool rotate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        cancellationToken.ThrowIfCancellationRequested();

        // Create and Rotate own their single SaveChanges, so any pre-existing pending change would be
        // committed with them. Refusing outright is what keeps that from happening.
        DetectChanges();
        if (dbContext.ChangeTracker.Entries().Any(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            return SetupCodeIssueResult.Rejected(WellKnownSetupCodeErrorCodes.DirtyContext);
        }

        var preTrackedEntry = TrackedEntry(serviceId);
        var entity = await LoadAsync(serviceId, tracked: true, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return SetupCodeIssueResult.Rejected(
                WellKnownSetupCodeErrorCodes.InstallationNotFound);
        }

        var entry = dbContext.Entry(entity);
        var wasPreTracked = preTrackedEntry is not null;

        var baseRejection = ValidateBaseState(entity);
        if (baseRejection is not null)
        {
            return SetupCodeIssueResult.Rejected(baseRejection);
        }

        if (!CanIncrementVersion(entity))
        {
            return SetupCodeIssueResult.Rejected(
                WellKnownSetupCodeErrorCodes.StateInvariantViolation);
        }

        if (entity.Status == InstallationStatus.Completed)
        {
            return SetupCodeIssueResult.Rejected(
                WellKnownSetupCodeErrorCodes.InstallationCompleted);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var material = Evaluate(entity);
        if (material.Status == SetupCodeMaterialStatus.Corrupt || nowUtc < entity.CreatedAtUtc)
        {
            return SetupCodeIssueResult.Rejected(WellKnownSetupCodeErrorCodes.StorageCorrupt);
        }

        if (rotate)
        {
            if (material.Status == SetupCodeMaterialStatus.NeverIssued)
            {
                return SetupCodeIssueResult.Rejected(WellKnownSetupCodeErrorCodes.NotCreated);
            }

            if (entity.SetupCodeGeneration == int.MaxValue)
            {
                return SetupCodeIssueResult.Rejected(
                    WellKnownSetupCodeErrorCodes.GenerationExhausted);
            }
        }
        else if (material.Status == SetupCodeMaterialStatus.Issued)
        {
            return SetupCodeIssueResult.Rejected(WellKnownSetupCodeErrorCodes.AlreadyExists);
        }

        if (DateTime.MaxValue - nowUtc < lifetime.Value)
        {
            return SetupCodeIssueResult.Rejected(WellKnownSetupCodeErrorCodes.StorageCorrupt);
        }

        var setupCode = SetupCode.Generate();
        var expiresAtUtc = nowUtc + lifetime.Value;
        var generation = entity.SetupCodeGeneration + 1;

        entity.SetupCodeGeneration = generation;
        entity.SetupCodeDigest = SetupCodeDigest.Compute(setupCode).Value;
        entity.SetupCodeIssuedAtUtc = nowUtc;
        entity.SetupCodeExpiresAtUtc = expiresAtUtc;
        entity.Version += 1;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RestoreEntry(entry, wasPreTracked);
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            RestoreEntry(entry, wasPreTracked);
            return SetupCodeIssueResult.Rejected(
                WellKnownSetupCodeErrorCodes.ConcurrencyConflict);
        }
        catch (Exception exception)
        {
            RestoreEntry(entry, wasPreTracked);
            throw new ServiceInstallationStoreException(
                "installation.storage_error",
                "Failed to store the installation Setup Code.",
                exception);
        }

        // The plaintext only exists in a result once the save has actually succeeded.
        return SetupCodeIssueResult.Issued(setupCode, generation, expiresAtUtc);
    }

    private string? EvaluateCandidate(
        ServiceInstallationEntity entity,
        string candidate,
        out DateTime nowUtc)
    {
        nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var baseRejection = ValidateBaseState(entity);
        if (baseRejection is not null)
        {
            return baseRejection;
        }

        if (entity.Status == InstallationStatus.Completed)
        {
            return WellKnownSetupCodeErrorCodes.InstallationCompleted;
        }

        var material = Evaluate(entity);
        if (material.Status == SetupCodeMaterialStatus.Corrupt || nowUtc < entity.CreatedAtUtc)
        {
            return WellKnownSetupCodeErrorCodes.StorageCorrupt;
        }

        if (!SetupCode.TryParse(candidate, out var parsedCandidate) || parsedCandidate is null)
        {
            return WellKnownSetupCodeErrorCodes.Invalid;
        }

        // Expiry is decided before the digest comparison so that expired material stays a stable
        // expired answer even when the candidate does not match.
        if (material.IsExpired(nowUtc))
        {
            return WellKnownSetupCodeErrorCodes.Expired;
        }

        return material.Status == SetupCodeMaterialStatus.Issued &&
            material.Digest!.Matches(parsedCandidate)
            ? null
            : WellKnownSetupCodeErrorCodes.Invalid;
    }

    private static string? ValidateBaseState(ServiceInstallationEntity entity)
    {
        try
        {
            ServiceInstallationEntityStateMapper.Validate(entity);
            return null;
        }
        catch (ServiceInstallationStoreException exception)
        {
            return exception.ErrorCode;
        }
    }

    private static bool CanIncrementVersion(ServiceInstallationEntity entity) =>
        entity.Version != int.MaxValue;

    private static SetupCodeMaterial Evaluate(ServiceInstallationEntity entity) =>
        SetupCodeMaterial.Evaluate(
            entity.SetupCodeGeneration,
            entity.SetupCodeDigest,
            entity.SetupCodeIssuedAtUtc,
            entity.SetupCodeExpiresAtUtc,
            entity.CreatedAtUtc);

    private static void RestoreEntry(
        EntityEntry<ServiceInstallationEntity> entry,
        bool wasPreTracked)
    {
        // Only the entry this operation touched is cleaned up. The caller's own tracked entries are
        // never detached, saved, or cleared.
        if (wasPreTracked)
        {
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
            return;
        }

        entry.State = EntityState.Detached;
    }

    private EntityEntry<ServiceInstallationEntity>? TrackedEntry(ServiceId serviceId) =>
        dbContext.ChangeTracker
            .Entries<ServiceInstallationEntity>()
            .FirstOrDefault(entry => string.Equals(
                entry.Entity.ServiceId,
                serviceId.Value,
                StringComparison.Ordinal));

    private async ValueTask<ServiceInstallationEntity?> LoadAsync(
        ServiceId serviceId,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var query = dbContext.ServiceInstallations.AsQueryable();
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query
            .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
            .ConfigureAwait(false);
    }
}
