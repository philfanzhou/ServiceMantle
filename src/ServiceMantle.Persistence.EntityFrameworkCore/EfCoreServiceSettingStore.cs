using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ServiceMantle.Configuration;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// Stores one complete shared setting aggregate per service with optimistic concurrency.
/// </summary>
/// <remarks>
/// Each operation creates and disposes a dedicated DbContext from the supplied factory. Update owns
/// one explicit transaction and one SaveChanges call, so it never commits a caller's shared work
/// unit. Caller cancellation propagates as a sanitized <see cref="OperationCanceledException"/>.
/// </remarks>
public sealed class EfCoreServiceSettingStore<TDbContext> : IServiceSettingStore
    where TDbContext : DbContext
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 64
    };

    private readonly IDbContextFactory<TDbContext> dbContextFactory;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a shared setting store with a dedicated-context factory.</summary>
    public EfCoreServiceSettingStore(
        IDbContextFactory<TDbContext> dbContextFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dbContextFactory);
        this.dbContextFactory = dbContextFactory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<ServiceSettingStoreSnapshot> LoadAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var dbContext = await dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            var entity = await dbContext.Set<ServiceSettingEntity>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
                .ConfigureAwait(false);
            return entity is null
                ? EmptySnapshot(serviceId)
                : ToSnapshot(serviceId, entity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw SafeCancellation(cancellationToken);
        }
        catch (ServiceSettingStoreException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StorageException(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ServiceSettingStoreUpdateResult> UpdateAsync(
        ServiceId serviceId,
        ServiceSettingStoreUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        TDbContext? dbContext = null;
        IDbContextTransaction? transaction = null;
        try
        {
            dbContext = await dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var entity = await dbContext.Set<ServiceSettingEntity>()
                .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
                .ConfigureAwait(false);
            var currentVersion = entity?.Version ?? 0;
            if (currentVersion != update.ExpectedVersion)
            {
                return ServiceSettingStoreUpdateResult.Failure(
                    WellKnownServiceSettingStoreErrorCodes.VersionConflict);
            }

            if (currentVersion == long.MaxValue)
            {
                return ServiceSettingStoreUpdateResult.Failure(
                    WellKnownServiceSettingStoreErrorCodes.VersionExhausted);
            }

            Dictionary<string, string> values;
            try
            {
                values = entity is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : DeserializeValues(entity.ValuesJson);
            }
            catch (JsonException)
            {
                return ServiceSettingStoreUpdateResult.Failure(
                    WellKnownServiceSettingStoreErrorCodes.StorageCorrupt);
            }

            foreach (var (key, value) in update.Changes)
            {
                if (value is null)
                {
                    values.Remove(key);
                }
                else
                {
                    values[key] = value;
                }
            }

            var nextVersion = currentVersion + 1;
            var valuesJson = JsonSerializer.Serialize(
                new SortedDictionary<string, string>(values, StringComparer.Ordinal),
                SerializerOptions);
            if (entity is null)
            {
                entity = new ServiceSettingEntity { ServiceId = serviceId.Value };
                dbContext.Set<ServiceSettingEntity>().Add(entity);
            }

            entity.ValuesJson = valuesJson;
            entity.Version = nextVersion;
            entity.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            entity.UpdatedBy = update.UpdatedBy;
            entity.RestartRequired = update.RestartRequired;

            cancellationToken.ThrowIfCancellationRequested();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ServiceSettingStoreUpdateResult.Success(nextVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw SafeCancellation(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return ServiceSettingStoreUpdateResult.Failure(
                WellKnownServiceSettingStoreErrorCodes.VersionConflict);
        }
        catch (DbUpdateException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            if (update.ExpectedVersion == 0 && await RowExistsAsync(serviceId).ConfigureAwait(false))
            {
                return ServiceSettingStoreUpdateResult.Failure(
                    WellKnownServiceSettingStoreErrorCodes.VersionConflict);
            }

            return ServiceSettingStoreUpdateResult.Failure(
                WellKnownServiceSettingStoreErrorCodes.ConstraintViolation);
        }
        catch (JsonException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return ServiceSettingStoreUpdateResult.Failure(
                WellKnownServiceSettingStoreErrorCodes.StorageCorrupt);
        }
        catch (Exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return ServiceSettingStoreUpdateResult.Failure(
                WellKnownServiceSettingStoreErrorCodes.StorageError);
        }
        finally
        {
            await DisposeAsync(transaction).ConfigureAwait(false);
            await DisposeAsync(dbContext).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> RowExistsAsync(ServiceId serviceId)
    {
        try
        {
            await using var dbContext = await dbContextFactory
                .CreateDbContextAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return await dbContext.Set<ServiceSettingEntity>()
                .AsNoTracking()
                .AnyAsync(item => item.ServiceId == serviceId.Value, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> DeserializeValues(string valuesJson)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(
            valuesJson,
            SerializerOptions) ?? throw new JsonException();
        var materialized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (value is null || !materialized.TryAdd(key, value))
            {
                throw new JsonException("The persisted setting value object is invalid.");
            }
        }

        return materialized;
    }

    private static ServiceSettingStoreSnapshot ToSnapshot(
        ServiceId serviceId,
        ServiceSettingEntity entity)
    {
        if (entity.Version <= 0 || entity.UpdatedAtUtc == default)
        {
            throw ServiceSettingStoreException.Failure(
                WellKnownServiceSettingStoreErrorCodes.StorageCorrupt);
        }

        try
        {
            var values = DeserializeValues(entity.ValuesJson);
            return new ServiceSettingStoreSnapshot(
                serviceId,
                entity.Version,
                new ReadOnlyDictionary<string, string>(values),
                new DateTimeOffset(DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc)),
                entity.UpdatedBy,
                entity.RestartRequired);
        }
        catch (JsonException exception)
        {
            throw ServiceSettingStoreException.Failure(
                WellKnownServiceSettingStoreErrorCodes.StorageCorrupt,
                exception);
        }
    }

    private static ServiceSettingStoreSnapshot EmptySnapshot(ServiceId serviceId) =>
        new(
            serviceId,
            version: 0,
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            updatedAtUtc: null,
            updatedBy: null,
            restartRequired: false);

    private static async ValueTask RollbackAsync(IDbContextTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup failures never replace the safe primary result or caller cancellation.
        }
    }

    private static async ValueTask DisposeAsync(IAsyncDisposable? resource)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Cleanup failures never replace the safe primary result or caller cancellation.
        }
    }

    private static OperationCanceledException SafeCancellation(CancellationToken cancellationToken) =>
        new("The shared setting operation was cancelled by the caller.", cancellationToken);

    private static ServiceSettingStoreException StorageException(Exception exception) =>
        ServiceSettingStoreException.Failure(
            WellKnownServiceSettingStoreErrorCodes.StorageError,
            exception);
}
