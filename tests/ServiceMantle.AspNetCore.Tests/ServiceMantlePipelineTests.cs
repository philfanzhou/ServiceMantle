using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Audit;
using ServiceMantle.Health;
using ServiceMantle.Http;
using ServiceMantle.Installation;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantlePipelineTests
{
    private const string Secret = "private-header-secret";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/ok", 200, null)]
    [InlineData("/no-content", 204, null)]
    [InlineData("/validation", 422, "test.invalid")]
    [InlineData("/exception", 500, "http.internal_server_error")]
    [InlineData("/internal-cancel", 500, "http.internal_server_error")]
    [InlineData("/unauthorized", 401, "management.session.unauthenticated")]
    [InlineData("/forbidden", 403, "management.session.forbidden")]
    [InlineData("/limited", 429, "rate_limit.exceeded")]
    public async Task Response_matrix_keeps_headers_correlation_and_safe_diagnostics(string path, int status, string? error)
    {
        var recorder = new Recorder();
        await using var app = Build(recorder);
        await app.StartAsync(Token);
        if (path == "/limited") await Send(app, path);
        var cookie = path == "/forbidden" ? Cookie(app, "reader", ManagementPermission.Read) : null;
        using var response = await Send(app, path, cookie: cookie);
        Assert.Equal(status, (int)response.StatusCode);
        AssertHeaders(response);
        Assert.Equal("pipeline-request", Assert.Single(response.Headers.GetValues("x-correlation-id")));
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        if (error is not null)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(error, json.RootElement.GetProperty("errorCode").GetString());
            if (status is 422 or 429 or 500)
            {
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
                Assert.Equal("pipeline-request", json.RootElement.GetProperty("correlationId").GetString());
            }
            else
            {
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                Assert.Null(response.Headers.Location);
            }
        }
        Assert.NotEmpty(recorder.Projections);
        Assert.All(recorder.Projections, projection =>
        {
            Assert.DoesNotContain(Secret, projection, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", projection, StringComparison.Ordinal);
        });
        Assert.All(recorder.Messages, message => Assert.DoesNotContain(Secret, message, StringComparison.Ordinal));
        if (status is 422 or 500) Assert.Contains(recorder.ErrorScopes, scope => scope.Contains("pipeline-request", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ServiceStartupPhase.BootstrapConfiguration)]
    [InlineData(ServiceStartupPhase.PendingSetup)]
    [InlineData(ServiceStartupPhase.Completed)]
    public async Task Gate_rejection_and_allowed_response_share_security_and_projection_boundaries(ServiceStartupPhase phase)
    {
        var recorder = new Recorder();
        await using var app = Build(recorder, snapshot: new(phase, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable));
        await app.StartAsync(Token);
        using var response = await Send(app, "/ok");
        Assert.Equal(phase == ServiceStartupPhase.Completed ? 200 : 503, (int)response.StatusCode);
        AssertHeaders(response);
        Assert.Equal(2, recorder.Projections.Count);
        Assert.All(recorder.Projections, text => Assert.DoesNotContain(Secret, text, StringComparison.Ordinal));
        if (phase != ServiceStartupPhase.Completed)
        {
            Assert.Equal("{\"errorCode\":\"service.phase.unavailable\"}", await response.Content.ReadAsStringAsync(Token));
            Assert.Equal(0, recorder.EndpointCalls);
        }
    }

    [Fact]
    public async Task Minimal_pipeline_starts_without_authentication_or_forwarding()
    {
        await using var app = Build(new Recorder(), authentication: false, forwarding: false);
        await app.StartAsync(Token);
        Assert.Null(app.Services.GetService<IAuthenticationSchemeProvider>());
        using var response = await Send(app, "/ok");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertHeaders(response);
    }

    [Theory]
    [InlineData("normal", 200)]
    [InlineData("authentication-after-rate", 429)]
    public async Task Authentication_precedes_operator_rate_partitioning(string composition, int secondStatus)
    {
        await using var app = Build(new Recorder(), composition: composition);
        await app.StartAsync(Token);
        using var first = await Send(app, "/limited", cookie: Cookie(app, "operator-one", ManagementPermission.Admin));
        using var second = await Send(app, "/limited", cookie: Cookie(app, "operator-two", ManagementPermission.Admin));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(secondStatus, (int)second.StatusCode);
    }

    [Theory]
    [InlineData("normal", 200)]
    [InlineData("forwarding-after-rate", 429)]
    public async Task Forwarded_client_identity_precedes_rate_partitioning(string composition, int secondStatus)
    {
        await using var app = Build(new Recorder(), composition: composition);
        await app.StartAsync(Token);
        using var first = await Send(app, "/limited", forwardedIp: "203.0.113.1");
        using var second = await Send(app, "/limited", forwardedIp: "203.0.113.2");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(secondStatus, (int)second.StatusCode);
    }

    [Theory]
    [InlineData("normal", true)]
    [InlineData("security-after-gate", false)]
    [InlineData("security-before-routing", false)]
    public async Task Routing_then_security_then_gate_is_observable_on_short_circuit(string composition, bool secure)
    {
        await using var app = Build(new Recorder(), composition: composition,
            snapshot: new(ServiceStartupPhase.BootstrapConfiguration, ServiceMigrationReadinessState.NotStarted, ServiceDatabaseReadinessState.Reachable));
        await app.StartAsync(Token);
        using var response = await Send(app, "/ok");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(secure, response.Headers.Contains("X-Frame-Options"));
    }

    [Theory]
    [InlineData("normal", true)]
    [InlineData("correlation-inside-problem", false)]
    public async Task Correlation_scope_survives_into_exception_handler(string composition, bool hasScope)
    {
        var recorder = new Recorder();
        await using var app = Build(recorder, composition: composition);
        await app.StartAsync(Token);
        using var response = await Send(app, "/exception");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(hasScope, recorder.ErrorScopes.Any(scope => scope.Contains("pipeline-request", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("correlation")]
    [InlineData("problem")]
    [InlineData("security")]
    [InlineData("forwarding")]
    [InlineData("gate")]
    public async Task Composition_rejects_constituent_middleware_before_and_after_it(string component)
    {
        await using var before = Build(new Recorder(), composition: "none");
        AddComponent(before, component);
        Assert.Throws<InvalidOperationException>(() => before.UseServiceMantlePipeline());
        await using var after = Build(new Recorder());
        Assert.Throws<InvalidOperationException>(() => AddComponent(after, component));
        Assert.Throws<InvalidOperationException>(() => after.UseServiceMantlePipeline());
    }

    [Fact]
    public async Task Missing_registration_fails_before_partial_composition()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"));
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.UseServiceMantlePipeline());
        // A failed preflight must not leave the builder marked as composed.
        app.UseServiceMantleCorrelationId();
    }

    [Fact]
    public async Task Caller_cancellation_is_not_changed_into_a_problem_response()
    {
        var recorder = new Recorder();
        await using var app = Build(recorder);
        await app.StartAsync(Token);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var request = app.GetTestClient().GetAsync("/cancel", source.Token);
        await recorder.Entered.Task.WaitAsync(Token);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Empty(recorder.ErrorScopes);
    }

    private static WebApplication Build(Recorder recorder, bool authentication = true, bool forwarding = true,
        string composition = "normal", ServiceHealthSnapshot? snapshot = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(recorder);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var mantle = builder.Services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"), serviceVersion: "1.0")
            .AddSecurityResponseHeaders().AddSensitiveHeaders(options => options.DeniedHeaderNames = ["X-Private-Test"])
            .AddRateLimiting(options => options.Management.PermitLimit = 1)
            .AddServiceMantlePhaseGate()
            .AddExceptionMapping<ValidationFailure>(422, "test.invalid", "The request is invalid.");
        if (forwarding) mantle.AddForwardedHeaders(options => options.KnownProxies = ["127.0.0.1"]);
        if (authentication) mantle.AddManagementCookieAuthentication();
        builder.Services.AddSingleton<IServiceHealthSnapshotSource>(new SnapshotSource(snapshot ??
            new(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable)));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            var projector = context.RequestServices.GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>();
            recorder.Projections.Enqueue(JsonSerializer.Serialize(projector.Project(context.Request.Headers)));
            try { await next(context); }
            finally { recorder.Projections.Enqueue(JsonSerializer.Serialize(projector.Project(context.Request.Headers))); }
        });
        if (composition == "normal") app.UseServiceMantlePipeline();
        else if (composition != "none") ComposeInverted(app, composition);
        app.MapGet("/ok", () => { recorder.EndpointCalls++; return Results.Ok(); }).RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/no-content", () => Results.NoContent()).RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/validation", (HttpContext _) => Task.FromException(new ValidationFailure(Secret))).RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/exception", (HttpContext _) => Task.FromException(new InvalidOperationException(Secret))).RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/internal-cancel", (HttpContext _) => Task.FromException(new OperationCanceledException(Secret))).RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/limited", () => Results.Ok()).RequireRateLimiting(ServiceMantleRateLimitingDefaults.ManagementPolicyName)
            .RequireServiceMantleSecurityResponseHeaders();
        if (authentication)
        {
            app.MapGet("/unauthorized", () => Results.Ok()).RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName)
                .RequireServiceMantleSecurityResponseHeaders();
            app.MapGet("/forbidden", () => Results.Ok()).RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName)
                .RequireServiceMantleSecurityResponseHeaders();
        }
        app.MapGet("/cancel", async (HttpContext context) =>
        {
            recorder.Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        }).RequireServiceMantleSecurityResponseHeaders();
        return app;
    }

    private static void ComposeInverted(WebApplication app, string inversion)
    {
        if (inversion != "forwarding-after-rate") app.UseServiceMantleForwardedHeaders();
        if (inversion == "correlation-inside-problem") app.UseServiceMantleProblemDetails();
        app.UseServiceMantleCorrelationId();
        if (inversion != "correlation-inside-problem") app.UseServiceMantleProblemDetails();
        if (inversion == "security-before-routing") app.UseServiceMantleSecurityResponseHeaders();
        app.UseRouting();
        if (inversion is not ("security-after-gate" or "security-before-routing")) app.UseServiceMantleSecurityResponseHeaders();
        app.UseServiceMantlePhaseGate();
        if (inversion == "security-after-gate") app.UseServiceMantleSecurityResponseHeaders();
        if (inversion != "authentication-after-rate") app.UseAuthentication();
        app.UseRateLimiter();
        if (inversion == "authentication-after-rate") app.UseAuthentication();
        if (inversion == "forwarding-after-rate") app.UseServiceMantleForwardedHeaders();
        app.UseAuthorization();
    }

    private static void AddComponent(WebApplication app, string component)
    {
        switch (component)
        {
            case "correlation": app.UseServiceMantleCorrelationId(); break;
            case "problem": app.UseServiceMantleProblemDetails(); break;
            case "security": app.UseServiceMantleSecurityResponseHeaders(); break;
            case "forwarding": app.UseServiceMantleForwardedHeaders(); break;
            case "gate": app.UseServiceMantlePhaseGate(); break;
        }
    }

    private static Task<HttpResponseMessage> Send(WebApplication app, string path, string? cookie = null, string forwardedIp = "203.0.113.1")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Authorization", "Bearer " + Secret);
        request.Headers.Add("X-Private-Test", Secret);
        request.Headers.Add("x-correlation-id", "pipeline-request");
        request.Headers.Add("X-Forwarded-For", forwardedIp);
        request.Headers.Add("X-Forwarded-Proto", "https");
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return app.GetTestClient().SendAsync(request, Token);
    }

    private static string Cookie(WebApplication app, string id, ManagementPermission permission)
    {
        var scheme = ServiceMantleManagementSessionDefaults.AuthenticationScheme;
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var identity = ManagementIdentity.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, id, [permission]);
        var ticket = new AuthenticationTicket(identity.ToClaimsPrincipal(), new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
        }, scheme);
        return ServiceMantleManagementSessionDefaults.CookieName + "=" + options.TicketDataFormat.Protect(ticket);
    }

    private static void AssertHeaders(HttpResponseMessage response)
    {
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["Cache-Control"] = "no-store", ["Pragma"] = "no-cache", ["X-Content-Type-Options"] = "nosniff",
            ["X-Frame-Options"] = "DENY", ["Referrer-Policy"] = "no-referrer",
            ["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
        }) Assert.Equal(value, Assert.Single(response.Headers.GetValues(name)));
    }

    private sealed class ValidationFailure(string message) : Exception(message);
    private sealed class SnapshotSource(ServiceHealthSnapshot snapshot) : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot);
    }
    private sealed class Recorder : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        internal ConcurrentQueue<string> Projections { get; } = new();
        internal ConcurrentQueue<string> Messages { get; } = new();
        internal ConcurrentQueue<string> ErrorScopes { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int EndpointCalls;
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }
        private sealed class Logger(Recorder owner, string category) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!category.StartsWith("ServiceMantle", StringComparison.Ordinal)) return;
                owner.Messages.Enqueue(formatter(state, exception));
                if (category == "ServiceMantle.Http.ProblemDetails")
                {
                    var values = new List<string>();
                    owner.scopes.ForEachScope((scope, list) => list.Add(JsonSerializer.Serialize(scope)), values);
                    owner.ErrorScopes.Enqueue(string.Join(";", values));
                }
            }
        }
    }
}
