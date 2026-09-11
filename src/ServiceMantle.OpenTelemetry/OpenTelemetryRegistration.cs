using Microsoft.Extensions.Hosting;

namespace ServiceMantle.OpenTelemetry;

internal sealed record OpenTelemetryRegistration(
    bool Enabled,
    bool EnableAspNetCoreTracing,
    bool EnableHttpClientTracing,
    bool EnableRuntimeMetrics);

internal sealed class OpenTelemetryRegistrationValidator(
    IEnumerable<OpenTelemetryRegistration> registrations) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(registrations);
        return Task.CompletedTask;
    }

    internal static void Validate(IEnumerable<OpenTelemetryRegistration> registrations)
    {
        OpenTelemetryRegistration? baseline = null;
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
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static OpenTelemetryRegistration Normalize(
        OpenTelemetryRegistration registration)
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
