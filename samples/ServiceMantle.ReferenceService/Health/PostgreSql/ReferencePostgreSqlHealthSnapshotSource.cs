using Microsoft.EntityFrameworkCore;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Database.PostgreSql;

namespace ServiceMantle.ReferenceService.Health.PostgreSql;

/// <summary>
/// The reference service's one live <see cref="IServiceHealthSnapshotSource"/> on PostgreSQL: it
/// re-reads the installation row on every request so the reported phase reflects authoritative
/// database fact, never the startup gate's frozen result and never a cached snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The source holds no state between reads. Each read confirms the startup gate reached Ready, then
/// creates its own context from the factory, makes exactly one <c>FindAsync</c> through
/// <see cref="EfCoreServiceInstallationStore{TDbContext}"/>, and releases that context before the
/// read ends. It never caches, polls in the background, retries, writes, or captures a request-scoped
/// <see cref="DbContext"/>, so a singleton registration is safe.
/// </para>
/// <para>
/// The gate result is a startup-time fact used only to prove the host is past migration; the phase is
/// resolved from the row read this request through <see cref="ServiceStartupPhaseResolver"/>, never
/// from the gate's own startup phase. Migration is reported <see cref="ServiceMigrationReadinessState.Succeeded"/>
/// because the host only accepts a request after the gate is Ready; the database is reported
/// <see cref="ServiceDatabaseReadinessState.Reachable"/> only when this read succeeded. This source
/// never produces an <see cref="ServiceDatabaseReadinessState.Unreachable"/> snapshot: when the row
/// cannot be observed there is no phase to report, so it fails closed with
/// <see cref="ReferencePostgreSqlHealthSnapshotUnavailableException"/> instead of guessing.
/// </para>
/// <para>
/// Every finite failure - the gate is not Ready, the installation row is missing, the store raises any
/// <see cref="ServiceInstallationStoreException"/>, creating, reading, or releasing the context fails,
/// or an internal cancellation the caller did not request arrives - leaves the same fixed exception
/// carrying no inner exception, no provider text, no connection string, and no user name. The health
/// pipeline classifies it as <c>health.probe_failed</c>, or as <c>health.probe_timeout</c> when the
/// read does not finish within the pipeline's own budget. A caller cancellation that has been requested
/// by the checkpoint after the context is released outranks both the observation and every finite
/// failure, and is reported with the caller's own token.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlHealthSnapshotSource : IServiceHealthSnapshotSource
{
    private readonly IDbContextFactory<ReferencePostgreSqlDbContext> contextFactory;
    private readonly ServiceId serviceId;
    private readonly ReferencePostgreSqlStartupHostedService startup;

    /// <summary>Creates the source over the fixed identity and the gate that owns startup.</summary>
    /// <param name="contextFactory">
    /// The factory each read creates its own short-lived context from. A factory is used deliberately:
    /// this source is registered as a singleton and must never capture a request-scoped context.
    /// </param>
    /// <param name="serviceId">The stable identity whose installation row is read.</param>
    /// <param name="startup">The gate whose Ready result proves the host is past migration.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ReferencePostgreSqlHealthSnapshotSource(
        IDbContextFactory<ReferencePostgreSqlDbContext> contextFactory,
        ServiceId serviceId,
        ReferencePostgreSqlStartupHostedService startup)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(startup);
        this.contextFactory = contextFactory;
        this.serviceId = serviceId;
        this.startup = startup;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The read is made and the context this read owns is released before the caller's token is read
    /// one last time; a cancellation requested by that checkpoint is reported with the caller's own
    /// token, outranking the observation and every finite failure. This source builds no timeout of
    /// its own - the health pipeline's budget owns the <c>health.probe_timeout</c> classification -
    /// and it does not forcibly interrupt an uncooperative provider. A cancellation arriving after
    /// that checkpoint is not covered.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancelled the read.</exception>
    /// <exception cref="ReferencePostgreSqlHealthSnapshotUnavailableException">
    /// The phase could not be observed as authoritative database fact this request.
    /// </exception>
    public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The gate result is a startup-time fact: it only proves the host reached Ready and is past
        // migration. A host that has not published a Ready result has no observable live phase, so
        // this fails closed rather than projecting the gate's own startup phase or guessing one.
        var result = startup.Result;
        if (result is null || !result.IsReady)
        {
            throw new ReferencePostgreSqlHealthSnapshotUnavailableException();
        }

        ServiceInstallationState? state;
        try
        {
            state = await ReadInstallationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // Creating, reading, or releasing the context failed, or the store raised any of its
            // finite error codes. The provider's own text, the connection string, and the user name
            // are deliberately not carried into the fixed failure, and an internal cancellation the
            // caller did not ask for is an unobservable phase like any other.
            throw new ReferencePostgreSqlHealthSnapshotUnavailableException();
        }

        // The context this read owned has been released; the caller's cancellation outranks both the
        // observation it had already made and the finite failure a missing row would otherwise raise.
        cancellationToken.ThrowIfCancellationRequested();

        if (state is null)
        {
            // A deleted or never-present row is never resolved to PendingSetup: the source does not
            // hand a missing row to the resolver, so the Setup entry cannot be reopened by this path.
            throw new ReferencePostgreSqlHealthSnapshotUnavailableException();
        }

        var phase = ServiceStartupPhaseResolver.Resolve(hasBootstrapConfiguration: true, state);
        return new ServiceHealthSnapshot(
            phase,
            ServiceMigrationReadinessState.Succeeded,
            ServiceDatabaseReadinessState.Reachable);
    }

    private async ValueTask<ServiceInstallationState?> ReadInstallationAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var store = new EfCoreServiceInstallationStore<ReferencePostgreSqlDbContext>(context);
        return await store.FindAsync(serviceId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The single fixed failure this source raises whenever the live phase could not be observed as
/// authoritative database fact. It carries a fixed message and no inner exception, provider text,
/// connection string, or user name, so the health pipeline can classify it as
/// <c>health.probe_failed</c> without projecting anything sensitive.
/// </summary>
public sealed class ReferencePostgreSqlHealthSnapshotUnavailableException : Exception
{
    /// <summary>The fixed message carried by every instance.</summary>
    public const string FixedMessage =
        "The reference PostgreSQL health snapshot could not be observed.";

    /// <summary>Creates the fixed failure with no inner exception.</summary>
    public ReferencePostgreSqlHealthSnapshotUnavailableException()
        : base(FixedMessage)
    {
    }
}
