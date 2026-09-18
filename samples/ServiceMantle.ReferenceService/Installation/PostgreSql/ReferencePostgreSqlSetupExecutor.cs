using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ServiceMantle.AspNetCore.ManagementApi.Setup;
using ServiceMantle.Audit;
using ServiceMantle.Installation;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Database.PostgreSql;

namespace ServiceMantle.ReferenceService.Installation.PostgreSql;

/// <summary>
/// The sample's one-shot Setup completion executor on PostgreSQL: the whole installation - the
/// consumed Setup Code, the business workspace, and one audit row - commits in a single
/// consumer-owned transaction or not at all.
/// </summary>
/// <remarks>
/// <para>
/// The sequence is the one the shared Setup entry contract fixes: a fresh asynchronous scope with
/// a clean <see cref="ReferencePostgreSqlDbContext"/>, its own transaction, a read-only code
/// validation, the setup orchestrator over the staging contributor and scope, one staged audit
/// row, staged consumption, exactly one <c>SaveChangesAsync</c>, and a commit that must complete
/// before <see cref="SetupCompletionResult.Committed"/> is returned. The commit runs on
/// <see cref="CancellationToken.None"/> so a caller disconnect after the save cannot interrupt an
/// in-flight commit into an unknown outcome; the caller's token is checked once, right before the
/// commit starts.
/// </para>
/// <para>
/// Every non-committed outcome rolls back and discards the whole scope first; a rollback failure
/// is swallowed under the outcome that already won. The executor never retries and never reuses a
/// context. Any other exception - including internal and caller cancellation - is rethrown after
/// the rollback for the shared handler to classify.
/// </para>
/// </remarks>
internal static class ReferencePostgreSqlSetupExecutor
{
    internal static async ValueTask<SetupCompletionResult> ExecuteAsync(
        HttpContext httpContext,
        SetupCode setupCode,
        CancellationToken cancellationToken)
    {
        await using var scope = httpContext.RequestServices
            .GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var services = scope.ServiceProvider;
        var serviceId = services.GetRequiredService<ServiceId>();
        var context = services.GetRequiredService<ReferencePostgreSqlDbContext>();
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var store = new EfCoreServiceSetupCodeStore<ReferencePostgreSqlDbContext>(context);
            var validation = await store
                .ValidateAsync(serviceId, setupCode.Reveal(), cancellationToken)
                .ConfigureAwait(false);
            if (!validation.IsValid)
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                return Map(validation.ErrorCode);
            }

            var orchestration = await new ServiceSetupOrchestrator(
                    [new ReferencePostgreSqlSetupContributor(context)],
                    new ReferencePostgreSqlSetupStagingScope(context))
                .OrchestrateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!orchestration.Succeeded)
            {
                // The sample's contributor has no semantic rejection, so an orchestrator failure is
                // staging or cleanup, never a validation outcome.
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                return SetupCompletionResult.Unavailable();
            }

            await new EfCoreManagementAuditWriter<ReferencePostgreSqlDbContext>(context).RecordAsync(
                ManagementAuditEvent.Create(
                    ManagementAuditOperator.System(),
                    WellKnownManagementAuditActions.InstallationCompleted,
                    ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Service, serviceId.Value),
                    ManagementAuditOutcome.Success,
                    occurredAtUtc: DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            var consumption = await store
                .StageConsumeAsync(serviceId, setupCode.Reveal(), cancellationToken)
                .ConfigureAwait(false);
            if (!consumption.IsStaged)
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                return Map(consumption.ErrorCode);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another completion committed the installation row first; this attempt has
                // nothing left to win.
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                return SetupCompletionResult.Conflict();
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return SetupCompletionResult.Committed();
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static SetupCompletionResult Map(string? errorCode) => errorCode switch
    {
        WellKnownSetupCodeErrorCodes.InstallationCompleted or
            WellKnownSetupCodeErrorCodes.ConcurrencyConflict =>
            SetupCompletionResult.Conflict(),
        WellKnownSetupCodeErrorCodes.Invalid or
            WellKnownSetupCodeErrorCodes.Expired =>
            SetupCompletionResult.CredentialInvalid(),
        _ => SetupCompletionResult.Unavailable(),
    };

    private static async Task TryRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The outcome that already won is the one this call reports.
        }
    }
}
