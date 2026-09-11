using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;

namespace ServiceMantle.AspNetCore.ManagementApi.Status;

/// <summary>
/// Assembles the one coherent installation observation the anonymous status entry answers with.
/// </summary>
/// <remarks>
/// The handler parses nothing: the entry declares no route value, query parameter, header, or body.
/// It reads the consumer health source at most once, the local Bootstrap status at most once, and
/// the process-local restart latch at most once, then either projects the five fixed fields or
/// answers the single closed rejection. It performs no write, no retry, no caching, and no
/// background work, and it holds no mutable object across requests.
/// </remarks>
internal static class InstallationStatusHandler
{
    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IBootstrapStatusReader bootstrap,
        BootstrapRestartLatch latch,
        TimeSpan budget)
    {
        var cancellationToken = context.RequestAborted;
        cancellationToken.ThrowIfCancellationRequested();

        using var timeout = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        ServiceHealthSnapshot? snapshot;
        try
        {
            var source = context.RequestServices.GetService<IServiceHealthSnapshotSource>();
            snapshot = source is null
                ? null
                : await source.GetSnapshotAsync(linked.Token).AsTask()
                    .WaitAsync(budget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }
        catch
        {
            // A source failure, an internal timeout, and an unrelated internal cancellation are one
            // safe outcome here. The reason itself is never projected.
            snapshot = null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }

        if (snapshot is null)
        {
            return InstallationStatusResult.Unavailable;
        }

        bool bootstrapConfigured;
        try
        {
            // Exactly one bit crosses the Bootstrap boundary; the projection is never serialized.
            bootstrapConfigured = bootstrap.IsBootstrapConfigured();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }
        catch
        {
            // A damaged, mismatched, or unreadable Bootstrap file is unavailable, not "absent".
            return InstallationStatusResult.Unavailable;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw CancelledByCaller(linked, cancellationToken);
        }

        if (timeout.IsCancellationRequested)
        {
            // The local Bootstrap read is synchronous file access and cannot be interrupted, so the
            // budget is enforced after it rather than pretended to bound it.
            return InstallationStatusResult.Unavailable;
        }

        var restartRequired = latch.RestartRequired;
        return IsCoherent(snapshot.Phase, bootstrapConfigured, restartRequired)
            ? InstallationStatusResult.Create(snapshot, bootstrapConfigured, restartRequired)
            : InstallationStatusResult.Unavailable;
    }

    /// <summary>
    /// Reports whether the three independently sampled values describe one installation this
    /// process could actually be in.
    /// </summary>
    /// <remarks>
    /// Before configuration the local Bootstrap file is normally absent; it may already exist only
    /// when this same process wrote it and is waiting for its own restart. Once the phase moved past
    /// configuration a valid local Bootstrap file must exist. Every other combination is a state the
    /// entry refuses to describe rather than report a value one of the two sources contradicts.
    /// </remarks>
    private static bool IsCoherent(
        ServiceStartupPhase phase,
        bool bootstrapConfigured,
        bool restartRequired) => phase switch
    {
        ServiceStartupPhase.BootstrapConfiguration => !bootstrapConfigured || restartRequired,
        ServiceStartupPhase.PendingSetup or ServiceStartupPhase.Completed => bootstrapConfigured,
        _ => false,
    };

    /// <summary>
    /// Owns the cancellation exit: the sources are notified on the token they received before the
    /// linked source is released, and the caller still observes its own cancellation.
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

        return new OperationCanceledException(
            "The installation status observation was cancelled by the caller.",
            cancellationToken);
    }
}
