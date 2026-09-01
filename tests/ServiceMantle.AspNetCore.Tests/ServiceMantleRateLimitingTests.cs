using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceMantle.Audit;
using ServiceMantle.Http;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantleRateLimitingTests
{
    private const string SensitiveOperator = "private-admin-92";
    private const string SensitiveIp = "203.0.113.92";
    private const string SensitiveCredential = "Bearer private-token-92";

    [Fact]
    public void Defaults_AreStableAndTheCapabilityIsOptIn()
    {
        Assert.Equal("servicemantle.setup", ServiceMantleRateLimitingDefaults.SetupPolicyName);
        Assert.Equal("servicemantle.management", ServiceMantleRateLimitingDefaults.ManagementPolicyName);
        Assert.Equal("rate_limit.exceeded", ServiceMantleRateLimitingDefaults.RejectedErrorCode);

        var options = new ServiceMantleRateLimitingOptions();
        Assert.Equal(5, options.Setup.PermitLimit);
        Assert.Equal(120, options.Management.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Setup.Window);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Management.Window);
        Assert.Equal(6, options.Setup.SegmentsPerWindow);
        Assert.Equal(6, options.Management.SegmentsPerWindow);

        var services = new ServiceCollection();
        services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.0.0");
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IConfigureOptions<RateLimiterOptions>));
    }

    [Fact]
    public async Task Registration_IsIdempotentHasNoGlobalLimiterAndConflictsAtStartup()
    {
        await using var identical = Build(options => options.Setup.PermitLimit = 7,
            options => options.Setup.PermitLimit = 7);
        await identical.StartAsync(TestContext.Current.CancellationToken);
        Assert.Null(identical.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter);
        await identical.StopAsync(TestContext.Current.CancellationToken);

        await using var conflicting = Build(options => options.Setup.PermitLimit = 7,
            options => options.Setup.PermitLimit = 8);
        var exception = await Assert.ThrowsAsync<ServiceMantleRateLimitingConfigurationException>(
            () => conflicting.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Registration", exception.FieldName);
    }

    [Theory]
    [InlineData("setup-low", "Setup.PermitLimit")]
    [InlineData("setup-high", "Setup.PermitLimit")]
    [InlineData("management-low", "Management.PermitLimit")]
    [InlineData("management-high", "Management.PermitLimit")]
    [InlineData("window-low", "Setup.Window")]
    [InlineData("window-high", "Setup.Window")]
    [InlineData("segments-low", "Setup.SegmentsPerWindow")]
    [InlineData("segments-high", "Setup.SegmentsPerWindow")]
    [InlineData("segments-window", "Setup.SegmentsPerWindow")]
    public async Task InvalidConfigurationFailsWhenTheHostStarts(string scenario, string fieldName)
    {
        await using var app = Build(options => ConfigureInvalid(options, scenario));
        var exception = await Assert.ThrowsAsync<ServiceMantleRateLimitingConfigurationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(fieldName, exception.FieldName);
    }

    [Fact]
    public async Task PoliciesPartitionByTrustedClientAndRemainIsolated()
    {
        await using var app = await StartAsync(options =>
        {
            ConfigureFast(options.Setup, 2);
            ConfigureFast(options.Management, 2);
        });

        Assert.Equal(200, await SendAsync(app, "/setup", "10.0.0.1"));
        Assert.Equal(200, await SendAsync(app, "/setup", "10.0.0.1"));
        Assert.Equal(429, await SendAsync(app, "/setup", "10.0.0.1"));
        Assert.Equal(429, await SendAsync(app, "/setup", "::ffff:10.0.0.1"));
        Assert.Equal(200, await SendAsync(app, "/setup", "10.0.0.2"));

        Assert.Equal(200, await SendAsync(app, "/management", "10.0.0.1"));
        Assert.Equal(200, await SendAsync(app, "/management", "10.0.0.1"));
        Assert.Equal(429, await SendAsync(app, "/management", "10.0.0.1"));

        Assert.Equal(200, await SendAsync(app, "/setup", remoteIp: null));
        Assert.Equal(200, await SendAsync(app, "/setup", remoteIp: null));
        Assert.Equal(429, await SendAsync(app, "/setup", remoteIp: null));
        var spoofed = await app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = null;
            context.Request.Path = "/setup";
            context.Request.Headers["X-Forwarded-For"] = "192.0.2.92";
        }, TestContext.Current.CancellationToken);
        Assert.Equal(429, spoofed.Response.StatusCode);
    }

    [Fact]
    public async Task ManagementUsesAStableOperatorPartitionAndFallsBackForInvalidClaims()
    {
        await using var app = await StartAsync(options => ConfigureFast(options.Management, 2));
        var first = CreatePrincipal(SensitiveOperator);
        var sameOperator = CreatePrincipal(SensitiveOperator);
        var other = CreatePrincipal("other-admin");

        var firstContext = new DefaultHttpContext { RequestServices = app.Services, User = first };
        var secondContext = new DefaultHttpContext { RequestServices = app.Services, User = sameOperator };
        var firstPartition = ServiceMantleRateLimitingPolicy.ManagementPartition(firstContext);
        var secondPartition = ServiceMantleRateLimitingPolicy.ManagementPartition(secondContext);
        Assert.Equal(firstPartition.PartitionKey, secondPartition.PartitionKey);
        Assert.Equal("management-operator:".Length + 64, firstPartition.PartitionKey.Length);
        Assert.DoesNotContain(SensitiveOperator, firstPartition.PartitionKey, StringComparison.Ordinal);

        Assert.Equal(200, await SendAsync(app, "/management", "10.0.0.1", first));
        Assert.Equal(200, await SendAsync(app, "/management", "10.0.0.2", sameOperator));
        Assert.Equal(429, await SendAsync(app, "/management", "10.0.0.3", first));
        Assert.Equal(200, await SendAsync(app, "/management", "10.0.0.3", other));

        var invalid = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ManagementClaimTypes.OperatorId, SensitiveOperator)],
            ManagementIdentityDefaults.AuthenticationType));
        Assert.Equal(200, await SendAsync(app, "/management", "10.0.0.9", invalid));
        Assert.Equal(200, await SendAsync(app, "/management", "10.0.0.9", invalid));
        Assert.Equal(429, await SendAsync(app, "/management", "10.0.0.9", invalid));
    }

    [Fact]
    public async Task ConcurrentExcessIsRejectedWithoutQueueingAndAWindowRecovers()
    {
        await using var app = await StartAsync(options => ConfigureFast(options.Setup, 5));
        var requests = Enumerable.Range(0, 12)
            .Select(_ => SendAsync(app, "/setup", "192.0.2.5"))
            .ToArray();
        var statuses = await Task.WhenAll(requests);
        Assert.Equal(5, statuses.Count(status => status == 200));
        Assert.Equal(7, statuses.Count(status => status == 429));

        await Task.Delay(TimeSpan.FromSeconds(10.2), TestContext.Current.CancellationToken);
        Assert.Equal(200, await SendAsync(app, "/setup", "192.0.2.5"));
    }

    [Fact]
    public async Task RejectionIsSafeAndIncludesOnlyARealRetryAfter()
    {
        var logs = new CapturingLoggerProvider();
        var metricTags = new ConcurrentQueue<string>();
        using var listener = CreateRateLimitingMeterListener(metricTags);
        listener.Start();
        await using var app = await StartAsync(
            options => ConfigureFast(options.Management, 1),
            logs);
        var principal = CreatePrincipal(SensitiveOperator);
        Assert.Equal(200, await SendAsync(app, "/management", SensitiveIp, principal));

        var context = await app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(SensitiveIp);
            context.Request.Path = "/management";
            context.Request.Headers.Authorization = SensitiveCredential;
            context.Request.Headers.Cookie = "session=private-cookie-92";
            context.User = principal;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        using var document = await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
        var root = document.RootElement;
        Assert.Equal("urn:servicemantle:error:rate_limit.exceeded", root.GetProperty("type").GetString());
        Assert.Equal("Too many requests.", root.GetProperty("title").GetString());
        Assert.Equal(429, root.GetProperty("status").GetInt32());
        Assert.Equal("rate_limit.exceeded", root.GetProperty("errorCode").GetString());
        Assert.NotEmpty(root.GetProperty("correlationId").GetString()!);

        var responseProjection = string.Join('|',
            context.Response.Headers.SelectMany(header => header.Value.Append(header.Key))) +
            root.GetRawText() + string.Join('|', logs.Messages) + string.Join('|', metricTags);
        Assert.DoesNotContain(SensitiveOperator, responseProjection, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveIp, responseProjection, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveCredential, responseProjection, StringComparison.Ordinal);
        Assert.DoesNotContain("private-cookie-92", responseProjection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountersAreIsolatedPerHostInstance()
    {
        await using var first = await StartAsync(options => ConfigureFast(options.Setup, 1));
        await using var second = await StartAsync(options => ConfigureFast(options.Setup, 1));

        Assert.Equal(200, await SendAsync(first, "/setup", "192.0.2.44"));
        Assert.Equal(200, await SendAsync(second, "/setup", "192.0.2.44"));
        Assert.Equal(429, await SendAsync(first, "/setup", "192.0.2.44"));
        Assert.Equal(429, await SendAsync(second, "/setup", "192.0.2.44"));
    }

    [Fact]
    public async Task RetryAfterIsRoundedUpAndCallerCancellationStaysCancellation()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        await ServiceMantleRateLimitingPolicy.OnRejectedAsync(
            new OnRejectedContext
            {
                HttpContext = context,
                Lease = new TestLease(TimeSpan.FromMilliseconds(1_001)),
            },
            TestContext.Current.CancellationToken);
        Assert.Equal("2", context.Response.Headers.RetryAfter);

        var zeroContext = new DefaultHttpContext { RequestServices = provider };
        zeroContext.Response.Body = new MemoryStream();
        await ServiceMantleRateLimitingPolicy.OnRejectedAsync(
            new OnRejectedContext
            {
                HttpContext = zeroContext,
                Lease = new TestLease(TimeSpan.Zero),
            },
            TestContext.Current.CancellationToken);
        Assert.Equal("1", zeroContext.Response.Headers.RetryAfter);

        var canceledContext = new DefaultHttpContext { RequestServices = provider };
        canceledContext.Response.Body = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ServiceMantleRateLimitingPolicy.OnRejectedAsync(
                new OnRejectedContext
                {
                    HttpContext = canceledContext,
                    Lease = new TestLease(retryAfter: null),
                },
                cancellation.Token).AsTask());
    }

    [Fact]
    public async Task AStartedResponseIsNotRewritten()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var responseFeature = new StartedResponseFeature
        {
            StatusCode = StatusCodes.Status202Accepted,
            Headers = new HeaderDictionary { ["existing"] = "value" },
        };
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        var context = new DefaultHttpContext(features) { RequestServices = provider };

        await ServiceMantleRateLimitingPolicy.OnRejectedAsync(
            new OnRejectedContext
            {
                HttpContext = context,
                Lease = new TestLease(TimeSpan.FromSeconds(3)),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status202Accepted, responseFeature.StatusCode);
        Assert.Equal("value", responseFeature.Headers["existing"]);
        Assert.False(responseFeature.Headers.ContainsKey("Retry-After"));
        Assert.Equal(0, responseFeature.Body.Length);
    }

    private static void ConfigureInvalid(ServiceMantleRateLimitingOptions options, string scenario)
    {
        switch (scenario)
        {
            case "setup-low": options.Setup.PermitLimit = 0; break;
            case "setup-high": options.Setup.PermitLimit = 61; break;
            case "management-low": options.Management.PermitLimit = 0; break;
            case "management-high": options.Management.PermitLimit = 10_001; break;
            case "window-low": options.Setup.Window = TimeSpan.FromSeconds(9); break;
            case "window-high": options.Setup.Window = TimeSpan.FromMinutes(11); break;
            case "segments-low": options.Setup.SegmentsPerWindow = 0; break;
            case "segments-high": options.Setup.SegmentsPerWindow = 61; break;
            case "segments-window":
                options.Setup.Window = TimeSpan.FromSeconds(10);
                options.Setup.SegmentsPerWindow = 11;
                break;
        }
    }

    private static void ConfigureFast(ServiceMantleRateLimitPolicyOptions options, int permits)
    {
        options.PermitLimit = permits;
        options.Window = TimeSpan.FromSeconds(10);
        options.SegmentsPerWindow = 1;
    }

    private static ClaimsPrincipal CreatePrincipal(string operatorId) =>
        ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            operatorId,
            [ManagementPermission.Admin])
        .ToClaimsPrincipal();

    private static WebApplication Build(params Action<ServiceMantleRateLimitingOptions>[] registrations)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.0.0");
        foreach (var registration in registrations)
        {
            serviceMantle.AddRateLimiting(registration);
        }

        return builder.Build();
    }

    private static async Task<WebApplication> StartAsync(
        Action<ServiceMantleRateLimitingOptions> configure,
        CapturingLoggerProvider? loggerProvider = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        builder.Services.AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.0.0")
            .AddRateLimiting(configure);
        var app = builder.Build();
        app.UseServiceMantleCorrelationId();
        app.UseRouting();
        app.UseRateLimiter();
        app.MapGet("/setup", () => Results.Ok())
            .RequireRateLimiting(ServiceMantleRateLimitingDefaults.SetupPolicyName);
        app.MapGet("/management", () => Results.Ok())
            .RequireRateLimiting(ServiceMantleRateLimitingDefaults.ManagementPolicyName);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<int> SendAsync(
        WebApplication app,
        string path,
        string? remoteIp,
        ClaimsPrincipal? principal = null)
    {
        var context = await app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);
            context.Request.Path = path;
            if (principal is not null)
            {
                context.User = principal;
            }
        }, TestContext.Current.CancellationToken);
        return context.Response.StatusCode;
    }

    private static MeterListener CreateRateLimitingMeterListener(
        ConcurrentQueue<string> capturedTags)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(
                    instrument.Meter.Name,
                    "Microsoft.AspNetCore.RateLimiting",
                    StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Capture(tags, capturedTags));
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) => Capture(tags, capturedTags));
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Capture(tags, capturedTags));
        return listener;
    }

    private static void Capture(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        ConcurrentQueue<string> capturedTags)
    {
        foreach (var tag in tags)
        {
            capturedTags.Enqueue($"{tag.Key}={tag.Value}");
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();
        internal IReadOnlyCollection<string> Messages => messages;
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class TestLease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;
        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is not null &&
                string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal))
            {
                metadata = retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; }
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
