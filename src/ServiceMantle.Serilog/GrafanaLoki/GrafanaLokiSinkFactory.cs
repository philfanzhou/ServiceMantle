using System.Diagnostics;
using global::Serilog;
using global::Serilog.Sinks.Grafana.Loki;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using ServiceMantle.Serilog;

namespace ServiceMantle.Serilog.GrafanaLoki;

internal sealed class GrafanaLokiSinkFactory(
    IServiceProvider serviceProvider,
    GrafanaLokiConfigurationProvider configurationProvider,
    GrafanaLokiDiagnostics diagnostics,
    GrafanaLokiRuntime runtime) : ISerilogSinkFactory
{
    public ILogEventSink Create(
        SerilogConfiguration serilogConfiguration,
        ILogFieldSanitizer sanitizer)
    {
        try
        {
            var configuration = configurationProvider.GetRequiredConfiguration();
            if (!configuration.Enabled)
            {
                return new ConsoleSinkFactory().Create(serilogConfiguration, sanitizer);
            }

            var headerValue = ResolveAuthorizationHeader(configuration.AuthorizationHeaderResolverName!);
            var handlerFactory = serviceProvider.GetService(typeof(ILokiHttpMessageHandlerFactory))
                as ILokiHttpMessageHandlerFactory ??
                throw GrafanaLokiConfigurationProvider.Failure(
                    "HttpMessageHandler",
                    WellKnownGrafanaLokiErrorCodes.SinkCreationFailed);
            var deliveryCounter = new GrafanaLokiDeliveryCounter();
            var handler = new LokiHttpMessageHandler(
                handlerFactory.Create(),
                headerValue,
                deliveryCounter);
            var httpClient = new HttpClient(handler, disposeHandler: true);
            var failureListener = new GrafanaLokiFailureListener(diagnostics);
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

            var remoteSink = new GrafanaLokiRemoteSink(
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
            var sanitizedRemote = new SanitizingSink(sanitizer, remotePipeline);
            var console = new ConsoleSinkFactory().Create(serilogConfiguration, sanitizer);
            return new GrafanaLokiCompositeSink(console, sanitizedRemote);
        }
        catch (SerilogConfigurationException)
        {
            throw;
        }
        catch
        {
            throw GrafanaLokiConfigurationProvider.Failure(
                "Sink",
                WellKnownGrafanaLokiErrorCodes.SinkCreationFailed);
        }
    }

    private string ResolveAuthorizationHeader(string resolverName)
    {
        ILokiAuthorizationHeaderResolver[] resolvers;
        try
        {
            resolvers = serviceProvider
                .GetServices<ILokiAuthorizationHeaderResolver>()
                .Take(2)
                .ToArray();
        }
        catch
        {
            throw GrafanaLokiConfigurationProvider.Failure(
                nameof(GrafanaLokiOptions.AuthorizationHeaderResolverName),
                WellKnownGrafanaLokiErrorCodes.AuthorizationResolutionFailed);
        }

        if (resolvers.Length != 1)
        {
            throw GrafanaLokiConfigurationProvider.Failure(
                nameof(GrafanaLokiOptions.AuthorizationHeaderResolverName),
                WellKnownGrafanaLokiErrorCodes.AuthorizationResolverMissing);
        }

        string? value;
        try
        {
            value = resolvers[0].ResolveAuthorizationHeader(resolverName);
        }
        catch
        {
            throw GrafanaLokiConfigurationProvider.Failure(
                nameof(GrafanaLokiOptions.AuthorizationHeaderResolverName),
                WellKnownGrafanaLokiErrorCodes.AuthorizationResolutionFailed);
        }

        if (value is not { Length: >= 1 and <= 4_096 } ||
            string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl))
        {
            throw GrafanaLokiConfigurationProvider.Failure(
                nameof(GrafanaLokiOptions.AuthorizationHeaderResolverName),
                WellKnownGrafanaLokiErrorCodes.AuthorizationValueInvalid);
        }

        return value;
    }
}

internal sealed class GrafanaLokiFailureListener(
    GrafanaLokiDiagnostics diagnostics) : ILoggingFailureListener
{
    public void OnLoggingFailed(
        object sender,
        LoggingFailureKind kind,
        string message,
        IReadOnlyCollection<LogEvent>? events,
        Exception? exception)
    {
        var errorCode = exception is LokiDeliveryException delivery
            ? delivery.ErrorCode
            : exception is HttpRequestException { StatusCode: not null }
                ? WellKnownGrafanaLokiErrorCodes.RemoteResponseFailed
                : WellKnownGrafanaLokiErrorCodes.TransportFailed;
        diagnostics.RecordFailedBatch(errorCode);
    }
}

internal sealed class GrafanaLokiCompositeSink(
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

internal sealed class GrafanaLokiRemoteSink(
    Logger remoteLogger,
    HttpClient httpClient,
    TimeSpan drainTimeout,
    GrafanaLokiDiagnostics diagnostics,
    GrafanaLokiDeliveryCounter deliveryCounter) : ILogEventSink, IDisposable
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
