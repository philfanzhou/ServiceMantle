using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using ServiceMantle.Http;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantleCorrelationIdTests
{
    private const string GeneratedShape = "^[0-9a-f]{32}$";
    private const string MiddlewareLoggerCategory = "ServiceMantle.Http.CorrelationId";

    [Fact]
    public void PublicContract_FixesHeaderNameFieldNameAndAccessorSemantics()
    {
        Assert.Equal("x-correlation-id", ServiceMantleHeaderNames.CorrelationId);
        Assert.Equal("CorrelationId", ServiceLogFieldNames.CorrelationId);

        var context = new DefaultHttpContext();
        Assert.Null(context.GetServiceMantleCorrelationId());
        Assert.False(context.TryGetServiceMantleCorrelationId(out var correlationId));
        Assert.Null(correlationId);
        Assert.Throws<ArgumentNullException>(() =>
            ServiceMantleCorrelationIdHttpContextExtensions.GetServiceMantleCorrelationId(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ServiceMantleCorrelationIdHttpContextExtensions.TryGetServiceMantleCorrelationId(
                null!,
                out _));
    }

    [Fact]
    public void BeginScope_CannotOverrideAnyProtectedIdentityField()
    {
        using var factory = new TestLoggerFactory();
        var logContext = new ServiceLogContext(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            "2.0.0");
        var logger = factory.CreateLogger("test");

        foreach (var protectedField in new[]
        {
            ServiceLogFieldNames.ServiceName,
            ServiceLogFieldNames.ServiceVersion,
            ServiceLogFieldNames.InstanceId,
            ServiceLogFieldNames.CorrelationId,
            "correlationid",
            "CORRELATIONID",
        })
        {
            Assert.Throws<ArgumentException>(() => logContext.BeginScope(
                logger,
                [new(protectedField, "override")]));
        }
    }

    [Fact]
    public async Task ValidCallerValue_IsReusedVerbatimInSlotScopeAndResponse()
    {
        const string callerValue = "Order-42.retry_1";
        using var fixture = new PipelineFixture();
        var response = new TestResponseFeature();
        var context = fixture.CreateContext(response, callerValue);

        await fixture.SendAsync(context);
        await response.StartAsync();

        Assert.Equal(callerValue, context.GetServiceMantleCorrelationId());
        Assert.True(context.TryGetServiceMantleCorrelationId(out var resolved));
        Assert.Equal(callerValue, resolved);
        Assert.Equal(
            callerValue,
            Assert.Single(fixture.Factory.Records).Fields[ServiceLogFieldNames.CorrelationId]);
        Assert.Equal(
            callerValue,
            Assert.Single(response.Headers[ServiceMantleHeaderNames.CorrelationId]));
        Assert.Equal(callerValue, context.Request.Headers[ServiceMantleHeaderNames.CorrelationId]);
    }

    [Fact]
    public async Task RequestScope_CarriesAllFourProtectedFields()
    {
        using var fixture = new PipelineFixture();

        await fixture.SendAsync(fixture.CreateContext(new TestResponseFeature(), "caller-1"));

        var record = Assert.Single(fixture.Factory.Records);
        Assert.Equal("catalog", record.Fields[ServiceLogFieldNames.ServiceName]);
        Assert.Equal("2.0.0", record.Fields[ServiceLogFieldNames.ServiceVersion]);
        Assert.Equal("catalog-01", record.Fields[ServiceLogFieldNames.InstanceId]);
        Assert.Equal("caller-1", record.Fields[ServiceLogFieldNames.CorrelationId]);
        Assert.Equal(4, record.Fields.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("too-long")]
    [InlineData("leading-punctuation")]
    [InlineData("illegal-character")]
    [InlineData("comma-joined")]
    [InlineData("repeated-identical")]
    [InlineData("repeated-conflicting")]
    public async Task RejectedHeader_GeneratesANewValueAndNeverEchoesTheRawInput(string shape)
    {
        var rawValues = RejectedHeaderValues(shape);
        using var fixture = new PipelineFixture();
        var response = new TestResponseFeature();
        var context = fixture.CreateContext(response, rawValues);

        await fixture.SendAsync(context);
        await response.StartAsync();

        var resolved = context.GetServiceMantleCorrelationId();
        Assert.NotNull(resolved);
        Assert.Matches(GeneratedShape, resolved);

        var record = Assert.Single(fixture.Factory.Records);
        var scopeText = string.Join(";", record.Fields.Select(field => $"{field.Key}={field.Value}"));
        var responseValue = Assert.Single(response.Headers[ServiceMantleHeaderNames.CorrelationId]);

        Assert.Equal(resolved, record.Fields[ServiceLogFieldNames.CorrelationId]);
        Assert.Equal(resolved, responseValue);
        foreach (var rawValue in rawValues.Where(value => value.Length > 0))
        {
            Assert.DoesNotContain(rawValue, resolved, StringComparison.Ordinal);
            Assert.DoesNotContain(rawValue, scopeText, StringComparison.Ordinal);
            Assert.DoesNotContain(rawValue, responseValue!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GeneratedValues_AlwaysMatchTheFixedShapeAndAreRequestScoped()
    {
        using var fixture = new PipelineFixture();
        var generated = new List<string>();

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var context = fixture.CreateContext(new TestResponseFeature());
            await fixture.SendAsync(context);
            generated.Add(context.GetServiceMantleCorrelationId()!);
        }

        Assert.All(generated, value => Assert.Matches(GeneratedShape, value));
        Assert.Equal(generated.Count, generated.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task BoundaryLengths_AreAcceptedAtSixtyFourAndRejectedAtSixtyFive()
    {
        using var fixture = new PipelineFixture();
        var accepted = new string('a', 64);
        var acceptedContext = fixture.CreateContext(new TestResponseFeature(), accepted);
        var rejectedContext = fixture.CreateContext(new TestResponseFeature(), new string('a', 65));

        await fixture.SendAsync(acceptedContext);
        await fixture.SendAsync(rejectedContext);

        Assert.Equal(accepted, acceptedContext.GetServiceMantleCorrelationId());
        Assert.Matches(GeneratedShape, rejectedContext.GetServiceMantleCorrelationId()!);
    }

    [Fact]
    public async Task ResponseHeader_CollapsesToASingleAssignedValue()
    {
        using var fixture = new PipelineFixture(terminal: context =>
        {
            context.Response.Headers.Append(ServiceMantleHeaderNames.CorrelationId, "downstream-a");
            context.Response.Headers.Append(ServiceMantleHeaderNames.CorrelationId, "downstream-b");
            return Task.CompletedTask;
        });
        var response = new TestResponseFeature();
        var context = fixture.CreateContext(response, "caller-1");

        await fixture.SendAsync(context);
        await response.StartAsync();

        Assert.Equal(1, response.RegisteredStartingCallbacks);
        Assert.Equal(
            "caller-1",
            Assert.Single(response.Headers[ServiceMantleHeaderNames.CorrelationId]));
    }

    [Fact]
    public async Task AlreadyStartedResponse_StillEstablishesContextWithoutRewritingSentHeaders()
    {
        using var fixture = new PipelineFixture();
        var response = new TestResponseFeature { HasStarted = true };
        response.Headers[ServiceMantleHeaderNames.CorrelationId] = "already-sent";
        var context = fixture.CreateContext(response, "caller-1");

        await fixture.SendAsync(context);

        Assert.Equal(0, response.RegisteredStartingCallbacks);
        Assert.Equal("already-sent", response.Headers[ServiceMantleHeaderNames.CorrelationId]);
        Assert.Equal("caller-1", context.GetServiceMantleCorrelationId());
        Assert.Equal(
            "caller-1",
            Assert.Single(fixture.Factory.Records).Fields[ServiceLogFieldNames.CorrelationId]);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("throws")]
    [InlineData("cancelled")]
    public async Task RequestScope_IsReleasedForEveryDownstreamOutcome(string outcome)
    {
        using var fixture = new PipelineFixture(terminal: _ => outcome switch
        {
            "throws" => Task.FromException(new InvalidOperationException("downstream")),
            "cancelled" => Task.FromException(new OperationCanceledException()),
            _ => Task.CompletedTask,
        });
        var context = fixture.CreateContext(new TestResponseFeature(), "caller-1");

        var send = async () => await fixture.SendAsync(context);
        switch (outcome)
        {
            case "throws":
                await Assert.ThrowsAsync<InvalidOperationException>(send);
                break;
            case "cancelled":
                await Assert.ThrowsAsync<OperationCanceledException>(send);
                break;
            default:
                await send();
                break;
        }

        Assert.Equal(1, fixture.Factory.StartedScopes);
        Assert.Equal(1, fixture.Factory.DisposedScopes);
        Assert.DoesNotContain(
            fixture.Factory.Records,
            record => record.Category == MiddlewareLoggerCategory);
    }

    [Fact]
    public async Task ConcurrentRequests_KeepCorrelationIdsIsolatedAndReleaseEveryScope()
    {
        using var fixture = new PipelineFixture();
        var expected = Enumerable.Range(0, 64).Select(index => $"caller-{index:D2}").ToArray();

        var requests = expected.Select(callerValue => Task.Run(
            async () =>
            {
                var response = new TestResponseFeature();
                var context = fixture.CreateContext(response, callerValue);
                await fixture.SendAsync(context);
                await response.StartAsync();
                Assert.Equal(callerValue, context.GetServiceMantleCorrelationId());
                Assert.Equal(
                    callerValue,
                    Assert.Single(response.Headers[ServiceMantleHeaderNames.CorrelationId]));
            },
            TestContext.Current.CancellationToken));

        await Task.WhenAll(requests);

        Assert.Equal(64, fixture.Factory.StartedScopes);
        Assert.Equal(64, fixture.Factory.DisposedScopes);
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            fixture.Factory.Records
                .Select(record => (string)record.Fields[ServiceLogFieldNames.CorrelationId]!)
                .Order(StringComparer.Ordinal));
        Assert.All(fixture.Factory.Records, record => Assert.Equal(4, record.Fields.Count));
    }

    [Fact]
    public void UseServiceMantleCorrelationId_RequiresRegisteredHostIdentity()
    {
        using var factory = new TestLoggerFactory();
        using var provider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(factory)
            .BuildServiceProvider();

        Assert.Throws<ArgumentNullException>(() =>
            ServiceMantleCorrelationIdApplicationBuilderExtensions.UseServiceMantleCorrelationId(
                null!));
        Assert.Throws<InvalidOperationException>(() =>
            new ApplicationBuilder(provider).UseServiceMantleCorrelationId());
    }

    [Fact]
    public void UseServiceMantleCorrelationId_RejectsManuallyRegisteredServiceMantleServices()
    {
        // Every service AddServiceMantle registers is public, so a consumer can register any of
        // them directly. Doing so must not satisfy the guard: only AddServiceMantle establishes the
        // host identity the middleware documents as its precondition.
        using var factory = new TestLoggerFactory();
        var serviceId = ServiceId.Parse("signacore");
        var instanceId = InstanceId.Parse("catalog-01");
        using var provider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(factory)
            .AddSingleton(serviceId)
            .AddSingleton(instanceId)
            .AddSingleton(new ServiceLogContext(serviceId, instanceId, "1.2.3"))
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<ServiceLogContext>());
        Assert.Throws<InvalidOperationException>(() =>
            new ApplicationBuilder(provider).UseServiceMantleCorrelationId());
    }

    [Fact]
    public void UseServiceMantleCorrelationId_AcceptsAServiceCollectionConfiguredByAddServiceMantle()
    {
        using var factory = new TestLoggerFactory();
        var services = new ServiceCollection().AddSingleton<ILoggerFactory>(factory);
        services.AddServiceMantle(
            ServiceId.Parse("signacore"),
            InstanceId.Parse("catalog-01"),
            Path.Combine(Path.GetTempPath(), $"servicemantle-{Guid.NewGuid():N}.json"),
            "1.2.3");

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider,
            new ApplicationBuilder(provider).UseServiceMantleCorrelationId().ApplicationServices);
    }

    [Fact]
    public async Task MinimalHost_RoundTripsTheCorrelationIdOnlyWhenTheMiddlewareIsUsed()
    {
        await using var enabled = await StartHostAsync(useMiddleware: true);
        await using var disabled = await StartHostAsync(useMiddleware: false);
        using var client = new HttpClient();

        using var callerRequest = new HttpRequestMessage(HttpMethod.Get, enabled.Urls.First());
        callerRequest.Headers.TryAddWithoutValidation(
            ServiceMantleHeaderNames.CorrelationId,
            "caller-round-trip");
        using var callerResponse = await client.SendAsync(
            callerRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "caller-round-trip",
            Assert.Single(callerResponse.Headers.GetValues(ServiceMantleHeaderNames.CorrelationId)));
        Assert.Equal(
            "caller-round-trip",
            await callerResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var generatedResponse = await client.GetAsync(
            enabled.Urls.First(),
            TestContext.Current.CancellationToken);
        var generated = Assert.Single(
            generatedResponse.Headers.GetValues(ServiceMantleHeaderNames.CorrelationId));

        Assert.Matches(GeneratedShape, generated);
        Assert.Equal(
            generated,
            await generatedResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var withoutMiddleware = await client.GetAsync(
            disabled.Urls.First(),
            TestContext.Current.CancellationToken);

        Assert.False(withoutMiddleware.Headers.Contains(ServiceMantleHeaderNames.CorrelationId));
        Assert.Equal(
            "none",
            await withoutMiddleware.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        await enabled.StopAsync(TestContext.Current.CancellationToken);
        await disabled.StopAsync(TestContext.Current.CancellationToken);
    }

    private static string[] RejectedHeaderValues(string shape) => shape switch
    {
        "missing" => [],
        "empty" => [""],
        "whitespace" => ["   "],
        "too-long" => [new string('a', 65)],
        "leading-punctuation" => ["-leading"],
        "illegal-character" => ["caller value"],
        "comma-joined" => ["caller-1,caller-2"],
        "repeated-identical" => ["caller-1", "caller-1"],
        "repeated-conflicting" => ["caller-1", "caller-2"],
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static async Task<WebApplication> StartHostAsync(bool useMiddleware)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "2.0.0");

        var app = builder.Build();
        if (useMiddleware)
        {
            app.UseServiceMantleCorrelationId();
        }

        app.Run(context => context.Response.WriteAsync(
            context.GetServiceMantleCorrelationId() ?? "none"));

        await app.StartAsync();
        return app;
    }

    private sealed class PipelineFixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly RequestDelegate pipeline;

        internal PipelineFixture(RequestDelegate? terminal = null)
        {
            Factory = new TestLoggerFactory();
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(Factory);
            services.AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "2.0.0");
            provider = services.BuildServiceProvider();

            var app = new ApplicationBuilder(provider);
            app.UseServiceMantleCorrelationId();
            app.Run(terminal ?? DefaultTerminal);
            pipeline = app.Build();
        }

        internal TestLoggerFactory Factory { get; }

        internal DefaultHttpContext CreateContext(
            TestResponseFeature response,
            params string[] headerValues)
        {
            var store = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
            if (headerValues.Length > 0)
            {
                // The raw store is used directly so that an empty header value survives; the
                // IHeaderDictionary indexer would drop it before the middleware ever sees it.
                store[ServiceMantleHeaderNames.CorrelationId] = new StringValues(headerValues);
            }

            var request = new HttpRequestFeature
            {
                Method = HttpMethods.Get,
                Path = "/",
                Protocol = "HTTP/1.1",
                Scheme = "http",
                Headers = new HeaderDictionary(store),
            };

            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(request);
            features.Set<IHttpResponseFeature>(response);
            return new DefaultHttpContext(features) { RequestServices = provider };
        }

        internal Task SendAsync(HttpContext context) => pipeline(context);

        public void Dispose()
        {
            provider.Dispose();
            Factory.Dispose();
        }

        private static Task DefaultTerminal(HttpContext context)
        {
            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("downstream")
                .LogInformation("handled");
            return Task.CompletedTask;
        }
    }

    private sealed class TestLoggerFactory : ILoggerFactory
    {
        private readonly LoggerExternalScopeProvider scopes = new();
        private int startedScopes;
        private int disposedScopes;

        internal ConcurrentQueue<LogRecord> Records { get; } = new();

        internal int StartedScopes => Volatile.Read(ref startedScopes);

        internal int DisposedScopes => Volatile.Read(ref disposedScopes);

        public ILogger CreateLogger(string categoryName) => new TestLogger(this, categoryName);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private IDisposable BeginScope(object? state)
        {
            Interlocked.Increment(ref startedScopes);
            return new TrackedScope(this, scopes.Push(state));
        }

        private void Capture(string category) =>
            Records.Enqueue(new LogRecord(category, CollectFields()));

        private Dictionary<string, object?> CollectFields()
        {
            var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            scopes.ForEachScope(
                (scope, target) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> structuredScope)
                    {
                        foreach (var field in structuredScope)
                        {
                            target[field.Key] = field.Value;
                        }
                    }
                },
                fields);
            return fields;
        }

        private sealed class TrackedScope(TestLoggerFactory factory, IDisposable inner) : IDisposable
        {
            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    Interlocked.Increment(ref factory.disposedScopes);
                }

                inner.Dispose();
            }
        }

        private sealed class TestLogger(TestLoggerFactory factory, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => factory.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => factory.Capture(category);
        }
    }

    private sealed record LogRecord(string Category, IReadOnlyDictionary<string, object?> Fields);

    private sealed class TestResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> startingCallbacks = [];

        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public string? ReasonPhrase { get; set; }

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        internal int RegisteredStartingCallbacks => startingCallbacks.Count;

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public void OnStarting(Func<object, Task> callback, object state)
        {
            if (HasStarted)
            {
                throw new InvalidOperationException("The response has already started.");
            }

            startingCallbacks.Add((callback, state));
        }

        internal async Task StartAsync()
        {
            HasStarted = true;
            foreach (var (callback, state) in startingCallbacks)
            {
                await callback(state);
            }
        }
    }
}
