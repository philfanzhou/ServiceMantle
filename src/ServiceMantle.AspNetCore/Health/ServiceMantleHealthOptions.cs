using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Health;

namespace ServiceMantle.AspNetCore.Health;

/// <summary>Well-known safe health probe error codes.</summary>
public static class WellKnownServiceHealthErrorCodes
{
    /// <summary>The snapshot source exceeded the configured internal timeout.</summary>
    public const string ProbeTimeout = "health.probe_timeout";

    /// <summary>The snapshot source was missing, failed, or returned no snapshot.</summary>
    public const string ProbeFailed = "health.probe_failed";

    /// <summary>A readiness contributor failed or returned an invalid result.</summary>
    public const string ContributorFailed =
        WellKnownServiceReadinessContributorErrorCodes.ContributorFailed;

    /// <summary>The shared total readiness contributor budget was exhausted.</summary>
    public const string ContributorTimeout =
        WellKnownServiceReadinessContributorErrorCodes.ContributorTimeout;
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

    /// <summary>The default total budget shared by all readiness contributors.</summary>
    public static readonly TimeSpan DefaultContributorTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The minimum permitted total contributor budget.</summary>
    public static readonly TimeSpan MinimumContributorTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The maximum permitted total contributor budget.</summary>
    public static readonly TimeSpan MaximumContributorTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the maximum duration of one snapshot read.</summary>
    public TimeSpan ProbeTimeout { get; set; } = DefaultProbeTimeout;

    /// <summary>Gets or sets the total budget shared by all readiness contributors in one request.</summary>
    public TimeSpan ContributorTimeout { get; set; } = DefaultContributorTimeout;
}

internal sealed record ServiceMantleHealthRegistration(
    TimeSpan ProbeTimeout,
    TimeSpan ContributorTimeout);

internal sealed class ServiceMantleHealthStartupValidator(
    IEnumerable<ServiceMantleHealthRegistration> registrations,
    IServiceProvider serviceProvider) : IHostedService
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

        var contributorTimeout = configured[0].ContributorTimeout;
        if (contributorTimeout < ServiceMantleHealthOptions.MinimumContributorTimeout ||
            contributorTimeout > ServiceMantleHealthOptions.MaximumContributorTimeout)
        {
            throw new InvalidOperationException(
                "The ServiceMantle readiness contributor timeout is outside the permitted range.");
        }

        try
        {
            serviceProvider.GetRequiredService<ServiceReadinessContributorCombiner>();
        }
        catch
        {
            throw new InvalidOperationException(
                "ServiceMantle readiness contributor registration is invalid.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
