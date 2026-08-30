using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using ServiceMantle.AspNetCore;
using ServiceMantle.Http;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantleProblemDetailsTests
{
    private const string CallerCorrelationId = "caller-problem-42";
    private const string SecretMessage =
        "internal failure; Host=db.internal;Password=correct-horse; SELECT * FROM private_table";

    [Fact]
    public void PublicContract_FixesFallbackAndTypeUriShape()
    {
        Assert.Equal("urn:servicemantle:error:", ServiceMantleProblemDetailsDefaults.TypeUriPrefix);
        Assert.Equal("correlationId", ServiceMantleProblemDetailsDefaults.CorrelationIdExtensionName);
        Assert.Equal("errorCode", ServiceMantleProblemDetailsDefaults.ErrorCodeExtensionName);
        Assert.Equal(
            "http.internal_server_error",
            ServiceMantleProblemDetailsDefaults.InternalServerErrorCode);
        Assert.Equal(
            "urn:servicemantle:error:http.internal_server_error",
            ServiceMantleProblemDetailsDefaults.InternalServerErrorType);
        Assert.Equal(
            "urn:servicemantle:error:catalog.request_invalid",
            ServiceMantleProblemDetailsDefaults.CreateTypeUri("catalog.request_invalid"));

        Assert.Throws<ArgumentException>(() =>
            ServiceMantleProblemDetailsDefaults.CreateTypeUri("Catalog Invalid"));
    }

    [Fact]
    public async Task KnownAndUnknownExceptions_ExposeOnlyRegisteredOrFallbackProblemFields()
    {
        using var known = new PipelineFixture(
            terminal: _ => ThrowKnownFailure(),
            configure: builder => builder.AddExceptionMapping<KnownFailure>(
                StatusCodes.Status422UnprocessableEntity,
                "catalog.request_invalid",
                "The catalog request is invalid.",
                new Dictionary<string, Func<KnownFailure, object?>>
                {
                    ["attempt"] = exception => exception.Attempt,
                }));
        using var unknown = new PipelineFixture(
            terminal: _ => ThrowUnknownFailure());

        using var knownResult = await known.SendAsync(CallerCorrelationId);
        using var unknownResult = await unknown.SendAsync(CallerCorrelationId);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, knownResult.StatusCode);
        Assert.Equal("application/problem+json", knownResult.ContentType);
        Assert.Equal(
            new[] { "attempt", "correlationId", "errorCode", "status", "title", "type" },
            knownResult.Json.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            "urn:servicemantle:error:catalog.request_invalid",
            knownResult.Json.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "The catalog request is invalid.",
            knownResult.Json.RootElement.GetProperty("title").GetString());
        Assert.Equal(7, knownResult.Json.RootElement.GetProperty("attempt").GetInt32());
        AssertSafeBody(knownResult.Body);

        Assert.Equal(StatusCodes.Status500InternalServerError, unknownResult.StatusCode);
        Assert.Equal("application/problem+json", unknownResult.ContentType);
        Assert.Equal(
            new[] { "correlationId", "errorCode", "status", "title", "type" },
            unknownResult.Json.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ServiceMantleProblemDetailsDefaults.InternalServerErrorType,
            unknownResult.Json.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            ServiceMantleProblemDetailsDefaults.InternalServerErrorTitle,
            unknownResult.Json.RootElement.GetProperty("title").GetString());
        AssertSafeBody(unknownResult.Body);
    }

    [Fact]
    public async Task UnknownExceptionBody_IsByteIdenticalInDevelopmentAndProduction()
    {
        var development = await GetUnknownBodyFromHostAsync(Environments.Development);
        var production = await GetUnknownBodyFromHostAsync(Environments.Production);

        Assert.Equal(development, production);
    }

    [Fact]
    public async Task Registration_IsIdempotentAndConflictsFailWhenTheHostStarts()
    {
        var duplicateBuilder = Host.CreateApplicationBuilder();
        var duplicate = duplicateBuilder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "2.0.0");
        duplicate.AddExceptionMapping<KnownFailure>(
            StatusCodes.Status409Conflict,
            "catalog.conflict",
            "The catalog request conflicts.");
        duplicate.AddExceptionMapping<KnownFailure>(
            StatusCodes.Status409Conflict,
            "catalog.conflict",
            "The catalog request conflicts.");

        using (var host = duplicateBuilder.Build())
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        var conflictBuilder = Host.CreateApplicationBuilder();
        var conflicting = conflictBuilder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "2.0.0");
        conflicting.AddExceptionMapping<KnownFailure>(
            StatusCodes.Status409Conflict,
            "catalog.conflict",
            "The catalog request conflicts.");
        conflicting.AddExceptionMapping<KnownFailure>(
            StatusCodes.Status503ServiceUnavailable,
            "catalog.unavailable",
            "The catalog service is unavailable.");

        using var conflictingHost = conflictBuilder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            conflictingHost.StartAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(nameof(KnownFailure), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("TYPE")]
    [InlineData("title")]
    [InlineData("detail")]
    [InlineData("instance")]
    [InlineData("correlationId")]
    [InlineData("ERRORCODE")]
    public async Task ProtectedFields_CannotBeRegisteredAsExtensions(string fieldName)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services
            .AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "2.0.0")
            .AddExceptionMapping<KnownFailure>(
                StatusCodes.Status400BadRequest,
                "catalog.invalid",
                "The catalog request is invalid.",
                new Dictionary<string, Func<KnownFailure, object?>>
                {
                    [fieldName] = _ => "override",
                });

        using var host = builder.Build();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailingExtensionFactory_FallsBackWithoutRemovingProtectedFields()
    {
        using var fixture = new PipelineFixture(
            terminal: _ => ThrowKnownFailure(),
            configure: builder => builder.AddExceptionMapping<KnownFailure>(
                StatusCodes.Status422UnprocessableEntity,
                "catalog.request_invalid",
                "The catalog request is invalid.",
                new Dictionary<string, Func<KnownFailure, object?>>
                {
                    ["custom"] = _ => throw new InvalidOperationException(SecretMessage),
                }));

        using var result = await fixture.SendAsync(CallerCorrelationId);

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Equal(
            ServiceMantleProblemDetailsDefaults.InternalServerErrorCode,
            result.Json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(
            CallerCorrelationId,
            result.Json.RootElement.GetProperty("correlationId").GetString());
        Assert.False(result.Json.RootElement.TryGetProperty("custom", out _));
        AssertSafeBody(result.Body);
    }

    [Fact]
    public async Task ResponseCarriesTheSameCorrelationIdAsHeaderAccessorScopeAndErrorLog()
    {
        using var fixture = new PipelineFixture(terminal: _ => ThrowUnknownFailure());

        using var result = await fixture.SendAsync(CallerCorrelationId);

        Assert.Equal(CallerCorrelationId, result.CorrelationId);
        Assert.Equal(
            CallerCorrelationId,
            result.Json.RootElement.GetProperty("correlationId").GetString());
        Assert.Contains(fixture.Logs, record =>
            record.Category == "ServiceMantle.Http.ProblemDetails" &&
            record.Message.Contains(CallerCorrelationId, StringComparison.Ordinal) &&
            Equals(record.Fields[ServiceLogFieldNames.CorrelationId], CallerCorrelationId));
    }

    [Fact]
    public async Task MissingCorrelationMiddleware_GeneratesAResponseAndLogCorrelationId()
    {
        using var fixture = new PipelineFixture(
            terminal: _ => ThrowUnknownFailure(),
            useCorrelationMiddleware: false);

        using var result = await fixture.SendAsync();
        var correlationId = result.Json.RootElement.GetProperty("correlationId").GetString();

        Assert.NotNull(correlationId);
        Assert.Matches("^[0-9a-f]{32}$", correlationId);
        Assert.Equal(correlationId, result.CorrelationId);
        Assert.Contains(fixture.Logs, record =>
            record.Category == "ServiceMantle.Http.ProblemDetails" &&
            record.Message.Contains(correlationId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartedResponse_IsNotRewrittenAndTheDownstreamExceptionIsNotRethrown()
    {
        using var fixture = new PipelineFixture(
            terminal: _ => ThrowUnknownFailure(),
            useCorrelationMiddleware: false);
        var response = new TestResponseFeature
        {
            HasStarted = true,
            StatusCode = StatusCodes.Status202Accepted,
        };
        response.Headers[ServiceMantleHeaderNames.CorrelationId] = "already-sent";
        await response.Body.WriteAsync(
            "sent"u8.ToArray(),
            TestContext.Current.CancellationToken);
        var context = fixture.CreateContext(response);

        await fixture.SendAsync(context);

        Assert.Equal(StatusCodes.Status202Accepted, response.StatusCode);
        Assert.Equal("already-sent", response.Headers[ServiceMantleHeaderNames.CorrelationId]);
        Assert.Equal("sent", Encoding.UTF8.GetString(response.Body.ToArray()));
        Assert.Null(context.GetServiceMantleCorrelationId());
    }

    [Fact]
    public async Task CallerCancellationPropagatesButInternalCancellationUsesTheFallback()
    {
        using var callerCancelled = new CancellationTokenSource();
        callerCancelled.Cancel();
        using var callerFixture = new PipelineFixture(
            terminal: _ => Task.FromException(new OperationCanceledException(SecretMessage)));
        var callerResponse = new TestResponseFeature();
        var callerContext = callerFixture.CreateContextWithRequestAborted(
            callerResponse,
            CallerCorrelationId,
            callerCancelled.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            callerFixture.SendAsync(callerContext));
        Assert.Equal(StatusCodes.Status200OK, callerResponse.StatusCode);
        Assert.Empty(callerResponse.Body.ToArray());

        using var internalFixture = new PipelineFixture(
            terminal: _ => Task.FromException(new OperationCanceledException(SecretMessage)));
        using var internalResult = await internalFixture.SendAsync(CallerCorrelationId);

        Assert.Equal(StatusCodes.Status500InternalServerError, internalResult.StatusCode);
        Assert.Equal(
            ServiceMantleProblemDetailsDefaults.InternalServerErrorCode,
            internalResult.Json.RootElement.GetProperty("errorCode").GetString());
        AssertSafeBody(internalResult.Body);
    }

    [Fact]
    public async Task ConcurrentRequests_KeepMappingsAndCorrelationIdsIsolated()
    {
        using var fixture = new PipelineFixture(
            terminal: context => Task.FromException(
                new KnownFailure(SecretMessage, (int)context.Items["attempt"]!)),
            configure: builder => builder.AddExceptionMapping<KnownFailure>(
                StatusCodes.Status422UnprocessableEntity,
                "catalog.request_invalid",
                "The catalog request is invalid.",
                new Dictionary<string, Func<KnownFailure, object?>>
                {
                    ["attempt"] = exception => exception.Attempt,
                }));

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(async attempt =>
        {
            var correlationId = $"caller-{attempt:D2}";
            var response = new TestResponseFeature();
            var context = fixture.CreateContext(response, correlationId);
            context.Items["attempt"] = attempt;
            await fixture.SendAsync(context);
            await response.StartAsync();
            return new
            {
                CorrelationId = response.Headers[ServiceMantleHeaderNames.CorrelationId].Single(),
                Json = JsonDocument.Parse(response.Body.ToArray()),
            };
        }));

        Assert.Equal(
            Enumerable.Range(0, 64).Select(index => $"caller-{index:D2}").Order(),
            results.Select(result => result.CorrelationId).Order());
        Assert.Equal(
            Enumerable.Range(0, 64),
            results.Select(result => result.Json.RootElement.GetProperty("attempt").GetInt32()).Order());
        Assert.All(results, result => Assert.Equal(
            result.CorrelationId,
            result.Json.RootElement.GetProperty("correlationId").GetString()));
        foreach (var result in results)
        {
            result.Json.Dispose();
        }
    }

    [Fact]
    public void MiddlewareRequiresAddServiceMantleRegistration()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();

        Assert.Throws<ArgumentNullException>(() =>
            ServiceMantleProblemDetailsApplicationBuilderExtensions
                .UseServiceMantleProblemDetails(null!));
        Assert.Throws<InvalidOperationException>(() =>
            new ApplicationBuilder(provider).UseServiceMantleProblemDetails());
    }

    private static Task ThrowKnownFailure()
    {
        var exception = new KnownFailure(SecretMessage, 7, new Exception("inner secret"));
        exception.Data["ConnectionString"] = "Host=db.internal;Password=correct-horse";
        throw exception;
    }

    private static Task ThrowUnknownFailure()
    {
        var exception = new InvalidOperationException(
            SecretMessage,
            new Exception("inner secret"));
        exception.Data["Sql"] = "SELECT * FROM private_table";
        throw exception;
    }

    private static void AssertSafeBody(string body)
    {
        Assert.DoesNotContain(SecretMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-horse", body, StringComparison.Ordinal);
        Assert.DoesNotContain("db.internal", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT *", body, StringComparison.Ordinal);
        Assert.DoesNotContain("inner secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ServiceMantle", body, StringComparison.Ordinal);
    }

    private static async Task<byte[]> GetUnknownBodyFromHostAsync(string environmentName)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "2.0.0");

        await using var app = builder.Build();
        app.UseServiceMantleCorrelationId();
        app.UseServiceMantleProblemDetails();
        app.Run(_ => ThrowUnknownFailure());
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single());
            request.Headers.TryAddWithoutValidation(
                ServiceMantleHeaderNames.CorrelationId,
                CallerCorrelationId);
            using var response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);

            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            return await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class KnownFailure : Exception
    {
        internal KnownFailure(
            string message,
            int attempt,
            Exception? innerException = null)
            : base(message, innerException)
        {
            Attempt = attempt;
        }

        internal int Attempt { get; }
    }

    private sealed class PipelineFixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly RequestDelegate pipeline;
        private readonly CapturingLoggerProvider loggerProvider = new();

        internal PipelineFixture(
            RequestDelegate terminal,
            Action<ServiceMantleBuilder>? configure = null,
            bool useCorrelationMiddleware = true)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(loggerProvider));
            var serviceMantle = services.AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "2.0.0");
            configure?.Invoke(serviceMantle);
            provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            var app = new ApplicationBuilder(provider);
            if (useCorrelationMiddleware)
            {
                app.UseServiceMantleCorrelationId();
            }

            app.UseServiceMantleProblemDetails();
            app.Run(terminal);
            pipeline = app.Build();
        }

        internal IReadOnlyCollection<LogRecord> Logs => loggerProvider.Records;

        internal DefaultHttpContext CreateContext(
            TestResponseFeature response,
            string? correlationId = null) =>
            CreateContextWithRequestAborted(response, correlationId, CancellationToken.None);

        internal DefaultHttpContext CreateContextWithRequestAborted(
            TestResponseFeature response,
            string? correlationId,
            CancellationToken requestAborted)
        {
            var requestHeaders = new HeaderDictionary();
            if (correlationId is not null)
            {
                requestHeaders[ServiceMantleHeaderNames.CorrelationId] = correlationId;
            }

            var request = new HttpRequestFeature
            {
                Method = HttpMethods.Get,
                Path = "/",
                Protocol = "HTTP/1.1",
                Scheme = "http",
                Headers = requestHeaders,
            };
            var requestLifetime = new HttpRequestLifetimeFeature
            {
                RequestAborted = requestAborted,
            };
            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(request);
            features.Set<IHttpResponseFeature>(response);
            features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(response.Body));
            features.Set<IHttpRequestLifetimeFeature>(requestLifetime);
            return new DefaultHttpContext(features) { RequestServices = provider };
        }

        internal async Task<ProblemResult> SendAsync(string? correlationId = null)
        {
            var response = new TestResponseFeature();
            var context = CreateContext(response, correlationId);
            await SendAsync(context);
            await response.StartAsync();
            var body = Encoding.UTF8.GetString(response.Body.ToArray());
            return new ProblemResult(
                response.StatusCode,
                response.Headers.ContentType.Single()!,
                response.Headers[ServiceMantleHeaderNames.CorrelationId].Single()!,
                body,
                JsonDocument.Parse(body));
        }

        internal Task SendAsync(HttpContext context) => pipeline(context);

        public void Dispose()
        {
            provider.Dispose();
            loggerProvider.Dispose();
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

        internal ConcurrentQueue<LogRecord> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            CapturingLoggerProvider provider,
            string category) : ILogger
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
                var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                provider.scopes.ForEachScope((scope, target) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> structured)
                    {
                        foreach (var field in structured)
                        {
                            target[field.Key] = field.Value;
                        }
                    }
                }, fields);
                provider.Records.Enqueue(new LogRecord(category, formatter(state, exception), fields));
            }
        }
    }

    private sealed record LogRecord(
        string Category,
        string Message,
        IReadOnlyDictionary<string, object?> Fields);

    private sealed record ProblemResult(
        int StatusCode,
        string ContentType,
        string CorrelationId,
        string Body,
        JsonDocument Json) : IDisposable
    {
        public void Dispose() => Json.Dispose();
    }

    private sealed class TestResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> startingCallbacks = [];

        public MemoryStream Body { get; set; } = new();

        Stream IHttpResponseFeature.Body
        {
            get => Body;
            set => Body = (MemoryStream)value;
        }

        public bool HasStarted { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public string? ReasonPhrase { get; set; }

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

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
            for (var index = startingCallbacks.Count - 1; index >= 0; index--)
            {
                var (callback, state) = startingCallbacks[index];
                await callback(state);
            }
        }
    }
}
