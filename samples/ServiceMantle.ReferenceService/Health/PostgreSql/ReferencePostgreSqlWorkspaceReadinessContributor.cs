using Microsoft.EntityFrameworkCore;
using ServiceMantle.Health;
using ServiceMantle.ReferenceService.Database.PostgreSql;

namespace ServiceMantle.ReferenceService.Health.PostgreSql;

/// <summary>
/// A consumer-owned example of a business readiness veto on PostgreSQL: given a base-ready snapshot,
/// it accepts only while at least one workspace row can be observed.
/// </summary>
/// <remarks>
/// <para>
/// The contributor holds no state between evaluations. Each evaluation creates its own context from
/// the factory and releases it before the evaluation ends, so a singleton registration is safe and
/// no caller-scoped <c>DbContext</c> is captured.
/// </para>
/// <para>
/// The base matrix decides first. When the snapshot the caller sampled is not base-ready, no context
/// is created and no SQL runs; the answer is the fixed <c>reference.phase_not_ready</c>. Only a
/// base-ready snapshot leads to one read-only existence check on
/// <c>public.reference_workspaces</c>. The result carries no workspace id, no display name, no row
/// count, and no provider text.
/// </para>
/// <para>
/// Ready here means only that the caller supplied a base-ready snapshot and that at least one
/// workspace was observed at that moment. It is not proof that the snapshot reflects authoritative
/// state, that an installation is <c>Completed</c>, that any row's fields are valid, or that the
/// schema, configuration, audit, capacity, or signing keys are in order. Nothing prevents the row
/// from being removed immediately afterwards.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlWorkspaceReadinessContributor : IServiceReadinessContributor
{
    /// <summary>The caller's snapshot was not base-ready, so no business read was made.</summary>
    public const string PhaseNotReady = "reference.phase_not_ready";

    /// <summary>The read succeeded and found no workspace.</summary>
    public const string WorkspaceMissing = "reference.workspace_missing";

    /// <summary>The read could not be made. It carries no provider text.</summary>
    public const string WorkspaceProbeFailed = "reference.workspace_probe_failed";

    private readonly IDbContextFactory<ReferencePostgreSqlDbContext> contextFactory;

    /// <summary>Creates the contributor over a context factory.</summary>
    /// <param name="contextFactory">
    /// The factory each evaluation creates its own short-lived context from. A factory is used
    /// deliberately: this contributor may be registered as a singleton, and a scoped context must
    /// never be captured by one.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="contextFactory"/> is null.</exception>
    public ReferencePostgreSqlWorkspaceReadinessContributor(
        IDbContextFactory<ReferencePostgreSqlDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        this.contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    /// <remarks>
    /// The read is made, the context this evaluation owns is released, and only then is the caller's
    /// token read one last time; a cancellation requested by that checkpoint is reported with the
    /// caller's own token. This contributor builds no timeout of its own - the combiner's shared
    /// budget owns that classification - and it does not forcibly interrupt an uncooperative
    /// provider. A cancellation arriving after the checkpoint is not covered.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancelled the evaluation.</exception>
    public async ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
        ServiceHealthSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ServiceHealthEvaluator.Evaluate(snapshot).IsReady)
        {
            // A snapshot that is not base-ready is answered without creating a context or reading.
            return ServiceReadinessContributorResult.NotReady(PhaseNotReady);
        }

        bool observed;
        try
        {
            observed = await HasWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // Creating the context, reading, or releasing it failed. The provider's own text is
            // deliberately not carried into the finite rejection, and an internal cancellation the
            // caller did not ask for is an unreadable probe like any other.
            return ServiceReadinessContributorResult.NotReady(WorkspaceProbeFailed);
        }

        // The context this evaluation owned has been released; the caller's cancellation outranks
        // the observation it had already made.
        cancellationToken.ThrowIfCancellationRequested();
        return observed
            ? ServiceReadinessContributorResult.Ready()
            : ServiceReadinessContributorResult.NotReady(WorkspaceMissing);
    }

    private async ValueTask<bool> HasWorkspaceAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        // Read-only: existence only, nothing tracked, no row content loaded.
        return await context.Workspaces
            .AsNoTracking()
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
