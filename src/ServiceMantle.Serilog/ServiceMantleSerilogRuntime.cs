using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using global::Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;

namespace ServiceMantle.Serilog;

internal sealed class ServiceMantleSerilogMarker;

internal sealed class ServiceMantleSerilogRuntime : IDisposable
{
    private readonly Logger logger;
    private readonly TimeSpan flushTimeout;
    private readonly ServiceMantleSerilogConfigurationException? configurationFailure;
    private int flushStarted;

    public ServiceMantleSerilogRuntime(
        IEnumerable<ServiceMantleSerilogRegistration> registrations,
        IServiceMantleStructuredLogSanitizer sanitizer,
        IServiceMantleSerilogSinkFactory sinkFactory)
    {
        try
        {
            var configuration = ServiceMantleSerilogConfiguration.Resolve(registrations);
            var sink = sinkFactory.Create(configuration, sanitizer);
            var loggerConfiguration = new LoggerConfiguration()
                .MinimumLevel.Is(configuration.MinimumLevel);
            if (configuration.EnricherNames.Contains("FromLogContext", StringComparer.Ordinal))
            {
                loggerConfiguration.Enrich.FromLogContext();
            }

            logger = loggerConfiguration.WriteTo.Sink(sink).CreateLogger();
            flushTimeout = configuration.FlushTimeout;
        }
        catch (ServiceMantleSerilogConfigurationException exception)
        {
            configurationFailure = exception;
            logger = new LoggerConfiguration().CreateLogger();
            flushTimeout = ServiceMantleSerilogDefaults.FlushTimeout;
        }
    }

    internal global::Serilog.ILogger Logger => logger;

    internal int FlushInvocationCount => Volatile.Read(ref flushStarted);

    internal void EnsureConfigurationIsValid()
    {
        if (configurationFailure is not null)
        {
            throw configurationFailure;
        }
    }

    internal void FlushOnce()
    {
        if (Interlocked.Exchange(ref flushStarted, 1) != 0)
        {
            return;
        }

        try
        {
            _ = Task.Run(DisposePipeline).Wait(flushTimeout);
        }
        catch
        {
            // Flushing is best effort and must never replace shutdown or unhandled-exception flow.
        }
    }

    public void Dispose() => FlushOnce();

    private void DisposePipeline()
    {
        try
        {
            logger.Dispose();
        }
        catch
        {
            // Sink disposal is never allowed to escape the bounded flush boundary.
        }
    }
}

internal sealed class ServiceMantleSerilogLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly SerilogLoggerProvider provider;

    public ServiceMantleSerilogLoggerProvider(ServiceMantleSerilogRuntime runtime)
    {
        provider = new SerilogLoggerProvider(runtime.Logger, dispose: false);
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
        provider.CreateLogger(categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        provider.SetScopeProvider(scopeProvider);

    public void Dispose() => provider.Dispose();
}

internal sealed class ServiceMantleSerilogLifecycle(
    ServiceMantleSerilogRuntime runtime,
    IHostApplicationLifetime lifetime) : IHostedService, IDisposable
{
    private CancellationTokenRegistration stoppedRegistration;
    private int subscribed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtime.EnsureConfigurationIsValid();
        if (Interlocked.Exchange(ref subscribed, 1) == 0)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            stoppedRegistration = lifetime.ApplicationStopped.Register(runtime.FlushOnce);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        runtime.FlushOnce();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Unsubscribe();
        runtime.FlushOnce();
    }

    internal void HandleUnhandledExceptionForTests() => runtime.FlushOnce();

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args) =>
        runtime.FlushOnce();

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref subscribed, 0) != 0)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            stoppedRegistration.Dispose();
        }
    }
}
