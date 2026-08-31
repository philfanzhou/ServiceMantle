using Microsoft.Extensions.Hosting;

namespace ServiceMantle.OpenTelemetry;

internal sealed record ServiceMantleOpenTelemetryRegistration(
    bool Enabled,
    bool EnableAspNetCoreTracing,
    bool EnableHttpClientTracing,
    bool EnableRuntimeMetrics);

internal sealed class ServiceMantleOpenTelemetryRegistrationValidator(
    IEnumerable<ServiceMantleOpenTelemetryRegistration> registrations) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ServiceMantleOpenTelemetryRegistration? baseline = null;
        foreach (var registration in registrations)
        {
            var normalized = Normalize(registration);
            if (baseline is not null && baseline != normalized)
            {
                throw new InvalidOperationException(
                    "OpenTelemetry instrumentation was registered with conflicting settings.");
            }

            baseline = normalized;
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static ServiceMantleOpenTelemetryRegistration Normalize(
        ServiceMantleOpenTelemetryRegistration registration)
    {
        if (!registration.Enabled)
        {
            return new(false, false, false, false);
        }

        if (!registration.EnableAspNetCoreTracing &&
            !registration.EnableHttpClientTracing &&
            !registration.EnableRuntimeMetrics)
        {
            throw new InvalidOperationException(
                "Enabled OpenTelemetry instrumentation must include at least one instrumentation.");
        }

        return registration;
    }
}
