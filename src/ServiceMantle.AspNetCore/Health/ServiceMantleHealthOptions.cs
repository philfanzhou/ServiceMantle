using Microsoft.Extensions.Hosting;

namespace ServiceMantle.AspNetCore.Health;

/// <summary>Well-known safe health probe error codes.</summary>
public static class WellKnownServiceHealthErrorCodes
{
    /// <summary>The snapshot source exceeded the configured internal timeout.</summary>
    public const string ProbeTimeout = "health.probe_timeout";

    /// <summary>The snapshot source was missing, failed, or returned no snapshot.</summary>
    public const string ProbeFailed = "health.probe_failed";
}

/// <summary>Configures bounded health snapshot reads.</summary>
public sealed class ServiceMantleHealthOptions
{
    /// <summary>The default maximum duration of a snapshot read.</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The minimum permitted snapshot timeout.</summary>
    public static readonly TimeSpan MinimumProbeTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The maximum permitted snapshot timeout.</summary>
    public static readonly TimeSpan MaximumProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the maximum duration of one snapshot read.</summary>
    public TimeSpan ProbeTimeout { get; set; } = DefaultProbeTimeout;
}

internal sealed record ServiceMantleHealthRegistration(TimeSpan ProbeTimeout);

internal sealed class ServiceMantleHealthStartupValidator(
    IEnumerable<ServiceMantleHealthRegistration> registrations) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = registrations.ToArray();
        if (configured.Length == 0)
        {
            throw new InvalidOperationException(
                "ServiceMantle health endpoints have no registration.");
        }

        if (configured.Any(candidate => candidate != configured[0]))
        {
            throw new InvalidOperationException(
                "Conflicting ServiceMantle health endpoint settings are registered.");
        }

        var timeout = configured[0].ProbeTimeout;
        if (timeout < ServiceMantleHealthOptions.MinimumProbeTimeout ||
            timeout > ServiceMantleHealthOptions.MaximumProbeTimeout)
        {
            throw new InvalidOperationException(
                "The ServiceMantle health probe timeout is outside the permitted range.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
