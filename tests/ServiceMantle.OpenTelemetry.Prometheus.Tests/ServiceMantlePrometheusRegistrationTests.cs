using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using ServiceMantle.OpenTelemetry.Prometheus;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Prometheus.Tests;

public sealed class ServiceMantlePrometheusRegistrationTests
{
    private const string PolicyName = "metrics.read";
    private const string MeterName = "ServiceMantle.Prometheus.Tests";

    [Fact]
    public async Task Disabled_by_default_registers_no_provider_or_route()
    {
        await using var application = CreateApplication(options => { }, addAuthorization: false);
        application.MapServiceMantlePrometheusEndpoint();

        await application.StartAsync(TestContext.Current.CancellationToken);
        using var client = application.GetTestClient();
        using var response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(application.Services.GetService<MeterProvider>());
    }

    [Fact]
    public async Task Mapping_without_registration_fails_safely()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var application = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            application.MapServiceMantlePrometheusEndpoint());

        Assert.DoesNotContain("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
        await application.DisposeAsync();
    }

    [Fact]
    public async Task Enabled_registration_requires_policy_name()
    {
        await using var application = CreateApplication(options => options.Enabled = true);
        application.MapServiceMantlePrometheusEndpoint();

        var exception = await Assert.ThrowsAsync<ServiceMantlePrometheusConfigurationException>(() =>
            application.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            WellKnownServiceMantlePrometheusErrorCodes.AuthorizationPolicyRequired,
            exception.ErrorCode);
        AssertSafe(exception);
    }

    public static TheoryData<string> InvalidPaths => new()
    {
        "",
        "/",
        "metrics",
        "/metrics/",
        "/one/two",
        "/metric?query",
        "/metric#fragment",
        "/metric%3Fquery",
        "/metric%23fragment",
        "/metric..backup",
        "/metric%2fother",
        "/metric%252fother",
        "/metric%5Cother",
        "/metric%255Cother",
        "/%2e%2e",
        "/%252e%252e",
        "/metric%",
        "/metric%25",
        "/{metric}",
        "/metric%257Bvalue%257D",
        "/metric\\other",
        "/metric other",
    };

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public async Task Invalid_path_fails_when_host_starts(string path)
    {
        await using var application = CreateApplication(options =>
        {
            options.Enabled = true;
            options.EndpointPath = path;
            options.AuthorizationPolicyName = PolicyName;
        });
        application.MapServiceMantlePrometheusEndpoint();

        var exception = await Assert.ThrowsAsync<ServiceMantlePrometheusConfigurationException>(() =>
            application.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownServiceMantlePrometheusErrorCodes.InvalidEndpointPath, exception.ErrorCode);
        AssertSafe(exception, path);
    }

    [Fact]
    public async Task Management_prefix_conflict_fails_when_host_starts()
    {
        await using var application = CreateApplication(options =>
        {
            options.Enabled = true;
            options.EndpointPath = "/MANAGEMENT";
            options.AuthorizationPolicyName = PolicyName;
        });
        application.MapServiceMantlePrometheusEndpoint();

        var exception = await Assert.ThrowsAsync<ServiceMantlePrometheusConfigurationException>(() =>
            application.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownServiceMantlePrometheusErrorCodes.EndpointPathConflict, exception.ErrorCode);
        AssertSafe(exception, "/MANAGEMENT");
    }

    [Fact]
    public async Task Encoded_management_prefix_conflict_fails_when_host_starts()
    {
        await using var application = CreateApplication(options =>
        {
            options.Enabled = true;
            options.EndpointPath = "/%6danagement";
            options.AuthorizationPolicyName = PolicyName;
        });
        application.MapServiceMantlePrometheusEndpoint();

        var exception = await Assert.ThrowsAsync<ServiceMantlePrometheusConfigurationException>(() =>
            application.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownServiceMantlePrometheusErrorCodes.EndpointPathConflict, exception.ErrorCode);
        AssertSafe(exception, "/%6danagement");
    }

    [Fact]
    public async Task Missing_named_policy_fails_when_host_starts()
    {
        const string sensitivePolicyName = "metrics-sensitive-policy";
        await using var application = CreateApplication(options =>
        {
            options.Enabled = true;
            options.AuthorizationPolicyName = sensitivePolicyName;
        }, registerPolicy: false);
        application.MapServiceMantlePrometheusEndpoint();

        var exception = await Assert.ThrowsAsync<ServiceMantlePrometheusConfigurationException>(() =>
            application.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            WellKnownServiceMantlePrometheusErrorCodes.AuthorizationPolicyNotFound,
            exception.ErrorCode);
        AssertSafe(exception, sensitivePolicyName);
    }

    [Fact]
    public async Task Enabled_endpoint_must_be_mapped_exactly_once_without_route_collision()
    {
        await using (var missing = CreateApplication(Enable))
        {
            var exception = await Assert.ThrowsAsync<ServiceMantlePrometheusConfigurationException>(() =>
                missing.StartAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                WellKnownServiceMantlePrometheusErrorCodes.EndpointMappingRequired,
                exception.ErrorCode);
        }

        await using (var duplicate = CreateApplication(Enable))
        {
            duplicate.MapServiceMantlePrometheusEndpoint();
            duplicate.MapServiceMantlePrometheusEndpoint();
            var exception = await Assert.ThrowsAsync<ServiceMantlePrometheusConfigurationException>(() =>
                duplicate.StartAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                WellKnownServiceMantlePrometheusErrorCodes.EndpointMappingRequired,
                exception.ErrorCode);
        }

        await using (var collision = CreateApplication(Enable))
        {
            collision.MapGet("/metrics", () => Results.NoContent()).AllowAnonymous();
            collision.MapServiceMantlePrometheusEndpoint();
            var exception = await Assert.ThrowsAsync<ServiceMantlePrometheusConfigurationException>(() =>
                collision.StartAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                WellKnownServiceMantlePrometheusErrorCodes.EndpointPathConflict,
                exception.ErrorCode);
        }
    }

    [Fact]
    public async Task Equivalent_registrations_are_idempotent_and_conflicts_fail_at_startup()
    {
        var duplicateBuilder = CreateBuilder();
        var duplicateServiceMantle = duplicateBuilder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"));
        RegisterAuthorization(duplicateBuilder.Services);
        duplicateServiceMantle.AddOpenTelemetryPrometheusEndpoint(options =>
        {
            options.Enabled = true;
            options.EndpointPath = "/prometheus";
            options.AuthorizationPolicyName = $" {PolicyName} ";
        });
        duplicateServiceMantle.AddOpenTelemetryPrometheusEndpoint(options =>
        {
            options.AuthorizationPolicyName = PolicyName;
            options.EndpointPath = "/prometheus";
            options.Enabled = true;
        });
        await using (var duplicate = duplicateBuilder.Build())
        {
            duplicate.UseAuthentication();
            duplicate.UseAuthorization();
            duplicate.MapServiceMantlePrometheusEndpoint();
            await duplicate.StartAsync(TestContext.Current.CancellationToken);
            Assert.Single(duplicate.Services.GetServices<MeterProvider>());
        }

        var conflictBuilder = CreateBuilder();
        var conflictServiceMantle = conflictBuilder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"));
        RegisterAuthorization(conflictBuilder.Services);
        conflictServiceMantle.AddOpenTelemetryPrometheusEndpoint(Enable);
        conflictServiceMantle.AddOpenTelemetryPrometheusEndpoint(options =>
        {
            options.Enabled = true;
            options.EndpointPath = "/other";
            options.AuthorizationPolicyName = PolicyName;
        });
        await using var conflict = conflictBuilder.Build();
        conflict.UseAuthentication();
        conflict.UseAuthorization();
        conflict.MapServiceMantlePrometheusEndpoint();

        var conflictException =
            await Assert.ThrowsAsync<ServiceMantlePrometheusConfigurationException>(() =>
                conflict.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            WellKnownServiceMantlePrometheusErrorCodes.ConflictingRegistration,
            conflictException.ErrorCode);
    }

    [Fact]
    public async Task Authorization_get_head_and_method_contract_are_enforced()
    {
        using var meter = new Meter(MeterName);
        var counter = meter.CreateCounter<long>("servicemantle_prometheus_requests");
        await using var application = CreateApplication(Enable, configureMetrics: metrics =>
            metrics.AddMeter(MeterName));
        application.MapServiceMantlePrometheusEndpoint();
        await application.StartAsync(TestContext.Current.CancellationToken);
        counter.Add(7);
        using var client = application.GetTestClient();

        using var unauthenticated = await client.GetAsync(
            "/metrics",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var forbiddenRequest = AuthorizedRequest(HttpMethod.Get, "forbidden");
        using var forbidden = await client.SendAsync(
            forbiddenRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var getRequest = AuthorizedRequest(HttpMethod.Get, "allowed");
        using var get = await client.SendAsync(getRequest, TestContext.Current.CancellationToken);
        var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.StartsWith("text/plain", get.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Contains("servicemantle_prometheus_requests_total{} 7", body, StringComparison.Ordinal);

        using var headRequest = AuthorizedRequest(HttpMethod.Head, "allowed");
        using var head = await client.SendAsync(headRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.StartsWith("text/plain", head.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        using var postRequest = AuthorizedRequest(HttpMethod.Post, "allowed");
        using var post = await client.SendAsync(postRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);

        using var suffixRequest = AuthorizedRequest(HttpMethod.Get, "allowed");
        suffixRequest.RequestUri = new Uri("/metrics/other", UriKind.Relative);
        using var suffix = await client.SendAsync(suffixRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, suffix.StatusCode);
    }

    [Fact]
    public async Task Endpoint_adds_no_forbidden_labels_or_request_values()
    {
        const string secretHeader = "request-secret-value";
        using var meter = new Meter(MeterName);
        var counter = meter.CreateCounter<long>("servicemantle_safe_metric");
        await using var application = CreateApplication(Enable, configureMetrics: metrics =>
            metrics.AddMeter(MeterName));
        application.MapServiceMantlePrometheusEndpoint();
        await application.StartAsync(TestContext.Current.CancellationToken);
        counter.Add(1);
        using var client = application.GetTestClient();
        using var request = AuthorizedRequest(HttpMethod.Get, "allowed");
        request.Headers.Add("X-Request-Secret", secretHeader);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("servicemantle_safe_metric_total{} 1", body, StringComparison.Ordinal);
        Assert.DoesNotContain(secretHeader, body, StringComparison.Ordinal);
        Assert.DoesNotContain("http_route", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service_name", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service_instance_id", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trace_id", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("span_id", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception_message", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("otel_scope", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Oversized_exposition_returns_empty_503_without_truncation()
    {
        using var meter = new Meter(MeterName);
        var payload = new string('x', 5_000);
        _ = meter.CreateObservableGauge(
            "servicemantle_oversized_metric",
            () => Enumerable.Range(0, 1_000).Select(index =>
                new Measurement<long>(index, new KeyValuePair<string, object?>(
                    "value",
                    $"{index:D4}-{payload}"))));
        await using var application = CreateApplication(Enable, configureMetrics: metrics =>
        {
            metrics.AddView(
                "servicemantle_oversized_metric",
                new MetricStreamConfiguration { CardinalityLimit = 2_000 });
            metrics.AddMeter(MeterName);
        });
        application.MapServiceMantlePrometheusEndpoint();
        await application.StartAsync(TestContext.Current.CancellationToken);
        using var client = application.GetTestClient();
        using var request = AuthorizedRequest(HttpMethod.Get, "allowed");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fifth_concurrent_scrape_is_rejected_immediately()
    {
        using var collectionEntered = new ManualResetEventSlim();
        using var releaseCollection = new ManualResetEventSlim();
        var testCancellationToken = TestContext.Current.CancellationToken;
        using var meter = new Meter(MeterName);
        _ = meter.CreateObservableGauge("servicemantle_blocked_metric", () =>
        {
            collectionEntered.Set();
            releaseCollection.Wait(TimeSpan.FromSeconds(10), testCancellationToken);
            return 1;
        });
        await using var application = CreateApplication(Enable, configureMetrics: metrics =>
            metrics.AddMeter(MeterName));
        application.MapServiceMantlePrometheusEndpoint();
        await application.StartAsync(TestContext.Current.CancellationToken);
        using var client = application.GetTestClient();

        var firstFour = Enumerable.Range(0, ServiceMantlePrometheusDefaults.MaximumConcurrentScrapes)
            .Select(_ => SendAuthorizedAsync(client))
            .ToArray();
        Assert.True(collectionEntered.Wait(TimeSpan.FromSeconds(2), testCancellationToken));
        var state = application.Services.GetRequiredService<ServiceMantlePrometheusEndpointState>();
        await WaitUntilAsync(
            () => state.AvailableScrapeSlots == 0,
            TestContext.Current.CancellationToken);

        var fifthTask = SendAuthorizedAsync(client);
        var completed = await Task.WhenAny(fifthTask, Task.Delay(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken));
        Assert.Same(fifthTask, completed);
        using var fifth = await fifthTask;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, fifth.StatusCode);
        Assert.Empty(await fifth.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        releaseCollection.Set();
        var accepted = await Task.WhenAll(firstFour);
        foreach (var response in accepted)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task Request_and_host_cancellation_propagate_and_stopping_rejects_new_scrapes()
    {
        await using var application = CreateApplication(Enable);
        application.MapServiceMantlePrometheusEndpoint();
        await application.StartAsync(TestContext.Current.CancellationToken);
        var state = application.Services.GetRequiredService<ServiceMantlePrometheusEndpointState>();
        CancellationToken observedToken = default;
        var requestGate = new ServiceMantlePrometheusScrapeGate(
            async context =>
            {
                observedToken = context.RequestAborted;
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            },
            state);
        using var requestCancellation = new CancellationTokenSource();
        var requestContext = new DefaultHttpContext
        {
            RequestAborted = requestCancellation.Token,
        };
        var requestTask = requestGate.InvokeAsync(requestContext);

        requestCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        Assert.True(observedToken.IsCancellationRequested);

        var hostCancellationObserved = false;
        var inFlightGate = new ServiceMantlePrometheusScrapeGate(
            async context =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                finally
                {
                    hostCancellationObserved = context.RequestAborted.IsCancellationRequested;
                }
            },
            state);
        var inFlightTask = inFlightGate.InvokeAsync(new DefaultHttpContext());
        application.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlightTask);
        Assert.True(hostCancellationObserved);

        var stoppingContext = new DefaultHttpContext();
        var invoked = false;
        var stoppingGate = new ServiceMantlePrometheusScrapeGate(
            _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            state);
        await stoppingGate.InvokeAsync(stoppingContext);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, stoppingContext.Response.StatusCode);
        Assert.Equal(0, stoppingContext.Response.ContentLength);
    }

    [Fact]
    public async Task Internal_failure_before_response_start_returns_empty_503()
    {
        const string secret = "scrape-internal-secret";
        await using var application = CreateApplication(Enable);
        application.MapServiceMantlePrometheusEndpoint();
        await application.StartAsync(TestContext.Current.CancellationToken);
        var state = application.Services.GetRequiredService<ServiceMantlePrometheusEndpointState>();
        var gate = new ServiceMantlePrometheusScrapeGate(
            _ => Task.FromException(new InvalidOperationException(secret)),
            state);
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        await gate.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal(0, context.Response.ContentLength);
        Assert.Null(context.Response.ContentType);
        Assert.Empty(body.ToArray());
        Assert.Equal(
            ServiceMantlePrometheusDefaults.MaximumConcurrentScrapes,
            state.AvailableScrapeSlots);
    }

    [Fact]
    public async Task Fixed_official_exporter_options_are_applied()
    {
        var builder = CreateBuilder();
        RegisterAuthorization(builder.Services);
        builder.Services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddOpenTelemetryPrometheusEndpoint(Enable);
        builder.Services.Configure<PrometheusAspNetCoreOptions>(options =>
        {
            options.ScrapeEndpointPath = "/consumer-path";
            options.MaxScrapeResponseSizeBytes =
                ServiceMantlePrometheusDefaults.MaximumResponseSizeBytes + 1;
            options.ScopeInfoEnabled = true;
            options.TargetInfoEnabled = true;
            options.ResourceConstantLabels = static _ => true;
        });
        await using var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapServiceMantlePrometheusEndpoint();
        await application.StartAsync(TestContext.Current.CancellationToken);

        var options = application.Services
            .GetRequiredService<IOptionsMonitor<PrometheusAspNetCoreOptions>>()
            .CurrentValue;
        Assert.Equal(ServiceMantlePrometheusDefaults.EndpointPath, options.ScrapeEndpointPath);
        Assert.Equal(
            ServiceMantlePrometheusDefaults.MaximumResponseSizeBytes,
            options.MaxScrapeResponseSizeBytes);
        Assert.False(options.ScopeInfoEnabled);
        Assert.False(options.TargetInfoEnabled);
        Assert.Null(options.ResourceConstantLabels);
    }

    [Fact]
    public async Task Later_post_configuration_cannot_bypass_fixed_exporter_options()
    {
        var builder = CreateBuilder();
        RegisterAuthorization(builder.Services);
        builder.Services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddOpenTelemetryPrometheusEndpoint(Enable);
        builder.Services.PostConfigure<PrometheusAspNetCoreOptions>(options =>
            options.MaxScrapeResponseSizeBytes =
                ServiceMantlePrometheusDefaults.MaximumResponseSizeBytes + 1);
        await using var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            application.MapServiceMantlePrometheusEndpoint());

        Assert.Contains(
            WellKnownServiceMantlePrometheusErrorCodes.ExporterOptionsConflict,
            exception.Failures);
    }

    private static WebApplication CreateApplication(
        Action<ServiceMantlePrometheusOptions> configure,
        bool addAuthorization = true,
        bool registerPolicy = true,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        var builder = CreateBuilder();
        if (addAuthorization)
        {
            RegisterAuthorization(builder.Services, registerPolicy);
        }

        builder.Services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddOpenTelemetryPrometheusEndpoint(configure);
        if (configureMetrics is not null)
        {
            builder.Services.AddOpenTelemetry().WithMetrics(configureMetrics);
        }

        var application = builder.Build();
        if (addAuthorization)
        {
            application.UseAuthentication();
            application.UseAuthorization();
        }

        return application;
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        return builder;
    }

    private static void RegisterAuthorization(IServiceCollection services, bool registerPolicy = true)
    {
        services
            .AddAuthentication(TestAuthenticationHandler.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.AuthenticationScheme,
                _ => { });
        services.AddAuthorization(options =>
        {
            if (registerPolicy)
            {
                options.AddPolicy(PolicyName, policy =>
                    policy.RequireAuthenticatedUser().RequireClaim("scope", "metrics"));
            }
        });
    }

    private static void Enable(ServiceMantlePrometheusOptions options)
    {
        options.Enabled = true;
        options.AuthorizationPolicyName = PolicyName;
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string identity)
    {
        var request = new HttpRequestMessage(method, "/metrics");
        request.Headers.Add(TestAuthenticationHandler.HeaderName, identity);
        return request;
    }

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(HttpClient client)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, "allowed");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(predicate());
    }

    private static void AssertSafe(Exception exception, params string?[] sensitiveValues)
    {
        foreach (var value in sensitiveValues.Where(value => !string.IsNullOrEmpty(value)))
        {
            Assert.DoesNotContain(value!, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(value!, exception.ToString(), StringComparison.Ordinal);
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string AuthenticationScheme = "Test";
        internal const string HeaderName = "X-Test-Identity";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var identity))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "scraper") };
            if (string.Equals(identity, "allowed", StringComparison.Ordinal))
            {
                claims.Add(new Claim("scope", "metrics"));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationScheme));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, AuthenticationScheme)));
        }
    }

}
