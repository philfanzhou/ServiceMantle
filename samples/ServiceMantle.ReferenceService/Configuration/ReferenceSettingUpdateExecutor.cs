using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ServiceMantle.AspNetCore.ManagementApi.SettingUpdates;
using ServiceMantle.Configuration;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Database.PostgreSql;

namespace ServiceMantle.ReferenceService.Configuration;

/// <summary>
/// The sample's caller-owned commit boundary for the shared management setting-update entry.
/// </summary>
/// <remarks>
/// The shared endpoint validates and projects; this executor owns the unit of work, exactly as
/// the contract requires: a fresh async scope, one transaction on the scoped
/// <see cref="ReferencePostgreSqlDbContext"/>, the shared <see cref="ServiceSettingUpdateService"/>
/// inside it, and a commit only after an applied result. The commit itself observes
/// <see cref="CancellationToken.None"/> on purpose: once the commit started, a caller abort must
/// not cut it into an unknown outcome, while an abort that arrives earlier still rolls the whole
/// scope back. Any failure rolls back and rethrows; a rollback failure is swallowed because the
/// scope and its connection are discarded immediately after, so nothing uncommitted survives.
/// </remarks>
internal static class ReferenceSettingUpdateExecutor
{
    internal static async ValueTask<ServiceSettingUpdateResult> ExecuteAsync(
        HttpContext httpContext,
        ServiceSettingUpdateCommand command,
        CancellationToken cancellationToken)
    {
        await using var scope = httpContext.RequestServices
            .GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<ReferencePostgreSqlDbContext>();
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var updater = new ServiceSettingUpdateService(
                services.GetRequiredService<ServiceId>(),
                services.GetRequiredService<ServiceSettingDefinitionRegistry>(),
                new EfCoreServiceSettingUpdateTransaction<ReferencePostgreSqlDbContext>(dbContext),
                services.GetRequiredService<IServiceSettingRootKeySource>());
            var result = await updater.UpdateAsync(command, cancellationToken).ConfigureAwait(false);
            if (result.Status != ServiceSettingUpdateStatus.Applied)
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task TryRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The scope and its connection are discarded right after; nothing uncommitted survives.
        }
    }
}
