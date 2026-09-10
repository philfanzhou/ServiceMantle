using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle.AspNetCore;
using ServiceMantle.Logging;
using ServiceMantle.ReferenceService.Logging;
using ServiceMantle.ReferenceService.Telemetry;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Serializes the sample's telemetry acceptance. The observations below are process-wide - listener
/// state on <see cref="ActivitySource"/> and <see cref="Meter"/> is not scoped to one host - so this
/// collection runs alone. It is declared here rather than on the assembly so the rest of this test
/// project keeps its existing parallelism.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReferenceTelemetryCollection
{
    public const string Name = "reference-telemetry";
}

/// <summary>
/// Accepts the sample's base instrumentation through its real <see cref="ReferenceApplication"/>
/// path: the switch, the providers it does and does not create, the resource identity it reuses,
/// its interaction with the logging switch, and its start, cancellation, and disposal boundaries.
/// </summary>
/// <remarks>
/// Nothing here wires or simulates an exporter, a Prometheus endpoint, a health source, or an
/// installation phase. A disabled row is never given a provider by the test in order to observe one.
/// </remarks>
[Collection(ReferenceTelemetryCollection.Name)]
public sealed class ReferenceTelemetryTests
{
    private const string Secret = "reference-telemetry-unrelated-config-secret";
    private const string RuntimeMeterName = "System.Runtime";
    private const string TestCounterName = "reference.telemetry.test";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("not-a-boolean")]
    public async Task An_unauthorized_switch_creates_no_provider_and_keeps_the_skeleton_host(string? value)
    {
        await using var host = new ReferenceTelemetryHost(telemetry: value);
        await host.StartAsync();

        Assert.Null(host.App.Services.GetService<TracerProvider>());
        Assert.Null(host.App.Services.GetService<MeterProvider>());
        Assert.Null(host.App.Services.GetService<ReferenceTelemetryRegistration>());
        Assert.False(host.IncomingSource.HasListeners());
        Assert.False(host.OutgoingSource.HasListeners());
        Assert.False(host.RuntimeCounter.Enabled);

        using (var root = await host.SendAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Contains("skeleton", await root.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        foreach (var path in new[] { "/metrics", "/health", "/management", "/setup" })
        {
            using var missing = await host.SendAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        await host.StopAndDisposeAsync();
        // The disabled row also never touched a database.
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Directory));
        host.AssertNoSecretInLogs();
    }

    [Fact]
    public async Task The_enabled_switch_creates_one_tracer_and_one_meter_and_observes_real_signals()
    {
        await using var host = new ReferenceTelemetryHost(telemetry: "true", collectors: true);
        await host.StartAsync();

        Assert.NotNull(host.App.Services.GetService<ReferenceTelemetryRegistration>());
        Assert.Single(host.App.Services.GetServices<TracerProvider>());
        Assert.Single(host.App.Services.GetServices<MeterProvider>());
        Assert.True(host.IncomingSource.HasListeners());
        Assert.True(host.OutgoingSource.HasListeners());
        Assert.True(host.RuntimeCounter.Enabled);

        // A real loopback request, so the server span and the client span are both genuine.
        using (var root = await host.SendAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        }

        await host.Spans.Incoming.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await host.Spans.Outgoing.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, host.Spans.Events.Count(span => span.Kind == ActivityKind.Server));
        Assert.Equal(1, host.Spans.Events.Count(span => span.Kind == ActivityKind.Client));

        host.RuntimeCounter.Add(7);
        Assert.True(host.Reader!.Collect());
        Assert.True(host.Metrics.ExportCalls > 0);
        Assert.Equal(7, host.Metrics.TestCounterValue);
        Assert.True(host.Metrics.SawRuntimeInstrument);

        await host.StopAndDisposeAsync();
        host.AssertDetached();
        host.AssertNoSecretInLogs();
    }

    [Fact]
    public async Task The_enabled_switch_runs_without_any_test_owned_reader_processor_or_exporter()
    {
        // No collector is attached at all, so nothing the test installs can be what makes it work.
        await using var host = new ReferenceTelemetryHost(telemetry: "true", collectors: false);
        await host.StartAsync();

        Assert.NotNull(host.Tracer);
        Assert.NotNull(host.Meter);
        Assert.Null(host.Reader);
        Assert.Single(host.App.Services.GetServices<TracerProvider>());
        Assert.Single(host.App.Services.GetServices<MeterProvider>());
        Assert.True(host.IncomingSource.HasListeners());
        // With no reader attached, the meter provider collects nothing - which is exactly why the
        // metric evidence in the test above has to come from a reader the test owns.
        Assert.False(host.RuntimeCounter.Enabled);

        using (var root = await host.SendAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        }

        await host.StopAndDisposeAsync();
        Assert.Empty(host.Spans.Events);
        Assert.Equal(0, host.Metrics.ExportCalls);
        host.AssertDetached();
    }

    [Fact]
    public async Task Both_providers_carry_exactly_the_existing_service_identity()
    {
        await using var host = new ReferenceTelemetryHost(telemetry: "true", collectors: true);
        await host.StartAsync();
        var identity = host.App.Services.GetRequiredService<ServiceLogContext>();
        var expected = new Dictionary<string, object>
        {
            ["service.name"] = identity.ServiceName,
            ["service.version"] = identity.ServiceVersion,
            ["service.instance.id"] = identity.InstanceId,
        };

        foreach (BaseProvider provider in new BaseProvider[]
        {
            host.App.Services.GetRequiredService<TracerProvider>(),
            host.App.Services.GetRequiredService<MeterProvider>(),
        })
        {
            // Exactly these three, so the sample adds no attribute and no high-cardinality dimension.
            Assert.Equal(
                expected,
                provider.GetResource().Attributes.ToDictionary(item => item.Key, item => item.Value));
        }

        // The unrelated configuration secret reached neither resource.
        Assert.DoesNotContain(Secret, string.Join('|', expected.Values), StringComparison.Ordinal);
        await host.StopAndDisposeAsync();
        host.AssertNoSecretInLogs();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("true", null)]
    [InlineData(null, "true")]
    [InlineData("true", "true")]
    public async Task Logging_and_telemetry_switches_compose_in_every_combination(
        string? logging,
        string? telemetry)
    {
        await using var host = new ReferenceTelemetryHost(telemetry, logging, collectors: true);
        await host.StartAsync();

        using (var root = await host.SendAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Contains("skeleton", await root.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
            // Logging owns the correlation Header; telemetry neither adds nor removes it.
            Assert.Equal(
                logging is not null,
                root.Headers.Contains(ReferenceTelemetryHost.CorrelationHeader));
        }

        Assert.Equal(telemetry is not null, host.App.Services.GetService<TracerProvider>() is not null);
        Assert.Equal(telemetry is not null, host.App.Services.GetService<MeterProvider>() is not null);

        await host.StopAndDisposeAsync();
        host.AssertDetached();
        host.AssertNoSecretInLogs();
    }

    [Fact]
    public async Task A_pre_cancelled_start_never_reports_the_application_as_started()
    {
        await using var host = new ReferenceTelemetryHost(telemetry: "true", collectors: true);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await host.App.StartAsync(abort.Token));

        Assert.False(host.App.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Empty(host.Spans.Events);
        Assert.Equal(0, host.Metrics.ExportCalls);
    }

    [Fact]
    public async Task A_cancelled_request_stays_cancelled_and_the_host_keeps_serving()
    {
        await using var host = new ReferenceTelemetryHost(telemetry: "true", collectors: true);
        await host.StartAsync();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await host.SendAsync("/", abort.Token));

        Assert.True(abort.IsCancellationRequested);
        Assert.IsNotType<HttpRequestException>(failure);
        using var afterwards = await host.SendAsync("/");
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);

        await host.StopAndDisposeAsync();
        host.AssertDetached();
    }

    [Fact]
    public async Task A_controlled_disposal_failure_stays_visible_and_the_fixture_still_cleans_up()
    {
        var host = new ReferenceTelemetryHost(telemetry: "true", collectors: true, throwOnDispose: true);
        try
        {
            await host.StartAsync();

            // The failure must reach the caller rather than being swallowed by the shutdown path.
            var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await host.StopAndDisposeAsync());
            Assert.Contains(
                "controlled instrumentation disposal failure",
                failure.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(1, host.TraceLifetime.Disposed);
        }
        finally
        {
            // A failed SDK disposal need not visit every later resource, so the fixture releases the
            // handles it owns itself rather than leaving a process-wide listener attached.
            host.Tracer?.Dispose();
            host.Meter?.Dispose();
            await host.DisposeAsync();
        }

        host.AssertDetached();
    }

    [Fact]
    public async Task An_equivalent_repeated_registration_still_produces_one_provider_and_one_span()
    {
        await using var host = new ReferenceTelemetryHost(
            telemetry: "true",
            collectors: true,
            duplicateRegistration: true);
        await host.StartAsync();

        Assert.Single(host.App.Services.GetServices<TracerProvider>());
        Assert.Single(host.App.Services.GetServices<MeterProvider>());

        using (var root = await host.SendAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        }

        await host.Spans.Incoming.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, host.Spans.Events.Count(span => span.Kind == ActivityKind.Server));
        Assert.Equal(1, host.TraceLifetime.Created);

        await host.StopAndDisposeAsync();
        host.AssertDetached();
    }

    [Fact]
    public async Task A_conflicting_extra_registration_is_refused_by_the_existing_startup_validation()
    {
        // The sample does not weaken the public package's own safe-start rule; it inherits it.
        await using var host = new ReferenceTelemetryHost(
            telemetry: "true",
            collectors: false,
            conflictingRegistration: true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await host.App.StartAsync(Token));

        Assert.Contains("conflicting", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
        Assert.False(host.App.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.False(host.IncomingSource.HasListeners());
        Assert.False(host.RuntimeCounter.Enabled);
    }

    [Fact]
    public void The_samples_restored_graph_carries_the_base_package_and_registers_no_exporter()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "eng", "packages.json")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        using var assets = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root.FullName, "artifacts", "obj", "ServiceMantle.ReferenceService", "project.assets.json")));
        var libraries = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(library => library.Name)
            .ToList();

        // The base instrumentation packages are a real, static dependency of the sample now.
        Assert.Contains(libraries, name =>
            name.StartsWith("OpenTelemetry.Instrumentation.AspNetCore/", StringComparison.Ordinal));
        Assert.Contains(libraries, name =>
            name.StartsWith("OpenTelemetry.Instrumentation.Runtime/", StringComparison.Ordinal));
        // ServiceMantle.OpenTelemetry now ships the OTLP and Prometheus exporters in the same
        // package as the instrumentation, so both drivers are in the sample's graph whether it wants
        // them or not. Absence from the graph is therefore no longer the guarantee. The guarantee is
        // that presence is not activation: the sample calls AddOpenTelemetryInstrumentation and
        // nothing else, so no exporter-owned service reaches its container and nothing it composes
        // can reach a remote destination.
        Assert.Contains(libraries, name =>
            name.StartsWith("OpenTelemetry.Exporter.OpenTelemetryProtocol/", StringComparison.Ordinal));
        Assert.Contains(libraries, name =>
            name.StartsWith("OpenTelemetry.Exporter.Prometheus.AspNetCore/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_enabled_sample_registers_no_exporter_owned_service()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sm-reference-telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var builder = ReferenceApplication.CreateBuilder([
                "--ReferenceService:DatabasePath", Path.Combine(directory, "reference.db"),
                "--" + ReferenceTelemetryDefaults.EnabledKey, "true",
            ]);

            Assert.Contains(builder.Services, descriptor =>
                descriptor.ServiceType == typeof(ReferenceTelemetryRegistration));
            Assert.DoesNotContain(builder.Services, descriptor =>
                IsExporterOwned(descriptor.ServiceType) ||
                IsExporterOwned(descriptor.ImplementationType) ||
                IsExporterOwned(descriptor.ImplementationInstance?.GetType()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool IsExporterOwned(Type? type) =>
        type?.Namespace is { } space &&
        (space.StartsWith("ServiceMantle.OpenTelemetry.Otlp", StringComparison.Ordinal) ||
            space.StartsWith("ServiceMantle.OpenTelemetry.Prometheus", StringComparison.Ordinal) ||
            space.StartsWith("OpenTelemetry.Exporter", StringComparison.Ordinal));

    /// <summary>
    /// Starts the sample's real composition on loopback Kestrel and owns every handle it creates.
    /// </summary>
    private sealed class ReferenceTelemetryHost : IAsyncDisposable
    {
        internal const string CorrelationHeader = "x-correlation-id";

        private readonly Meter runtimeMeter = new(RuntimeMeterName);
        private readonly HttpClient client = new();
        private bool stopped;
        private bool disposed;

        internal WebApplication App { get; }

        internal string Directory { get; }

        internal ActivitySource IncomingSource { get; } = new("Microsoft.AspNetCore");

        internal ActivitySource OutgoingSource { get; } = new("System.Net.Http");

        internal Counter<long> RuntimeCounter { get; }

        internal SpanCollector Spans { get; } = new();

        internal MetricCollector Metrics { get; } = new();

        internal RecordingLogs Logs { get; } = new();

        internal Lifetime TraceLifetime { get; }

        internal Lifetime MetricLifetime { get; } = new();

        internal BaseExportingMetricReader? Reader { get; }

        internal TracerProvider? Tracer { get; private set; }

        internal MeterProvider? Meter { get; private set; }

        internal ReferenceTelemetryHost(
            string? telemetry,
            string? logging = null,
            bool collectors = false,
            bool throwOnDispose = false,
            bool duplicateRegistration = false,
            bool conflictingRegistration = false)
        {
            RuntimeCounter = runtimeMeter.CreateCounter<long>(TestCounterName);
            TraceLifetime = new Lifetime(throwOnDispose);
            Directory = Path.Combine(Path.GetTempPath(), $"sm-reference-telemetry-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);

            var arguments = new List<string>
            {
                "--ReferenceService:DatabasePath", Path.Combine(Directory, "reference.db"),
                // An unrelated secret that no telemetry path reads.
                "--Unrelated:Password", Secret,
            };
            if (telemetry is not null)
            {
                arguments.AddRange([
                    "--" + ReferenceTelemetryDefaults.EnabledKey, telemetry]);
            }

            if (logging is not null)
            {
                arguments.AddRange(["--" + ReferenceLoggingDefaults.EnabledKey, logging]);
            }

            var builder = ReferenceApplication.CreateBuilder([.. arguments]);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.AddProvider(Logs);
            if (duplicateRegistration)
            {
                // The same base wiring again, which the public package normalizes to one provider.
                SameMantle(builder).AddReferenceTelemetry(builder.Configuration);
            }

            if (conflictingRegistration)
            {
                SameMantle(builder)
                    .AddOpenTelemetryInstrumentation(options => options.EnableRuntimeMetrics = false);
            }

            if (collectors && builder.Services.Any(descriptor =>
                    descriptor.ServiceType == typeof(TracerProvider)))
            {
                builder.Services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
                    tracing.AddProcessor(Spans).AddInstrumentation(TraceLifetime.Create));
            }

            if (collectors && builder.Services.Any(descriptor =>
                    descriptor.ServiceType == typeof(MeterProvider)))
            {
                Reader = new BaseExportingMetricReader(Metrics);
                builder.Services.ConfigureOpenTelemetryMeterProvider((_, metrics) =>
                    metrics.AddReader(Reader).AddInstrumentation(MetricLifetime.Create));
            }

            App = ReferenceApplication.Build(builder);
        }

        /// <summary>
        /// Returns a builder over the sample's own registration. An identical repeated
        /// <c>AddServiceMantle</c> re-registers nothing; it only hands back the same container.
        /// </summary>
        private static ServiceMantleBuilder SameMantle(WebApplicationBuilder builder) =>
            builder.Services.AddServiceMantle(
                ServiceId.Parse("reference-service"),
                InstanceId.Parse("reference-local"));

        internal async Task StartAsync()
        {
            await App.StartAsync(Token);
            client.BaseAddress = new Uri(Assert.Single(App.Urls));
            Tracer = App.Services.GetService<TracerProvider>();
            Meter = App.Services.GetService<MeterProvider>();
        }

        internal Task<HttpResponseMessage> SendAsync(string path, CancellationToken? cancellationToken = null) =>
            client.GetAsync(path, cancellationToken ?? Token);

        internal async Task StopAndDisposeAsync()
        {
            if (!stopped)
            {
                await App.StopAsync(Token);
                stopped = true;
            }

            if (!disposed)
            {
                await App.DisposeAsync();
                disposed = true;
            }
        }

        internal void AssertDetached()
        {
            Assert.False(IncomingSource.HasListeners());
            Assert.False(OutgoingSource.HasListeners());
            Assert.False(RuntimeCounter.Enabled);
            Assert.True(TraceLifetime.Disposed >= TraceLifetime.Created);
            Assert.Equal(MetricLifetime.Created, MetricLifetime.Disposed);
            var exports = Metrics.ExportCalls;
            RuntimeCounter.Add(1);
            Assert.Equal(exports, Metrics.ExportCalls);
        }

        internal void AssertNoSecretInLogs() => Assert.All(
            Logs.Messages,
            message => Assert.DoesNotContain(Secret, message, StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!disposed)
                {
                    await StopAndDisposeAsync();
                }
            }
            finally
            {
                client.Dispose();
                IncomingSource.Dispose();
                OutgoingSource.Dispose();
                runtimeMeter.Dispose();
                Reader?.Dispose();
                Spans.Dispose();
                try
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                }
            }
        }
    }

    private sealed class Lifetime(bool throwOnDispose = false) : IDisposable
    {
        private int created;
        private int disposed;

        internal int Created => Volatile.Read(ref created);

        internal int Disposed => Volatile.Read(ref disposed);

        internal Lifetime Create()
        {
            Interlocked.Increment(ref created);
            return this;
        }

        public void Dispose()
        {
            var count = Interlocked.Increment(ref disposed);
            if (throwOnDispose && count == 1)
            {
                throw new InvalidOperationException("controlled instrumentation disposal failure");
            }
        }
    }

    private sealed record Span(ActivityKind Kind, string TraceId);

    private sealed class SpanCollector : BaseProcessor<Activity>
    {
        internal ConcurrentQueue<Span> Events { get; } = new();

        internal TaskCompletionSource Incoming { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Outgoing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void OnEnd(Activity activity)
        {
            if (activity.Kind is not (ActivityKind.Server or ActivityKind.Client))
            {
                return;
            }

            Events.Enqueue(new Span(activity.Kind, activity.TraceId.ToString()));
            if (activity.Kind == ActivityKind.Server)
            {
                Incoming.TrySetResult();
            }
            else
            {
                Outgoing.TrySetResult();
            }
        }
    }

    private sealed class MetricCollector : BaseExporter<Metric>
    {
        private int exportCalls;

        internal int ExportCalls => Volatile.Read(ref exportCalls);

        internal long TestCounterValue { get; private set; }

        internal bool SawRuntimeInstrument { get; private set; }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Interlocked.Increment(ref exportCalls);
            foreach (var metric in batch)
            {
                if (metric.MeterName != RuntimeMeterName)
                {
                    continue;
                }

                if (metric.Name == TestCounterName)
                {
                    foreach (ref readonly var point in metric.GetMetricPoints())
                    {
                        TestCounterValue = point.GetSumLong();
                    }
                }
                else
                {
                    // A signal the runtime instrumentation produced, not one the test created.
                    SawRuntimeInstrument = true;
                }
            }

            return ExportResult.Success;
        }
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(RecordingLogs owner) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(formatter(state, exception));
        }
    }
}
