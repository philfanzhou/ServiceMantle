using Microsoft.Extensions.Hosting;
using ServiceMantle.Serilog;

namespace ServiceMantle.Serilog.GrafanaLoki;

internal sealed class ServiceMantleGrafanaLokiRuntime
{
    private ServiceMantleGrafanaLokiRemoteSink? remoteSink;

    internal void Register(ServiceMantleGrafanaLokiRemoteSink sink)
    {
        if (Interlocked.CompareExchange(ref remoteSink, sink, null) is not null)
        {
            throw ServiceMantleGrafanaLokiConfigurationProvider.Failure(
                "Sink",
                WellKnownServiceMantleGrafanaLokiErrorCodes.SinkCreationFailed);
        }
    }

    internal Task StopAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref remoteSink)?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}

internal sealed class ServiceMantleGrafanaLokiLifecycle(
    IServiceProvider serviceProvider,
    ServiceMantleGrafanaLokiConfigurationProvider configurationProvider,
    ServiceMantleGrafanaLokiRuntime runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = configurationProvider.GetRequiredConfiguration();
        if (!configuration.Enabled)
        {
            return Task.CompletedTask;
        }

        var serilogRuntime = serviceProvider.GetService(typeof(ServiceMantleSerilogRuntime))
            as ServiceMantleSerilogRuntime;
        if (serilogRuntime is null)
        {
            throw ServiceMantleGrafanaLokiConfigurationProvider.Failure(
                "SerilogPipeline",
                WellKnownServiceMantleGrafanaLokiErrorCodes.SerilogPipelineMissing);
        }

        serilogRuntime.EnsureConfigurationIsValid();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => runtime.StopAsync(cancellationToken);
}
