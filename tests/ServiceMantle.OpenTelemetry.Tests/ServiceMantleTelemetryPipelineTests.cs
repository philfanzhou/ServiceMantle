using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Tests;

// AssemblyInfo.cs serializes this assembly's process-wide Activity/Meter observations.
public sealed class ServiceMantleTelemetryPipelineTests
{
    private const string Secret = "telemetry-unrelated-config-secret";
    private const string Correlation = "telemetry-request-correlation";
    private const string CounterName = "servicemantle.composition.test";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("absent", 0)]
    [InlineData("disabled", 0)]
    [InlineData("disabled", 7)]
    [InlineData("enabled", 1)]
    [InlineData("enabled", 2)]
    [InlineData("enabled", 3)]
    [InlineData("enabled", 4)]
    [InlineData("enabled", 5)]
    [InlineData("enabled", 6)]
    [InlineData("enabled", 7)]
    public async Task HTTP_matrix_activates_only_selected_signals_and_releases_observed_listeners(string mode, int selection)
    {
        await using var host = new TelemetryHost(mode, selection);
        await host.StartAsync();
        await AssertSuccessfulSelectionAsync(host, mode == "enabled" ? selection : 0);
        await host.StopAndDisposeAsync();
        AssertDetached(host);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("disabled")]
    public async Task Equivalent_duplicates_emit_each_request_once_and_dispose_each_instrumentation_once(string mode)
    {
        // Disabled selections normalize to the same effective configuration.
        await using var host = new TelemetryHost(mode, 7, secondMode: mode, secondSelection: mode == "disabled" ? 0 : 7);
        await host.StartAsync();
        await AssertSuccessfulSelectionAsync(host, mode == "enabled" ? 7 : 0);
        await host.StopAndDisposeAsync();
        await host.App.DisposeAsync();
        AssertDetached(host);
        Assert.Equal(mode == "enabled" ? 1 : 0, host.TraceLifetime.Disposed);
        Assert.Equal(mode == "enabled" ? 1 : 0, host.MetricLifetime.Disposed);
        Assert.Equal(mode == "enabled" ? 1 : 0, host.Spans.Events.Count(item => item.Kind == ActivityKind.Server));
        Assert.Equal(mode == "enabled" ? 1 : 0, host.Spans.Events.Count(item => item.Kind == ActivityKind.Client));
    }

    [Theory]
    [InlineData("enabled", 0, null, 0, "at least one")]
    [InlineData("enabled", 7, "enabled", 3, "conflicting")]
    [InlineData("enabled", 1, "enabled", 2, "conflicting")]
    [InlineData("enabled", 7, "disabled", 7, "conflicting")]
    [InlineData("disabled", 7, "enabled", 7, "conflicting")]
    public async Task Invalid_selection_or_conflict_fails_before_provider_factories_run(string mode, int selection,
        string? secondMode, int secondSelection, string message)
    {
        await using var host = new TelemetryHost(mode, selection, secondMode, secondSelection);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.False(host.App.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, host.TraceLifetime.Created);
        Assert.Equal(0, host.MetricLifetime.Created);
        Assert.False(host.IncomingSource.HasListeners());
        Assert.False(host.OutgoingSource.HasListeners());
        Assert.False(host.RuntimeCounter.Enabled);
        Assert.Empty(host.Spans.Events);
        Assert.Equal(0, host.Metrics.ExportCalls);
        AssertSafeLogs(host);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("disabled")]
    [InlineData("enabled")]
    public async Task Exceptions_gate_rejection_and_disconnect_keep_the_existing_HTTP_contract(string mode)
    {
        await using var host = new TelemetryHost(mode, 7);
        await host.StartAsync();
        using (var ok = await host.SendAsync("/ok"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            AssertHeaders(ok);
        }
        using (var error = await host.SendAsync("/exception"))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
            AssertHeaders(error);
            Assert.Equal("application/problem+json", error.Content.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(await error.Content.ReadAsStringAsync(Token));
            Assert.Equal("http.internal_server_error", json.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(Correlation, json.RootElement.GetProperty("correlationId").GetString());
        }
        host.Snapshot.Ready = false;
        using (var gated = await host.SendAsync("/ok"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, gated.StatusCode);
            AssertHeaders(gated);
            Assert.Equal("application/json", gated.Content.Headers.ContentType?.MediaType);
            Assert.Equal("{\"errorCode\":\"service.phase.unavailable\"}", await gated.Content.ReadAsStringAsync(Token));
            Assert.Equal(1, host.EndpointCalls);
        }
        host.Snapshot.Ready = true;
        var errorsBeforeCancellation = host.Logs.ProblemErrors;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var request = host.Client.GetAsync("/cancel", cancellation.Token);
        await host.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        cancellation.Cancel();
        // Explicit fixture binding avoids depending on TCP half-close notification timing.
        host.RequestAbort.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.True(await host.RequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(errorsBeforeCancellation, host.Logs.ProblemErrors);
        await host.StopAndDisposeAsync();
        AssertSafeLogs(host);
        AssertDetached(host);
    }

    [Fact]
    public async Task Precancelled_HTTP_host_does_not_activate_instrumentation_or_report_started()
    {
        await using var host = new TelemetryHost("enabled", 7);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.App.StartAsync(cancelled.Token));
        Assert.False(host.App.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, host.TraceLifetime.Created);
        Assert.Equal(0, host.MetricLifetime.Created);
    }

    [Fact]
    public async Task Instrumentation_disposal_failure_remains_visible_and_fixture_cleans_its_resources()
    {
        var host = new TelemetryHost("enabled", 7, throwOnDispose: true);
        try
        {
            await host.StartAsync();
            await AssertSuccessfulSelectionAsync(host, 7);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => host.StopAndDisposeAsync());
            Assert.Contains("controlled instrumentation disposal failure", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, host.TraceLifetime.Disposed);
        }
        finally
        {
            // A failed SDK Dispose need not visit every later resource. These are fixture-owned handles.
            host.Tracer?.Dispose();
            host.Meter?.Dispose();
            host.Reader?.Dispose();
            host.Spans.Dispose();
            await host.DisposeAsync();
        }
        Assert.False(host.IncomingSource.HasListeners());
        Assert.False(host.OutgoingSource.HasListeners());
        Assert.False(host.RuntimeCounter.Enabled);
        Assert.True(host.TraceLifetime.Disposed >= 1);
        Assert.Equal(1, host.MetricLifetime.Disposed);
    }

    [Fact]
    public async Task Base_instrumentation_runs_HTTP_without_a_test_reader_exporter_or_destination()
    {
        await using var host = new TelemetryHost("enabled", 7, collectors: false);
        await host.StartAsync();
        Assert.NotNull(host.Tracer);
        Assert.NotNull(host.Meter);
        Assert.Null(host.Reader);
        using var response = await host.SendAsync("/ok");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertHeaders(response);
        await host.StopAndDisposeAsync();
        Assert.Empty(host.Spans.Events);
        Assert.Equal(0, host.Metrics.ExportCalls);
        AssertDetached(host);
    }

    [Fact]
    public void Core_and_AspNetCore_restored_graphs_have_no_transitive_telemetry_dependency()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "eng", "packages.json"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var project in new[] { "ServiceMantle", "ServiceMantle.AspNetCore" })
        {
            using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "artifacts", "obj", project, "project.assets.json")));
            Assert.DoesNotContain(assets.RootElement.GetProperty("libraries").EnumerateObject(),
                library => library.Name.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase) ||
                    library.Name.Contains("Prometheus", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static async Task AssertSuccessfulSelectionAsync(TelemetryHost host, int selection)
    {
        var incoming = (selection & 1) != 0;
        var outgoing = (selection & 2) != 0;
        var runtime = (selection & 4) != 0;
        Assert.Equal(incoming || outgoing ? 1 : 0, host.App.Services.GetServices<TracerProvider>().Count());
        Assert.Equal(runtime ? 1 : 0, host.App.Services.GetServices<MeterProvider>().Count());
        Assert.Equal(incoming, host.IncomingSource.HasListeners());
        Assert.Equal(outgoing, host.OutgoingSource.HasListeners());
        Assert.Equal(runtime, host.RuntimeCounter.Enabled);
        Assert.Equal(incoming || outgoing ? 1 : 0, host.TraceLifetime.Created);
        Assert.Equal(runtime ? 1 : 0, host.MetricLifetime.Created);
        if (host.Tracer is not null) AssertResource(host, host.Tracer);
        if (host.Meter is not null) AssertResource(host, host.Meter);
        using var response = await host.SendAsync("/ok");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertHeaders(response);
        if (incoming) await host.Spans.Incoming.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (outgoing) await host.Spans.Outgoing.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(incoming ? 1 : 0, host.Spans.Events.Count(item => item.Kind == ActivityKind.Server));
        Assert.Equal(outgoing ? 1 : 0, host.Spans.Events.Count(item => item.Kind == ActivityKind.Client));
        Assert.All(host.Spans.Events, span =>
        {
            Assert.Equal(32, span.TraceId.Length);
            Assert.NotEqual(Correlation, span.TraceId);
            if (span.Kind == ActivityKind.Server) Assert.True(span.IsTestEndpoint);
            else Assert.Equal(new Uri(host.Client.BaseAddress!, "/ok").ToString(), span.Url);
        });
        host.RuntimeCounter.Add(7);
        if (runtime)
        {
            Assert.True(host.Reader!.Collect());
            Assert.True(host.Metrics.ExportCalls > 0);
            Assert.Equal(7, host.Metrics.TestCounterValue);
        }
        else Assert.Equal(0, host.Metrics.ExportCalls);
        AssertSafeLogs(host);
    }

    private static void AssertResource(TelemetryHost host, BaseProvider provider)
    {
        var identity = host.App.Services.GetRequiredService<ServiceLogContext>();
        Assert.Equal(new Dictionary<string, object>
        {
            ["service.name"] = identity.ServiceName,
            ["service.version"] = identity.ServiceVersion,
            ["service.instance.id"] = identity.InstanceId
        }, provider.GetResource().Attributes.ToDictionary(item => item.Key, item => item.Value));
    }

    private static void AssertDetached(TelemetryHost host)
    {
        Assert.False(host.IncomingSource.HasListeners());
        Assert.False(host.OutgoingSource.HasListeners());
        Assert.False(host.RuntimeCounter.Enabled);
        Assert.Equal(host.TraceLifetime.Created, host.TraceLifetime.Disposed);
        Assert.Equal(host.MetricLifetime.Created, host.MetricLifetime.Disposed);
        var exports = host.Metrics.ExportCalls;
        host.RuntimeCounter.Add(1);
        Assert.Equal(exports, host.Metrics.ExportCalls);
    }

    private static void AssertHeaders(HttpResponseMessage response)
    {
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["x-correlation-id"] = Correlation,
            ["Cache-Control"] = "no-store",
            ["Pragma"] = "no-cache",
            ["X-Content-Type-Options"] = "nosniff",
            ["X-Frame-Options"] = "DENY",
            ["Referrer-Policy"] = "no-referrer",
            ["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
        }) Assert.Equal(value, Assert.Single(response.Headers.GetValues(name)));
    }

    private static void AssertSafeLogs(TelemetryHost host) =>
        Assert.All(host.Logs.Messages, message => Assert.DoesNotContain(Secret, message, StringComparison.Ordinal));

    private sealed class TelemetryHost : IAsyncDisposable
    {
        internal WebApplication App { get; }
        internal HttpClient Client { get; } = new();
        internal SnapshotSource Snapshot { get; } = new();
        internal ActivitySource IncomingSource { get; } = new("Microsoft.AspNetCore");
        internal ActivitySource OutgoingSource { get; } = new("System.Net.Http");
        private readonly Meter runtimeMeter = new("System.Runtime");
        internal Counter<long> RuntimeCounter { get; }
        internal SpanCollector Spans { get; } = new();
        internal MetricCollector Metrics { get; } = new();
        internal RecordingLogs Logs { get; } = new();
        internal Lifetime TraceLifetime { get; }
        internal Lifetime MetricLifetime { get; } = new();
        internal BaseExportingMetricReader? Reader { get; }
        internal TracerProvider? Tracer { get; private set; }
        internal MeterProvider? Meter { get; private set; }
        internal TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> RequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int EndpointCalls;
        internal CancellationTokenSource RequestAbort { get; } = new();
        private bool stopped;
        private bool disposed;

        internal TelemetryHost(string mode, int selection, string? secondMode = null, int secondSelection = 0,
            bool throwOnDispose = false, bool collectors = true)
        {
            RuntimeCounter = runtimeMeter.CreateCounter<long>(CounterName);
            TraceLifetime = new Lifetime(throwOnDispose);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(Logs);
            builder.Configuration["Unrelated:Password"] = Secret;
            var mantle = builder.Services.AddServiceMantle(ServiceId.Parse("telemetry-composition"),
                    InstanceId.Parse("telemetry-01"), serviceVersion: "2.3.4")
                .AddSecurityResponseHeaders().AddSensitiveHeaders().AddRateLimiting().AddServiceMantlePhaseGate();
            builder.Services.AddSingleton<IServiceHealthSnapshotSource>(Snapshot);
            void Register(string registrationMode, int bits) => mantle.AddOpenTelemetryInstrumentation(options =>
            {
                options.Enabled = registrationMode == "enabled";
                options.EnableAspNetCoreTracing = (bits & 1) != 0;
                options.EnableHttpClientTracing = (bits & 2) != 0;
                options.EnableRuntimeMetrics = (bits & 4) != 0;
            });
            if (mode != "absent") Register(mode, selection);
            if (secondMode is not null) Register(secondMode, secondSelection);
            // Never call WithTracing/WithMetrics to manufacture a provider for an absent signal.
            if (collectors && builder.Services.Any(descriptor => descriptor.ServiceType == typeof(TracerProvider)))
                builder.Services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
                    tracing.AddProcessor(Spans).AddInstrumentation(TraceLifetime.Create));
            if (collectors && builder.Services.Any(descriptor => descriptor.ServiceType == typeof(MeterProvider)))
            {
                Reader = new BaseExportingMetricReader(Metrics);
                builder.Services.ConfigureOpenTelemetryMeterProvider((_, metrics) =>
                    metrics.AddReader(Reader).AddInstrumentation(MetricLifetime.Create));
            }
            App = builder.Build();
            App.Use(async (context, next) =>
            {
                using var requestAbort = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, RequestAbort.Token);
                context.RequestAborted = requestAbort.Token;
                try { await next(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    RequestCancelled.TrySetResult(true);
                    throw;
                }
            });
            App.UseServiceMantlePipeline();
            App.MapGet("/ok", () =>
            {
                Interlocked.Increment(ref EndpointCalls);
                Activity.Current?.SetTag("composition.test.endpoint", true);
                return Results.Ok();
            }).RequireServiceMantleSecurityResponseHeaders();
            App.MapGet("/exception", (HttpContext _) => Task.FromException(new InvalidOperationException("controlled endpoint failure")))
                .RequireServiceMantleSecurityResponseHeaders();
            App.MapGet("/cancel", async (HttpContext context) =>
            {
                RequestEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }).RequireServiceMantleSecurityResponseHeaders();
        }

        internal async Task StartAsync()
        {
            await App.StartAsync(Token);
            Client.BaseAddress = new Uri(Assert.Single(App.Urls));
            Tracer = App.Services.GetService<TracerProvider>();
            Meter = App.Services.GetService<MeterProvider>();
        }
        internal async Task<HttpResponseMessage> SendAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("x-correlation-id", Correlation);
            return await Client.SendAsync(request, Token);
        }
        internal async Task StopAndDisposeAsync()
        {
            if (!stopped)
            {
                await App.StopAsync(Token);
                stopped = true;
            }
            if (!disposed)
            {
                // The error must reach the test; cleanup retries only through explicit finally ownership.
                await App.DisposeAsync();
                disposed = true;
            }
        }
        public async ValueTask DisposeAsync()
        {
            try { await StopAndDisposeAsync(); }
            finally
            {
                Client.Dispose();
                RequestAbort.Dispose();
                IncomingSource.Dispose();
                OutgoingSource.Dispose();
                runtimeMeter.Dispose();
                Reader?.Dispose();
                Spans.Dispose();
            }
        }
    }

    private sealed class SnapshotSource : IServiceHealthSnapshotSource
    {
        internal bool Ready = true;
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ServiceHealthSnapshot(Ready ? ServiceStartupPhase.Completed : ServiceStartupPhase.PendingSetup,
                ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable));
    }
    private sealed class Lifetime(bool throwOnDispose = false) : IDisposable
    {
        internal int Created;
        internal int Disposed;
        internal Lifetime Create() { Interlocked.Increment(ref Created); return this; }
        public void Dispose()
        {
            var count = Interlocked.Increment(ref Disposed);
            if (throwOnDispose && count == 1) throw new InvalidOperationException("controlled instrumentation disposal failure");
        }
    }
    private sealed record Span(ActivityKind Kind, string TraceId, string? Url, bool IsTestEndpoint);
    private sealed class SpanCollector : BaseProcessor<Activity>
    {
        internal ConcurrentQueue<Span> Events { get; } = new();
        internal TaskCompletionSource Incoming { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Outgoing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void OnEnd(Activity activity)
        {
            if (activity.Kind is not (ActivityKind.Server or ActivityKind.Client)) return;
            Events.Enqueue(new Span(activity.Kind, activity.TraceId.ToString(), activity.GetTagItem("url.full")?.ToString(),
                activity.GetTagItem("composition.test.endpoint") is true));
            if (activity.Kind == ActivityKind.Server) Incoming.TrySetResult();
            else Outgoing.TrySetResult();
        }
    }
    private sealed class MetricCollector : BaseExporter<Metric>
    {
        internal int ExportCalls;
        internal long TestCounterValue;
        public override ExportResult Export(in Batch<Metric> batch)
        {
            Interlocked.Increment(ref ExportCalls);
            foreach (var metric in batch)
            {
                if (metric.MeterName != "System.Runtime" || metric.Name != CounterName) continue;
                foreach (ref readonly var point in metric.GetMetricPoints()) TestCounterValue = point.GetSumLong();
            }
            return ExportResult.Success;
        }
    }
    private sealed class RecordingLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        internal int ProblemErrors;
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }
        private sealed class Logger(RecordingLogs owner, string category) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                owner.Messages.Enqueue(formatter(state, exception));
                if (category == "ServiceMantle.Http.ProblemDetails") Interlocked.Increment(ref owner.ProblemErrors);
            }
        }
    }
}
