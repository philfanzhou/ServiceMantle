using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle.Diagnostics;
using ServiceMantle.OpenTelemetry.Otlp;
using ServiceMantle.ReferenceService.Telemetry;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Accepts the sample's optional OTLP trace and metric export wiring through its real
/// <see cref="ReferenceApplication"/> path against a loopback collector: the explicit switches, the
/// parse failures before the host is built, the loopback-only testing affordance, the authentication
/// header, delivery-free operation without a collector, cancellation, and secret hygiene.
/// </summary>
/// <remarks>
/// The class needs no container. It runs in the serialized telemetry collection because provider
/// and meter state is process-wide.
/// </remarks>
[Collection(ReferenceTelemetryCollection.Name)]
public sealed class ReferenceOtlpTests
{
    private const string SyntheticSecret = "Bearer synthetic-otlp-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Both_signals_off_register_nothing()
    {
        var builder = CreateBuilder(
            "--ReferenceService:Telemetry:Otlp:Traces:Enabled", "false",
            "--ReferenceService:Telemetry:Otlp:Metrics:Enabled", "not-a-boolean");

        Assert.DoesNotContain(builder.Services, descriptor =>
            IsOtlpOwned(descriptor.ServiceType) ||
            IsOtlpOwned(descriptor.ImplementationType) ||
            IsOtlpOwned(descriptor.ImplementationInstance?.GetType()));
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IRemoteTelemetryAuthenticationResolver));
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(TracerProvider));
    }

    [Fact]
    public async Task Traces_reach_the_loopback_collector_with_the_configured_header()
    {
        await using var collector = await LoopbackCollector.StartAsync();
        using var logs = new CapturingLoggerProvider();
        await using var app = await StartAppAsync(
            logs,
            "--" + ReferenceTelemetryDefaults.EnabledKey, "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Enabled", "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Protocol", "HttpProtobuf",
            "--ReferenceService:Telemetry:Otlp:Traces:Endpoint", collector.TracesEndpoint.ToString(),
            "--ReferenceService:Telemetry:AllowNothing", "0",
            "--ReferenceService:Telemetry:Otlp:AllowInsecureLoopbackForTesting", "true",
            "--ReferenceService:Telemetry:Otlp:Authentication:HeaderName", "Authorization",
            "--ReferenceService:Telemetry:Otlp:Authentication:HeaderValue", SyntheticSecret);

        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        using (var root = await client.GetAsync("/", Token))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        }

        app.Services.GetRequiredService<TracerProvider>().ForceFlush(5000);
        var captured = await collector.Request.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal("/v1/traces", captured.Path);
        Assert.Equal("application/x-protobuf", captured.ContentType);
        Assert.Equal(SyntheticSecret, captured.Authorization);
        Assert.NotEmpty(captured.Body);

        AssertSecretStaysHidden(app, logs);
    }

    [Fact]
    public async Task Metrics_reach_the_loopback_collector_when_base_telemetry_is_on()
    {
        await using var collector = await LoopbackCollector.StartAsync();
        await using var app = await StartAppAsync(
            null,
            "--" + ReferenceTelemetryDefaults.EnabledKey, "true",
            "--ReferenceService:Telemetry:Otlp:Metrics:Enabled", "true",
            "--ReferenceService:Telemetry:Otlp:Metrics:Protocol", "HttpProtobuf",
            "--ReferenceService:Telemetry:Otlp:Metrics:Endpoint", collector.MetricsEndpoint.ToString(),
            "--ReferenceService:Telemetry:Otlp:AllowInsecureLoopbackForTesting", "true");

        // The runtime meter the base instrumentation registered is exported without any request;
        // a forced flush is the deterministic observation point.
        Assert.NotNull(app.Services.GetService<MeterProvider>());
        app.Services.GetRequiredService<MeterProvider>().ForceFlush();

        var captured = await collector.Request.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal("/v1/metrics", captured.Path);
        Assert.Equal("application/x-protobuf", captured.ContentType);
        Assert.NotEmpty(captured.Body);
    }

    [Fact]
    public async Task A_non_development_environment_refuses_the_loopback_http_endpoint()
    {
        await using var collector = await LoopbackCollector.StartAsync();
        var builder = CreateBuilder(
            "--environment", "Production",
            "--" + ReferenceTelemetryDefaults.EnabledKey, "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Enabled", "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Protocol", "HttpProtobuf",
            "--ReferenceService:Telemetry:Otlp:Traces:Endpoint", collector.TracesEndpoint.ToString(),
            // Ignored outside Development, so the package's own startup validation answers.
            "--ReferenceService:Telemetry:Otlp:AllowInsecureLoopbackForTesting", "true",
            "--ReferenceService:Telemetry:Otlp:Authentication:HeaderName", "Authorization",
            "--ReferenceService:Telemetry:Otlp:Authentication:HeaderValue", SyntheticSecret);
        await using var app = ReferenceApplication.Build(builder);

        var failure = await Assert.ThrowsAsync<OtlpConfigurationException>(
            () => app.StartAsync(Token));

        Assert.Equal("otlp.insecure_endpoint", failure.ErrorCode);
        Assert.False(app.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, collector.RequestCount);
        Assert.DoesNotContain(SyntheticSecret, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    [InlineData("")]
    public void An_unknown_protocol_fails_before_build(string protocol)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => CreateBuilder(
            "--ReferenceService:Telemetry:Otlp:Traces:Enabled", "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Protocol", protocol));

        Assert.Contains("ReferenceService:Telemetry:Otlp:Traces:Protocol", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_endpoint_fails_before_build()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => CreateBuilder(
            "--ReferenceService:Telemetry:Otlp:Traces:Enabled", "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Endpoint", "not a uri"));

        Assert.Contains("ReferenceService:Telemetry:Otlp:Traces:Endpoint", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_endpoint_fails_at_startup_with_the_fixed_error_code()
    {
        var builder = CreateBuilder(
            "--" + ReferenceTelemetryDefaults.EnabledKey, "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Enabled", "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Protocol", "HttpProtobuf");
        await using var app = ReferenceApplication.Build(builder);

        var failure = await Assert.ThrowsAsync<OtlpConfigurationException>(
            () => app.StartAsync(Token));

        Assert.Equal("otlp.endpoint_required", failure.ErrorCode);
        Assert.False(app.Lifetime.ApplicationStarted.IsCancellationRequested);
    }

    [Theory]
    [InlineData("name-only")]
    [InlineData("value-only")]
    public void A_half_configured_authentication_fails_before_build(string shape)
    {
        var arguments = new List<string>
        {
            "--ReferenceService:Telemetry:Otlp:Traces:Enabled", "true",
        };
        if (shape == "name-only")
        {
            arguments.AddRange(["--ReferenceService:Telemetry:Otlp:Authentication:HeaderName", "Authorization"]);
        }
        else
        {
            arguments.AddRange(["--ReferenceService:Telemetry:Otlp:Authentication:HeaderValue", SyntheticSecret]);
        }

        var failure = Assert.Throws<InvalidOperationException>(() => CreateBuilder([.. arguments]));

        Assert.Contains(
            "ReferenceService:Telemetry:Otlp:Authentication:HeaderName",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceService:Telemetry:Otlp:Authentication:HeaderValue",
            failure.Message,
            StringComparison.Ordinal);
        // The half-configured secret value itself is never echoed.
        Assert.DoesNotContain(SyntheticSecret, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_collector_neither_blocks_requests_nor_shutdown()
    {
        var deadPort = ReserveUnusedPort();
        await using var app = await StartAppAsync(
            null,
            "--" + ReferenceTelemetryDefaults.EnabledKey, "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Enabled", "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Protocol", "HttpProtobuf",
            "--ReferenceService:Telemetry:Otlp:Traces:Endpoint", $"http://127.0.0.1:{deadPort}/v1/traces",
            "--ReferenceService:Telemetry:Otlp:AllowInsecureLoopbackForTesting", "true");

        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        using (var root = await client.GetAsync("/", Token))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task A_pre_cancelled_start_never_calls_the_resolver()
    {
        var resolver = new CountingResolver();
        var builder = CreateBuilder(
            "--" + ReferenceTelemetryDefaults.EnabledKey, "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Enabled", "true",
            "--ReferenceService:Telemetry:Otlp:Traces:Protocol", "HttpProtobuf",
            "--ReferenceService:Telemetry:Otlp:Traces:Endpoint", "https://collector.invalid/v1/traces");
        builder.Services.AddSingleton<IRemoteTelemetryAuthenticationResolver>(resolver);
        await using var app = ReferenceApplication.Build(builder);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.StartAsync(abort.Token));

        Assert.False(app.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, resolver.Calls);
    }

    private static WebApplicationBuilder CreateBuilder(params string[] arguments)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sm-reference-otlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var all = new List<string>
        {
            "--contentRoot", directory,
            "--urls", "http://127.0.0.1:0",
            "--ReferenceService:DatabasePath", Path.Combine(directory, "reference.db"),
            "--environment", "Development",
        };
        all.AddRange(arguments);
        return ReferenceApplication.CreateBuilder([.. all]);
    }

    private static async Task<WebApplication> StartAppAsync(
        CapturingLoggerProvider? logs,
        params string[] arguments)
    {
        var builder = CreateBuilder(arguments);
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        var app = ReferenceApplication.Build(builder);
        await app.StartAsync(Token);
        return app;
    }

    private static void AssertSecretStaysHidden(WebApplication app, CapturingLoggerProvider logs)
    {
        Assert.All(logs.Messages, message =>
            Assert.DoesNotContain(SyntheticSecret, message, StringComparison.Ordinal));
        foreach (var options in app.Services.GetServices<OtlpOptions>())
        {
            Assert.DoesNotContain(SyntheticSecret, options.ToString(), StringComparison.Ordinal);
        }
    }

    private static bool IsOtlpOwned(Type? type) =>
        type?.Namespace is { } space &&
        space.StartsWith("ServiceMantle.OpenTelemetry.Otlp", StringComparison.Ordinal);

    private static int ReserveUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class CountingResolver : IRemoteTelemetryAuthenticationResolver
    {
        internal int Calls;

        public bool TryResolve(string lookup, out RemoteTelemetryAuthenticationHeader? header)
        {
            Interlocked.Increment(ref Calls);
            header = null;
            return false;
        }
    }

    private sealed record CapturedRequest(
        string Path,
        string? ContentType,
        string? Authorization,
        byte[] Body);

    /// <summary>A loopback OTLP collector capturing the first export request verbatim.</summary>
    private sealed class LoopbackCollector : IAsyncDisposable
    {
        private readonly WebApplication application;

        private LoopbackCollector(WebApplication application, Uri baseUri)
        {
            this.application = application;
            BaseUri = baseUri;
        }

        internal Uri BaseUri { get; }

        internal Uri TracesEndpoint => new(BaseUri, "/v1/traces");

        internal Uri MetricsEndpoint => new(BaseUri, "/v1/metrics");

        internal TaskCompletionSource<CapturedRequest> Request { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int RequestCount { get; private set; }

        internal static async Task<LoopbackCollector> StartAsync()
        {
            var port = ReserveUnusedPort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
            var collector = new LoopbackCollector(
                builder.Build(),
                new Uri($"http://127.0.0.1:{port}/"));
            collector.application.Run(collector.HandleAsync);
            await collector.application.StartAsync(Token);
            return collector;
        }

        private async Task HandleAsync(HttpContext context)
        {
            await using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            RequestCount++;
            Request.TrySetResult(new CapturedRequest(
                context.Request.Path.Value ?? string.Empty,
                context.Request.ContentType,
                context.Request.Headers.Authorization,
                body.ToArray()));
            context.Response.ContentType = "application/x-protobuf";
        }

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync();
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> messages = [];

        internal IReadOnlyList<string> Messages => messages;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.messages)
                {
                    owner.messages.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
                }
            }
        }
    }
}
