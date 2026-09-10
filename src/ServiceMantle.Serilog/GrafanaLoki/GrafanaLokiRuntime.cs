using Microsoft.Extensions.Hosting;
using ServiceMantle.Serilog;

namespace ServiceMantle.Serilog.GrafanaLoki;

internal sealed class GrafanaLokiRuntime
{
    private GrafanaLokiRemoteSink? remoteSink;

    internal void Register(GrafanaLokiRemoteSink sink)
    {
        if (Interlocked.CompareExchange(ref remoteSink, sink, null) is not null)
        {
            throw GrafanaLokiConfigurationProvider.Failure(
                "Sink",
                WellKnownGrafanaLokiErrorCodes.SinkCreationFailed);
        }
    }

    internal Task StopAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref remoteSink)?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}

internal sealed class GrafanaLokiLifecycle(
    IServiceProvider serviceProvider,
    GrafanaLokiConfigurationProvider configurationProvider,
    GrafanaLokiRuntime runtime) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = configurationProvider.GetRequiredConfiguration();
        if (!configuration.Enabled)
        {
            return Task.CompletedTask;
        }

        var serilogRuntime = serviceProvider.GetService(typeof(SerilogRuntime))
            as SerilogRuntime;
        if (serilogRuntime is null)
        {
            throw GrafanaLokiConfigurationProvider.Failure(
                "SerilogPipeline",
                WellKnownGrafanaLokiErrorCodes.SerilogPipelineMissing);
        }

        serilogRuntime.EnsureConfigurationIsValid();
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) =>
        runtime.StopAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => runtime.StopAsync(cancellationToken);

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
