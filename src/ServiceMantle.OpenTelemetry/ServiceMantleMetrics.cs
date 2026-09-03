using System.Diagnostics.Metrics;
using ServiceMantle.Installation;

namespace ServiceMantle.OpenTelemetry;

/// <summary>Publishes fixed service information and the last explicitly observed installation phase.</summary>
/// <remarks>
/// No user tags or arbitrary strings are accepted. The consumer publishes phases after observing
/// durable state; collection performs no I/O. Unknown is the initial state. Host identity resource
/// values must be non-secret deployment metadata. Instances are thread-safe and owned by the host.
/// </remarks>
public sealed class ServiceMantleMetrics : IDisposable
{
    /// <summary>Gets the stable meter name.</summary>
    public const string MeterName = "ServiceMantle";
    /// <summary>Gets the metric contract version.</summary>
    public const string MeterVersion = "1.0.0";
    /// <summary>Gets the dimensionless service information gauge name.</summary>
    public const string ServiceInfoName = "servicemantle.service.info";
    /// <summary>Gets the dimensionless one-hot installation phase gauge name.</summary>
    public const string InstallationPhaseName = "servicemantle.installation.phase";

    private static readonly string[] PhaseNames = ["unknown", "bootstrap_configuration", "pending_setup", "completed"];
    private readonly object gate = new();
    private int phaseIndex;
    private bool disposed;

    internal ServiceMantleMetrics()
    {
        Meter = new Meter(MeterName, MeterVersion);
        Meter.CreateObservableGauge(ServiceInfoName, ObserveInfo, unit: "1",
            description: "One for this host; identity is carried by its resource.");
        Meter.CreateObservableGauge(InstallationPhaseName, ObservePhase, unit: "1",
            description: "Last observed installation phase; exactly one phase has value one.");
    }

    internal Meter Meter { get; }

    /// <summary>Publishes a finite, consumer-observed phase without accessing persistent state.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The phase is not defined.</exception>
    /// <exception cref="ObjectDisposedException">The host has disposed this publisher.</exception>
    public void SetPhase(ServiceStartupPhase phase)
    {
        var index = phase switch
        {
            ServiceStartupPhase.BootstrapConfiguration => 1,
            ServiceStartupPhase.PendingSetup => 2,
            ServiceStartupPhase.Completed => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), "The installation phase is invalid.")
        };
        SetIndex(index);
    }

    /// <summary>Clears the last observation when the consumer can no longer determine the phase.</summary>
    public void SetUnknown() => SetIndex(0);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        Meter.Dispose();
    }

    private void SetIndex(int index)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            phaseIndex = index;
        }
    }

    private IEnumerable<Measurement<long>> ObserveInfo()
    {
        lock (gate)
        {
            return disposed ? [] : [new Measurement<long>(1)];
        }
    }

    private IEnumerable<Measurement<long>> ObservePhase()
    {
        lock (gate)
        {
            if (disposed) return [];
            var measurements = new Measurement<long>[PhaseNames.Length];
            for (var index = 0; index < PhaseNames.Length; index++)
            {
                measurements[index] = new Measurement<long>(index == phaseIndex ? 1 : 0,
                    new KeyValuePair<string, object?>("phase", PhaseNames[index]));
            }
            return measurements;
        }
    }
}
