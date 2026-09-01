using System.Diagnostics;
using global::Serilog;
using global::Serilog.Sinks.Grafana.Loki;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using ServiceMantle.Serilog;

namespace ServiceMantle.Serilog.GrafanaLoki;

internal sealed class ServiceMantleGrafanaLokiSinkFactory(
    IServiceProvider serviceProvider,
    ServiceMantleGrafanaLokiConfigurationProvider configurationProvider,
    ServiceMantleGrafanaLokiDiagnostics diagnostics,
    ServiceMantleGrafanaLokiRuntime runtime) : IServiceMantleSerilogSinkFactory
{
    public ILogEventSink Create(
        ServiceMantleSerilogConfiguration serilogConfiguration,
        IServiceMantleStructuredLogSanitizer sanitizer)
    {
        try
        {
            var configuration = configurationProvider.GetRequiredConfiguration();
            if (!configuration.Enabled)
            {
                return new ServiceMantleConsoleSinkFactory().Create(serilogConfiguration, sanitizer);
            }

            var headerValue = ResolveAuthorizationHeader(configuration.AuthorizationHeaderResolverName!);
            var handlerFactory = serviceProvider.GetService(typeof(IServiceMantleLokiHttpMessageHandlerFactory))
                as IServiceMantleLokiHttpMessageHandlerFactory ??
                throw ServiceMantleGrafanaLokiConfigurationProvider.Failure(
                    "HttpMessageHandler",
                    WellKnownServiceMantleGrafanaLokiErrorCodes.SinkCreationFailed);
            var deliveryCounter = new ServiceMantleGrafanaLokiDeliveryCounter();
            var handler = new ServiceMantleLokiHttpMessageHandler(
                handlerFactory.Create(),
                headerValue,
                deliveryCounter);
            var httpClient = new HttpClient(handler, disposeHandler: true);
            var failureListener = new ServiceMantleGrafanaLokiFailureListener(diagnostics);
            Logger remoteLogger;
            try
            {
                remoteLogger = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.Fallible(
                        sink => sink.GrafanaLoki(
                            configuration.Endpoint!.AbsoluteUri,
                            labels: null,
                            propertiesAsLabels: null,
                            propertiesAsStructuredMetadata: null,
                            handleLogLevelAsLabel: true,
                            credentials: null,
                            tenant: null,
                            traceIdMode: global::Serilog.Sinks.Grafana.Loki.LokiFieldDestination.None,
                            spanIdMode: global::Serilog.Sinks.Grafana.Loki.LokiFieldDestination.None,
                            batchSizeLimit: configuration.BatchSize,
                            queueLimit: configuration.QueueLimit,
                            period: configuration.FlushPeriod,
                            eagerlyEmitFirstEvent: false,
                            retryTimeLimit: configuration.ShutdownDrainTimeout,
                            textFormatter: null,
                            exceptionFormatter: null,
                            httpClient: httpClient,
                            httpMessageHandler: null,
                            restrictedToMinimumLevel: LogEventLevel.Verbose),
                        failureListener)
                    .CreateLogger();
            }
            catch
            {
                httpClient.Dispose();
                throw;
            }

            var remoteSink = new ServiceMantleGrafanaLokiRemoteSink(
                remoteLogger,
                httpClient,
                configuration.ShutdownDrainTimeout,
                diagnostics,
                deliveryCounter);
            runtime.Register(remoteSink);
            var remotePipeline = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(remoteSink)
                .CreateLogger();
            var sanitizedRemote = new ServiceMantleSanitizingSink(sanitizer, remotePipeline);
            var console = new ServiceMantleConsoleSinkFactory().Create(serilogConfiguration, sanitizer);
            return new ServiceMantleGrafanaLokiCompositeSink(console, sanitizedRemote);
        }
        catch (ServiceMantleSerilogConfigurationException)
        {
            throw;
        }
        catch
        {
            throw ServiceMantleGrafanaLokiConfigurationProvider.Failure(
                "Sink",
                WellKnownServiceMantleGrafanaLokiErrorCodes.SinkCreationFailed);
        }
    }

    private string ResolveAuthorizationHeader(string resolverName)
    {
        IServiceMantleLokiAuthorizationHeaderResolver[] resolvers;
        try
        {
            resolvers = serviceProvider
                .GetServices<IServiceMantleLokiAuthorizationHeaderResolver>()
                .Take(2)
                .ToArray();
        }
        catch
        {
            throw ServiceMantleGrafanaLokiConfigurationProvider.Failure(
                nameof(ServiceMantleGrafanaLokiOptions.AuthorizationHeaderResolverName),
                WellKnownServiceMantleGrafanaLokiErrorCodes.AuthorizationResolutionFailed);
        }

        if (resolvers.Length != 1)
        {
            throw ServiceMantleGrafanaLokiConfigurationProvider.Failure(
                nameof(ServiceMantleGrafanaLokiOptions.AuthorizationHeaderResolverName),
                WellKnownServiceMantleGrafanaLokiErrorCodes.AuthorizationResolverMissing);
        }

        string? value;
        try
        {
            value = resolvers[0].ResolveAuthorizationHeader(resolverName);
        }
        catch
        {
            throw ServiceMantleGrafanaLokiConfigurationProvider.Failure(
                nameof(ServiceMantleGrafanaLokiOptions.AuthorizationHeaderResolverName),
                WellKnownServiceMantleGrafanaLokiErrorCodes.AuthorizationResolutionFailed);
        }

        if (value is not { Length: >= 1 and <= 4_096 } ||
            string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl))
        {
            throw ServiceMantleGrafanaLokiConfigurationProvider.Failure(
                nameof(ServiceMantleGrafanaLokiOptions.AuthorizationHeaderResolverName),
                WellKnownServiceMantleGrafanaLokiErrorCodes.AuthorizationValueInvalid);
        }

        return value;
    }
}

