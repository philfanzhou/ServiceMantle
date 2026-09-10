using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Health;

namespace ServiceMantle.AspNetCore.Health;

/// <summary>
/// The default decision source: it reads the consumer-owned base snapshot once, applies the finite
/// base matrix, and then combines the ordered readiness contributors under their shared budget.
/// </summary>
/// <remarks>
/// The snapshot source is resolved from the provider this instance was created for, so a scoped
/// source keeps working when the decision is requested inside a request scope. A missing source, a
/// resolution failure, a null snapshot, an internal timeout, or a source failure all fail closed
/// without a snapshot. A caller cancellation that has been requested when this read reaches its
/// output outranks all of those: the read ends with an <see cref="OperationCanceledException"/>
/// carrying the caller's own token rather than with a finite failure code, and that holds for an
/// ordinary source failure and a <see cref="TimeoutException"/> just as it does for a completed
/// snapshot. Caller cancellation is notified on the token the source received before the linked
/// source is released. An internal timeout without a caller cancellation stays a timeout; the
/// cancellation the caller may observe after this checkpoint is not covered.
/// </remarks>
internal sealed class ServiceMantleReadinessDecisionSource(
    IServiceProvider serviceProvider,
    ServiceMantleHealthRegistration registration) : IServiceReadinessDecisionSource
{
    private const string ProbeFailed = WellKnownServiceReadinessDecisionErrorCodes.ProbeFailed;

    private const string ProbeTimeout = WellKnownServiceReadinessDecisionErrorCodes.ProbeTimeout;

    public async ValueTask<ServiceReadinessDecision> GetDecisionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IServiceHealthSnapshotSource? source;
        try
        {
            source = serviceProvider.GetService<IServiceHealthSnapshotSource>();
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ServiceReadinessDecision.Unavailable(ProbeFailed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (source is null)
        {
            return ServiceReadinessDecision.Unavailable(ProbeFailed);
        }

        using var timeout = new CancellationTokenSource(registration.ProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        ServiceHealthSnapshot? snapshot;
        try
        {
            snapshot = await source.GetSnapshotAsync(linked.Token)
                .AsTask()
                .WaitAsync(registration.ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return SnapshotFailure(linked, cancellationToken, ProbeTimeout);
        }
        catch (TimeoutException)
        {
            return SnapshotFailure(linked, cancellationToken, ProbeTimeout);
        }
        catch
        {
            return SnapshotFailure(linked, cancellationToken, ProbeFailed);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }

        if (snapshot is null)
        {
            return ServiceReadinessDecision.Unavailable(ProbeFailed);
        }

        if (!ServiceHealthEvaluator.Evaluate(snapshot).IsReady)
        {
            return ServiceReadinessDecision.NotReady(snapshot, snapshot.ErrorCode);
        }

        ServiceReadinessContributorCombiner combiner;
        try
        {
            combiner = serviceProvider.GetRequiredService<ServiceReadinessContributorCombiner>();
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ContributorFailed(snapshot);
        }

        ServiceReadinessContributorResult? contribution;
        try
        {
            contribution = await combiner
                .EvaluateAsync(snapshot, registration.ContributorTimeout, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ContributorFailed(snapshot);
        }

        if (contribution is null)
        {
            return ContributorFailed(snapshot);
        }

        return contribution.IsReady
            ? ServiceReadinessDecision.Ready(snapshot)
            : ServiceReadinessDecision.NotReady(
                snapshot,
                contribution.ErrorCode ??
                    WellKnownServiceReadinessContributorErrorCodes.ContributorFailed);
    }

    /// <summary>
    /// The one exit every snapshot failure passes through: a caller cancellation that was requested
    /// by this point outranks the finite failure classification this read would otherwise report.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancelled the read.</exception>
    private static ServiceReadinessDecision SnapshotFailure(
        CancellationTokenSource linked,
        CancellationToken cancellationToken,
        string errorCode)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }

        return ServiceReadinessDecision.Unavailable(errorCode);
    }

    /// <summary>
    /// Owns the cancellation exit: the snapshot source is notified on the token it received before
    /// the linked source is released, and the caller still observes its own cancellation.
    /// </summary>
    private static OperationCanceledException CancelledByCaller(
        CancellationTokenSource linked,
        CancellationToken cancellationToken)
    {
        try
        {
            linked.Cancel();
        }
        catch (AggregateException)
        {
            // Cancellation callbacks that throw are outside the cooperative cancellation contract
            // and must not replace the caller's cancellation result.
        }

        return new OperationCanceledException(cancellationToken);
    }

    private static ServiceReadinessDecision ContributorFailed(ServiceHealthSnapshot snapshot) =>
        ServiceReadinessDecision.NotReady(
            snapshot,
            WellKnownServiceReadinessContributorErrorCodes.ContributorFailed);
}
