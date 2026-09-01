using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle.AspNetCore;
using ServiceMantle.OpenTelemetry.Otlp;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Otlp.Tests;

public sealed class ServiceMantleOtlpRegistrationTests
{
    private const string TraceSourceName = "ServiceMantle.Tests.Otlp.Trace";
    private const string MetricSourceName = "ServiceMantle.Tests.Otlp.Metric";

    [Fact]
    public async Task Both_signals_disabled_register_no_provider_or_authentication_activity()
    {
        var (builder, serviceMantle) = CreateHostBuilder();
        var resolver = new RecordingResolver();
        builder.Services.AddSingleton<IServiceMantleOtlpAuthenticationHeaderResolver>(resolver);
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            options.Traces.AuthenticationHeaderName = "trace-secret";
            options.Metrics.AuthenticationHeaderName = "metric-secret";
        });

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(host.Services.GetService<TracerProvider>());
        Assert.Null(host.Services.GetService<MeterProvider>());
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Trace_and_metric_exporters_are_independently_enabled(bool tracing)
    {
        var (builder, serviceMantle) = CreateHostBuilder();
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            var signal = tracing
                ? (ServiceMantleOtlpSignalOptions)options.Traces
                : options.Metrics;
            signal.Enabled = true;
            signal.Endpoint = new Uri("https://collector.example:4317/");
        });

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(tracing, host.Services.GetService<TracerProvider>() is not null);
        Assert.Equal(!tracing, host.Services.GetService<MeterProvider>() is not null);
    }

    [Theory]
    [InlineData("protocol", WellKnownServiceMantleOtlpErrorCodes.InvalidProtocol)]
    [InlineData("missing-endpoint", WellKnownServiceMantleOtlpErrorCodes.EndpointRequired)]
    [InlineData("relative-endpoint", WellKnownServiceMantleOtlpErrorCodes.InvalidEndpoint)]
    [InlineData("userinfo", WellKnownServiceMantleOtlpErrorCodes.InvalidEndpoint)]
    [InlineData("query", WellKnownServiceMantleOtlpErrorCodes.InvalidEndpoint)]
    [InlineData("insecure", WellKnownServiceMantleOtlpErrorCodes.InsecureEndpoint)]
    [InlineData("timeout-low", WellKnownServiceMantleOtlpErrorCodes.InvalidExportTimeout)]
    [InlineData("timeout-high", WellKnownServiceMantleOtlpErrorCodes.InvalidExportTimeout)]
    [InlineData("delay-low", WellKnownServiceMantleOtlpErrorCodes.InvalidBatchDelay)]
    [InlineData("delay-high", WellKnownServiceMantleOtlpErrorCodes.InvalidBatchDelay)]
    [InlineData("queue-low", WellKnownServiceMantleOtlpErrorCodes.InvalidQueueSize)]
    [InlineData("queue-high", WellKnownServiceMantleOtlpErrorCodes.InvalidQueueSize)]
    [InlineData("batch-low", WellKnownServiceMantleOtlpErrorCodes.InvalidBatchSize)]
    [InlineData("batch-high", WellKnownServiceMantleOtlpErrorCodes.InvalidBatchSize)]
    [InlineData("batch-over-queue", WellKnownServiceMantleOtlpErrorCodes.InvalidBatchSize)]
    public async Task Invalid_protocol_endpoint_and_numeric_boundaries_fail_at_startup(
        string scenario,
        string expectedCode)
    {
        const string unsafeEndpoint = "https://user:uri-secret@collector.example:4317/?token=query-secret";
        var (builder, serviceMantle) = CreateHostBuilder();
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            var traces = options.Traces;
            traces.Enabled = true;
            traces.Endpoint = new Uri("https://collector.example:4317/");
            switch (scenario)
            {
                case "protocol": traces.Protocol = (ServiceMantleOtlpProtocol)999; break;
                case "missing-endpoint": traces.Endpoint = null; break;
                case "relative-endpoint": traces.Endpoint = new Uri("relative", UriKind.Relative); break;
                case "userinfo": traces.Endpoint = new Uri(unsafeEndpoint); break;
                case "query": traces.Endpoint = new Uri("https://collector.example:4317/?token=query-secret"); break;
                case "insecure": traces.Endpoint = new Uri("http://collector.example:4317/"); break;
                case "timeout-low": traces.ExportTimeout = TimeSpan.FromMilliseconds(999); break;
                case "timeout-high": traces.ExportTimeout = TimeSpan.FromMilliseconds(30_001); break;
                case "delay-low": traces.BatchDelay = TimeSpan.FromMilliseconds(99); break;
                case "delay-high": traces.BatchDelay = TimeSpan.FromMilliseconds(30_001); break;
                case "queue-low": traces.MaxQueueSize = 99; break;
                case "queue-high": traces.MaxQueueSize = 50_001; break;
                case "batch-low": traces.MaxExportBatchSize = 0; break;
                case "batch-high": traces.MaxExportBatchSize = 1_001; break;
                case "batch-over-queue":
                    traces.MaxQueueSize = 100;
                    traces.MaxExportBatchSize = 101;
                    break;
                default: throw new InvalidOperationException();
            }
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleOtlpConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.DoesNotContain("uri-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServiceMantleOtlpProtocol.Grpc, "https://collector.example:4317/")]
    [InlineData(ServiceMantleOtlpProtocol.HttpProtobuf, "https://collector.example:4318/v1/traces")]
    [InlineData(ServiceMantleOtlpProtocol.HttpProtobuf, "https://collector.example:4318/custom/trace-ingest")]
    public async Task Valid_protocol_endpoint_and_inclusive_numeric_boundaries_start(
        ServiceMantleOtlpProtocol protocol,
        string endpoint)
    {
        var (builder, serviceMantle) = CreateHostBuilder();
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            options.Traces.Enabled = true;
            options.Traces.Protocol = protocol;
            options.Traces.Endpoint = new Uri(endpoint);
            options.Traces.ExportTimeout = TimeSpan.FromSeconds(1);
            options.Traces.BatchDelay = TimeSpan.FromMilliseconds(100);
            options.Traces.MaxQueueSize = 100;
            options.Traces.MaxExportBatchSize = 100;
        });

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Missing_or_throwing_authentication_is_safe_and_fails_at_startup()
    {
        const string secret = "resolver-internal-token-secret";
        var (builder, serviceMantle) = CreateHostBuilder();
        builder.Services.AddSingleton<IServiceMantleOtlpAuthenticationHeaderResolver>(
            new RecordingResolver(failure: new InvalidOperationException(secret)));
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            options.Traces.Enabled = true;
            options.Traces.Endpoint = new Uri("https://collector.example:4317/");
            options.Traces.AuthenticationHeaderName = "primary";
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleOtlpConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownServiceMantleOtlpErrorCodes.AuthenticationMissing, exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Authentication_is_resolved_once_and_official_options_receive_the_header_safely()
    {
        const string token = "Bearer header-token-secret";
        var resolver = new RecordingResolver(
            header: new ServiceMantleOtlpAuthenticationHeader("Authorization", token));
        var (builder, serviceMantle) = CreateHostBuilder();
        builder.Services.AddSingleton<IServiceMantleOtlpAuthenticationHeaderResolver>(resolver);
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            options.Traces.Enabled = true;
            options.Traces.Endpoint = new Uri("https://collector.example:4317/");
            options.Traces.AuthenticationHeaderName = "primary";
            options.Traces.ExportTimeout = TimeSpan.FromSeconds(2);
            options.Traces.BatchDelay = TimeSpan.FromMilliseconds(250);
            options.Traces.MaxQueueSize = 321;
            options.Traces.MaxExportBatchSize = 123;
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        var official = host.Services
            .GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>()
            .Get("ServiceMantle.Otlp.Traces");

        Assert.Equal(1, resolver.CallCount);
        Assert.Contains("Authorization=Bearer%20header-token-secret", official.Headers);
        Assert.Equal(2_000, official.TimeoutMilliseconds);
        Assert.Equal(2_000, official.BatchExportProcessorOptions.ExporterTimeoutMilliseconds);
        Assert.Equal(250, official.BatchExportProcessorOptions.ScheduledDelayMilliseconds);
        Assert.Equal(321, official.BatchExportProcessorOptions.MaxQueueSize);
        Assert.Equal(123, official.BatchExportProcessorOptions.MaxExportBatchSize);
        Assert.DoesNotContain(token, resolver.Header!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, new ServiceMantleOtlpOptions().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_resolved_header_fails_without_echoing_name_or_value()
    {
        const string unsafeName = "Authorization\r\nX-Leak";
        const string secret = "header-value-secret";
        var (builder, serviceMantle) = CreateHostBuilder();
        builder.Services.AddSingleton<IServiceMantleOtlpAuthenticationHeaderResolver>(
            new RecordingResolver(new ServiceMantleOtlpAuthenticationHeader(unsafeName, secret)));
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            EnableTrace(options, "https://collector.example:4317/");
            options.Traces.AuthenticationHeaderName = "primary";
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleOtlpConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownServiceMantleOtlpErrorCodes.AuthenticationInvalid, exception.ErrorCode);
        Assert.DoesNotContain(unsafeName, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflicting_repeated_registration_fails_before_export()
    {
        var (builder, serviceMantle) = CreateHostBuilder();
        serviceMantle.AddOpenTelemetryOtlpExporter(options => EnableTrace(options, "https://one.example:4317/"));
        serviceMantle.AddOpenTelemetryOtlpExporter(options => EnableTrace(options, "https://two.example:4317/"));
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleOtlpConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownServiceMantleOtlpErrorCodes.ConflictingRegistration, exception.ErrorCode);
    }

    [Fact]
    public async Task Equivalent_repeated_registration_is_idempotent()
    {
        var (builder, serviceMantle) = CreateHostBuilder();
        serviceMantle.AddOpenTelemetryOtlpExporter(options => EnableTrace(options, "https://one.example:4317/"));
        serviceMantle.AddOpenTelemetryOtlpExporter(options => EnableTrace(options, "https://one.example:4317/"));
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Single(host.Services.GetServices<TracerProvider>());
        Assert.Null(host.Services.GetService<MeterProvider>());
    }

    [Fact]
    public async Task Caller_cancellation_before_startup_validation_does_not_resolve_authentication()
    {
        var resolver = new RecordingResolver(
            header: new ServiceMantleOtlpAuthenticationHeader("Authorization", "cancel-secret"));
        var (builder, serviceMantle) = CreateHostBuilder();
        builder.Services.AddSingleton<IServiceMantleOtlpAuthenticationHeaderResolver>(resolver);
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            EnableTrace(options, "https://collector.example:4317/");
            options.Traces.AuthenticationHeaderName = "primary";
        });
        using var host = builder.Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.StartAsync(cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task HttpProtobuf_trace_export_reaches_local_collector_with_authentication()
    {
        await using var collector = await LoopbackCollector.StartAsync(HttpProtocols.Http1);
        const string token = "Bearer collector-token-secret";
        var (builder, serviceMantle) = CreateHostBuilder();
        builder.Services.AddSingleton<IServiceMantleOtlpAuthenticationHeaderResolver>(
            new RecordingResolver(new ServiceMantleOtlpAuthenticationHeader("Authorization", token)));
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(TraceSourceName));
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            var traces = options.Traces;
            traces.Enabled = true;
            traces.Protocol = ServiceMantleOtlpProtocol.HttpProtobuf;
            traces.Endpoint = new Uri(collector.BaseUri, "/v1/traces");
            traces.AllowInsecureLoopbackForTesting = true;
            traces.AuthenticationHeaderName = "primary";
            traces.BatchDelay = TimeSpan.FromMilliseconds(100);
            traces.MaxExportBatchSize = 1;
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var source = new ActivitySource(TraceSourceName);
        using (source.StartActivity("export-me"))
        {
        }
        Assert.True(host.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000));

        var request = await collector.Request.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal("/v1/traces", request.Path);
        Assert.Equal(token, request.Authorization);
        Assert.Contains("application/x-protobuf", request.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(request.Body);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Grpc_trace_export_reaches_local_http2_collector()
    {
        await using var collector = await LoopbackCollector.StartAsync(HttpProtocols.Http2);
        var (builder, serviceMantle) = CreateHostBuilder();
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(TraceSourceName));
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            var traces = options.Traces;
            traces.Enabled = true;
            traces.Protocol = ServiceMantleOtlpProtocol.Grpc;
            traces.Endpoint = collector.BaseUri;
            traces.AllowInsecureLoopbackForTesting = true;
            traces.BatchDelay = TimeSpan.FromMilliseconds(100);
            traces.MaxExportBatchSize = 1;
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var source = new ActivitySource(TraceSourceName);
        using (source.StartActivity("grpc-export"))
        {
        }
        Assert.True(host.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000));

        var request = await collector.Request.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "/opentelemetry.proto.collector.trace.v1.TraceService/Export",
            request.Path);
        Assert.Contains("application/grpc", request.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(request.Body);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Metric_export_uses_bounded_periodic_reader_and_stops_with_the_sdk()
    {
        await using var collector = await LoopbackCollector.StartAsync(HttpProtocols.Http1);
        var (builder, serviceMantle) = CreateHostBuilder();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(MetricSourceName));
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            var metrics = options.Metrics;
            metrics.Enabled = true;
            metrics.Protocol = ServiceMantleOtlpProtocol.HttpProtobuf;
            metrics.Endpoint = new Uri(collector.BaseUri, "/v1/metrics");
            metrics.AllowInsecureLoopbackForTesting = true;
            metrics.BatchDelay = TimeSpan.FromMilliseconds(100);
            metrics.ExportTimeout = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var meter = new Meter(MetricSourceName);
        meter.CreateCounter<long>("requests").Add(1);
        Assert.True(host.Services.GetRequiredService<MeterProvider>().ForceFlush(5_000));
        var request = await collector.Request.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal("/v1/metrics", request.Path);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();
        var requestCount = collector.RequestCount;
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Equal(requestCount, collector.RequestCount);
    }

    [Fact]
    public async Task Export_connection_failure_does_not_add_retry_or_block_beyond_shutdown_cancellation()
    {
        const string token = "Bearer failed-export-token-secret";
        var unavailablePort = ReserveUnusedPort();
        var (builder, serviceMantle) = CreateHostBuilder();
        builder.Services.AddSingleton<IServiceMantleOtlpAuthenticationHeaderResolver>(
            new RecordingResolver(new ServiceMantleOtlpAuthenticationHeader("Authorization", token)));
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(TraceSourceName));
        serviceMantle.AddOpenTelemetryOtlpExporter(options =>
        {
            var traces = options.Traces;
            traces.Enabled = true;
            traces.Protocol = ServiceMantleOtlpProtocol.HttpProtobuf;
            traces.Endpoint = new Uri($"http://127.0.0.1:{unavailablePort}/v1/traces");
            traces.AllowInsecureLoopbackForTesting = true;
            traces.ExportTimeout = TimeSpan.FromSeconds(1);
            traces.BatchDelay = TimeSpan.FromMilliseconds(100);
            traces.MaxExportBatchSize = 1;
            traces.AuthenticationHeaderName = "primary";
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var source = new ActivitySource(TraceSourceName);
        using (source.StartActivity("failed-export"))
        {
        }
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await host.StopAsync(shutdown.Token);
    }

    private static void EnableTrace(ServiceMantleOtlpOptions options, string endpoint)
    {
        options.Traces.Enabled = true;
        options.Traces.Endpoint = new Uri(endpoint);
    }

    private static (HostApplicationBuilder Builder, ServiceMantleBuilder ServiceMantle) CreateHostBuilder()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.2.3");
        return (builder, serviceMantle);
    }

    private static int ReserveUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RecordingResolver(
        ServiceMantleOtlpAuthenticationHeader? header = null,
        Exception? failure = null) : IServiceMantleOtlpAuthenticationHeaderResolver
    {
        private int callCount;
        public int CallCount => Volatile.Read(ref callCount);
        public ServiceMantleOtlpAuthenticationHeader? Header { get; } = header;

        public bool TryResolve(string name, out ServiceMantleOtlpAuthenticationHeader? resolved)
        {
            Interlocked.Increment(ref callCount);
            if (failure is not null)
            {
                throw failure;
            }

            resolved = Header;
            return resolved is not null;
        }
    }

    private sealed record CapturedRequest(
        string Path,
        string? ContentType,
        string? Authorization,
        byte[] Body);

    private sealed class LoopbackCollector : IAsyncDisposable
    {
        private readonly WebApplication application;
        private int requestCount;

        private LoopbackCollector(WebApplication application, Uri baseUri)
        {
            this.application = application;
            BaseUri = baseUri;
        }

        internal Uri BaseUri { get; }
        internal TaskCompletionSource<CapturedRequest> Request { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RequestCount => Volatile.Read(ref requestCount);

        internal static async Task<LoopbackCollector> StartAsync(HttpProtocols protocols)
        {
            var port = ReserveUnusedPort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
                options.ListenLocalhost(port, listen => listen.Protocols = protocols));
            var app = builder.Build();
            var collector = new LoopbackCollector(app, new Uri($"http://127.0.0.1:{port}/"));
            app.Run(collector.HandleAsync);
            await app.StartAsync(TestContext.Current.CancellationToken);
            return collector;
        }

        private async Task HandleAsync(HttpContext context)
        {
            await using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            Interlocked.Increment(ref requestCount);
            Request.TrySetResult(new CapturedRequest(
                context.Request.Path.Value ?? string.Empty,
                context.Request.ContentType,
                context.Request.Headers.Authorization,
                body.ToArray()));

            if (context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/grpc";
                context.Response.DeclareTrailer("grpc-status");
                await context.Response.Body.WriteAsync(new byte[5], context.RequestAborted);
                context.Response.AppendTrailer("grpc-status", "0");
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/x-protobuf";
            }
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync(CancellationToken.None);
            await application.DisposeAsync();
        }
    }
}
