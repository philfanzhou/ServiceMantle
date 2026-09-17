using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Diagnostics;
using ServiceMantle.Health;
using ServiceMantle.ReferenceService.Health.PostgreSql;

namespace ServiceMantle.ReferenceService.Telemetry;

/// <summary>
/// Publishes every authoritative phase observation of the inner source as the ServiceMantle
/// installation phase metric, without changing what the caller observes.
/// </summary>
/// <remarks>
/// <para>
/// The decorator is transparent: it returns the same snapshot and rethrows the same exception
/// instance the inner source produced, so the phase gate and the health endpoints keep their exact
/// behavior. Publishing is a side effect only.
/// </para>
/// <para>
/// A successful observation publishes the observed phase; any non-caller-cancellation failure
/// publishes <c>unknown</c>, so a previously observed <c>completed</c> is never left standing after
/// the phase could not be observed. A caller cancellation publishes nothing and leaves the last
/// observation in place. A publisher already disposed by the host is ignored, because the observation
/// itself still has to reach its caller.
/// </para>
/// <para>
/// The metric is the <b>last</b> observation: there is no polling, no caching, and no background
/// refresh. Without a read there is no new value.
/// </para>
/// </remarks>
public sealed class ReferencePhaseMetricsSnapshotSource(
    ReferencePostgreSqlHealthSnapshotSource inner,
    ServiceMetrics metrics) : IServiceHealthSnapshotSource
{
    /// <inheritdoc />
    public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        ServiceHealthSnapshot snapshot;
        try
        {
            snapshot = await inner.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Publish(static publisher => publisher.SetUnknown());
            throw;
        }

        Publish(publisher => publisher.SetPhase(snapshot.Phase));
        return snapshot;
    }

    private void Publish(Action<ServiceMetrics> publish)
    {
        try
        {
            publish(metrics);
        }
        catch (ObjectDisposedException)
        {
            // The host is disposing the publisher while a late read is still in flight; the
            // observation itself still has to reach its caller.
        }
    }
}