internal sealed class ServiceMantleGrafanaLokiFailureListener(
    ServiceMantleGrafanaLokiDiagnostics diagnostics) : ILoggingFailureListener
{
    public void OnLoggingFailed(
        object sender,
        LoggingFailureKind kind,
        string message,
        IReadOnlyCollection<LogEvent>? events,
        Exception? exception)
    {
        var errorCode = exception is ServiceMantleLokiDeliveryException delivery
            ? delivery.ErrorCode
            : exception is HttpRequestException { StatusCode: not null }
                ? WellKnownServiceMantleGrafanaLokiErrorCodes.RemoteResponseFailed
                : WellKnownServiceMantleGrafanaLokiErrorCodes.TransportFailed;
        diagnostics.RecordFailedBatch(errorCode);
    }
}

internal sealed class ServiceMantleGrafanaLokiCompositeSink(
    ILogEventSink console,
    ILogEventSink remote) : ILogEventSink, IDisposable
{
    private int disposed;

    public void Emit(LogEvent logEvent)
    {
        console.Emit(logEvent);
        remote.Emit(logEvent);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        (remote as IDisposable)?.Dispose();
        (console as IDisposable)?.Dispose();
    }
}

internal sealed class ServiceMantleGrafanaLokiRemoteSink(
    Logger remoteLogger,
    HttpClient httpClient,
    TimeSpan drainTimeout,
    ServiceMantleGrafanaLokiDiagnostics diagnostics,
    ServiceMantleGrafanaLokiDeliveryCounter deliveryCounter) : ILogEventSink, IDisposable
{
    private readonly object disposeSync = new();
    private readonly object stopSync = new();
    private readonly CancellationTokenSource stopCancellation = new();
    private Task? disposeTask;
    private Task? stopTask;
    private int stopping;

    public void Emit(LogEvent logEvent)
    {
        if (Volatile.Read(ref stopping) == 0)
        {
            deliveryCounter.RecordAccepted();
            remoteLogger.Write(logEvent);
        }
    }

    internal Task StopAsync(CancellationToken cancellationToken)
    {
        var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(
                static state => ((CancellationTokenSource)state!).Cancel(),
                stopCancellation)
            : default;
        Task task;
        lock (stopSync)
        {
            task = stopTask ??= StopCoreAsync(stopCancellation.Token);
        }

        if (cancellationToken.CanBeCanceled)
        {
            _ = DisposeRegistrationAfterStopAsync(task, registration);
        }

        return task;
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref stopping, 1);
        var task = StartDispose();
        var stopwatch = Stopwatch.StartNew();
        var cancellationDelay = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var cancellationMargin = TimeSpan.FromMilliseconds(
            Math.Min(250, Math.Max(1, drainTimeout.TotalMilliseconds / 4)));
        var gracefulDuration = drainTimeout - cancellationMargin;
        var gracefulDelay = Task.Delay(gracefulDuration);
        var first = await Task.WhenAny(task, gracefulDelay, cancellationDelay).ConfigureAwait(false);
        if (first == task)
        {
            await ObserveAsync(task).ConfigureAwait(false);
            httpClient.Dispose();
            RecordDroppedEvents();
            return;
        }

        if (first == cancellationDelay)
        {
            diagnostics.RecordDrainCancellation();
        }
        else
        {
            diagnostics.RecordDrainTimeout();
        }

        httpClient.CancelPendingRequests();
        httpClient.Dispose();
        if (cancellationToken.IsCancellationRequested)
        {
            RecordDroppedEvents();
            return;
        }

        var remaining = drainTimeout - stopwatch.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.WhenAny(task, Task.Delay(remaining)).ConfigureAwait(false);
        }

        if (task.IsCompleted)
        {
            await ObserveAsync(task).ConfigureAwait(false);
        }

        RecordDroppedEvents();
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    private Task StartDispose()
    {
        lock (disposeSync)
        {
            return disposeTask ??= Task.Run(remoteLogger.Dispose);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Serilog disposal failures are represented only by the bounded diagnostics above.
        }
    }

    private static async Task DisposeRegistrationAfterStopAsync(
        Task task,
        CancellationTokenRegistration registration)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RecordDroppedEvents() =>
        diagnostics.RecordDroppedEvents(deliveryCounter.NotAcknowledgedCount);
}
