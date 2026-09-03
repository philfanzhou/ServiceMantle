using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ServiceMantle.Audit;
using ServiceMantle.Configuration;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>Saves settings and audit records in an existing caller-owned relational transaction.</summary>
/// <remarks>
/// Requires savepoints, no pending tracked changes and no tracked setting aggregate. Only this
/// operation's changes are saved; the caller commits the outer transaction. On any failure the
/// caller should roll back and dispose its transaction, particularly if the connection is unusable.
/// Register this adapter and ServiceSettingUpdateService as scoped services for the same DbContext.
/// </remarks>
public sealed class EfCoreServiceSettingUpdateTransaction<TDbContext>(
    TDbContext dbContext,
    TimeProvider? timeProvider = null) : IServiceSettingUpdateTransaction
    where TDbContext : DbContext
{
    private readonly TDbContext dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private Guid? readTransactionId;

    /// <inheritdoc />
    public async ValueTask<ServiceSettingStoreSnapshot> LoadAsync(
        ServiceId serviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        readTransactionId = dbContext.Database.CurrentTransaction?.TransactionId;
        var entity = await dbContext.Set<ServiceSettingEntity>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
            .ConfigureAwait(false);
        return entity is null
            ? EfCoreServiceSettingStore<TDbContext>.EmptySnapshot(serviceId)
            : EfCoreServiceSettingStore<TDbContext>.ToSnapshot(serviceId, entity);
    }

    /// <inheritdoc />
    public async ValueTask<ServiceSettingUpdateResult> ApplyAsync(
        ServiceId serviceId,
        ServiceSettingStoreUpdate update,
        IReadOnlyList<ManagementAuditEvent> audits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(audits);
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = dbContext.Database.CurrentTransaction;
        if (transaction is null || !transaction.SupportsSavepoints ||
            transaction.TransactionId != readTransactionId)
        {
            return Failure(ServiceSettingUpdateStatus.TransactionRequired);
        }
        dbContext.ChangeTracker.DetectChanges();
        if (dbContext.ChangeTracker.HasChanges() || dbContext.ChangeTracker.Entries<ServiceSettingEntity>().Any())
        {
            return Failure(ServiceSettingUpdateStatus.ContextNotClean);
        }

        var savepoint = "sm_settings_" + Guid.NewGuid().ToString("N");
        var owned = new List<object>();
        var saved = false;
        var createdSavepoint = false;
        try
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            createdSavepoint = true;
            var entity = await dbContext.Set<ServiceSettingEntity>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.ServiceId == serviceId.Value, cancellationToken)
                .ConfigureAwait(false);
            var current = entity is null
                ? EfCoreServiceSettingStore<TDbContext>.EmptySnapshot(serviceId)
                : EfCoreServiceSettingStore<TDbContext>.ToSnapshot(serviceId, entity);
            if (current.Version != update.ExpectedVersion)
            {
                return Failure(ServiceSettingUpdateStatus.VersionConflict);
            }
            if (current.Version == long.MaxValue)
            {
                return Failure(ServiceSettingUpdateStatus.VersionExhausted);
            }
            var values = new Dictionary<string, string>(current.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in update.Changes)
            {
                if (value is null) values.Remove(key);
                else values[key] = value;
            }

            if (entity is null)
            {
                entity = new ServiceSettingEntity { ServiceId = serviceId.Value };
                dbContext.Add(entity);
            }
            else
            {
                dbContext.Attach(entity);
            }
            owned.Add(entity);
            entity.ValuesJson = JsonSerializer.Serialize(new SortedDictionary<string, string>(values, StringComparer.Ordinal));
            entity.Version = current.Version + 1;
            entity.UpdatedAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            entity.UpdatedBy = update.UpdatedBy;
            entity.RestartRequired = update.RestartRequired;
            foreach (var audit in audits)
            {
                var auditEntity = ManagementAuditEntityMapper.ConvertToEntity(Guid.NewGuid(), audit);
                owned.Add(auditEntity);
                dbContext.Add(auditEntity);
            }

            cancellationToken.ThrowIfCancellationRequested();
            dbContext.ChangeTracker.DetectChanges();
            await dbContext.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            saved = true;
            return ServiceSettingUpdateResult.Applied(entity.Version);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The setting batch was cancelled by the caller.", cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failure(ServiceSettingUpdateStatus.VersionConflict);
        }
        catch (DbUpdateException) when (update.ExpectedVersion == 0)
        {
            if (await RollbackAsync(transaction, savepoint).ConfigureAwait(false))
            {
                try
                {
                    if (await dbContext.Set<ServiceSettingEntity>().AsNoTracking()
                        .AnyAsync(item => item.ServiceId == serviceId.Value, cancellationToken).ConfigureAwait(false))
                    {
                        return Failure(ServiceSettingUpdateStatus.VersionConflict);
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // A failed diagnostic read must not expose provider details.
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(ServiceSettingUpdateStatus.StorageFailed);
        }
        catch
        {
            return Failure(ServiceSettingUpdateStatus.StorageFailed);
        }
        finally
        {
            if (createdSavepoint && !saved)
            {
                await RollbackAsync(transaction, savepoint).ConfigureAwait(false);
            }
            foreach (var entity in owned)
            {
                dbContext.Entry(entity).State = EntityState.Detached;
            }
        }
    }

    private static async ValueTask<bool> RollbackAsync(IDbContextTransaction transaction, string name)
    {
        try
        {
            await transaction.RollbackToSavepointAsync(name, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ServiceSettingUpdateResult Failure(ServiceSettingUpdateStatus status) =>
        ServiceSettingUpdateResult.Failure(status);
}
