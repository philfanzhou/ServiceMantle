using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using global::Serilog;
using Serilog.Core;
using Serilog.Events;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Audit;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Logging;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.Serilog.Tests;

[Collection("ServiceMantle Serilog Console")]
public sealed class ServiceMantleCoreOptionalCompositionTests
{
    private const string Secret = "composition-private-value";
    private const string Correlation = "composition-request";
    private const string AdminPath = "/management/protected";
    private static readonly string[] HealthPaths = ["/health/live", "/health/ready", "/health"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ServiceHealthSnapshot Ready = new(ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task All_eight_subsets_run_real_HTTP_and_own_only_selected_capabilities(bool authentication, bool health, bool logging)
    {
        await using var host = new Composition(authentication, health, logging);
        await host.StartAsync();
        Assert.Equal(0, host.Source.Resolutions);
        Assert.Equal(0, host.Source.Calls);
        await host.AssertCapabilitiesAsync(authentication, health, logging);
        using (var response = await host.SendAsync("/ok"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertSecurity(response);
        }
        Assert.Equal(1, host.Source.Calls); // The required Gate, not optional health polling.

        foreach (var path in HealthPaths)
        {
            var calls = host.Source.Calls;
            using var response = await host.SendAsync(path);
            Assert.Equal(health ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(calls + (health && path != "/health/live" ? 1 : 0), host.Source.Calls);
        }
        if (authentication)
        {
            foreach (var (permission, status, error) in new (ManagementPermission?, int, string?)[]
            {
                (null, 401, ServiceMantleManagementSessionDefaults.UnauthenticatedErrorCode),
                (ManagementPermission.Read, 403, ServiceMantleManagementSessionDefaults.ForbiddenErrorCode),
                (ManagementPermission.Admin, 200, null)
            })
            {
                using var response = await host.SendAsync(AdminPath, permission);
                Assert.Equal(status, (int)response.StatusCode);
                AssertSecurity(response);
                if (error is not null) await AssertErrorAsync(response, error);
            }
            host.Source.Snapshot = new(ServiceStartupPhase.PendingSetup,
                ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable);
            using var gated = await host.SendAsync(AdminPath, ManagementPermission.Admin);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, gated.StatusCode);
            await AssertErrorAsync(gated, "service.phase.unavailable");
            AssertSecurity(gated);
            Assert.Equal(1, host.AdminCalls);
        }
        else
        {
            using var absent = await host.SendAsync(AdminPath);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            Assert.Equal(0, host.AdminCalls);
        }
        if (logging) AssertRequestLog(host.Sink.Events);
        else Assert.Empty(host.Sink.Events);
        await host.StopAndDisposeAsync();
        Assert.Equal(logging ? 1 : 0, host.Sink.Created);
        Assert.Equal(logging ? 1 : 0, host.Sink.Disposed);
        Assert.Equal(logging ? 1 : 0, host.Runtime?.FlushInvocationCount ?? 0);
    }

    [Fact]
    public async Task Health_in_full_composition_maps_every_defined_snapshot_without_gate_resampling()
    {
        await using var host = new Composition();
        await host.StartAsync();
        using (var live = await host.SendAsync("/health/live")) Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(0, host.Source.Resolutions);
        foreach (var phase in Enum.GetValues<ServiceStartupPhase>())
        foreach (var migration in Enum.GetValues<ServiceMigrationReadinessState>())
        foreach (var database in Enum.GetValues<ServiceDatabaseReadinessState>())
        {
            host.Source.Snapshot = new(phase, migration, database);
            var ready = phase == ServiceStartupPhase.Completed && migration == ServiceMigrationReadinessState.Succeeded &&
                database == ServiceDatabaseReadinessState.Reachable;
            using (var management = await host.SendAsync(AdminPath, ManagementPermission.Admin))
            {
                Assert.Equal(ready ? 200 : 503, (int)management.StatusCode);
                AssertSecurity(management);
                if (!ready) await AssertErrorAsync(management, "service.phase.unavailable");
            }
            foreach (var path in HealthPaths)
            {
                var before = host.Source.Calls;
                using var response = await host.SendAsync(path);
                Assert.Equal(path == "/health/live" || ready ? 200 : 503, (int)response.StatusCode);
                Assert.Equal(before + (path == "/health/live" ? 0 : 1), host.Source.Calls);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
                Assert.Equal(path == "/health/live" ? "live" : ready ? "ready" : "not_ready",
                    json.RootElement.GetProperty("status").GetString());
            }
        }
    }

    [Theory]
    [InlineData("missing", "health.probe_failed")]
    [InlineData("throw", "health.probe_failed")]
    [InlineData("timeout", "health.probe_timeout")]
    public async Task Health_failures_keep_the_existing_safe_classification(string mode, string error)
    {
        await using var host = new Composition(sourceMode: mode);
        await host.StartAsync();
        using (var live = await host.SendAsync("/health/live")) Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(0, host.Source.Resolutions);
        foreach (var path in HealthPaths.Skip(1))
        {
            using var response = await host.SendAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await AssertErrorAsync(response, error);
        }
        Assert.All(host.Sink.Events, item => Assert.DoesNotContain(Secret, item.RenderMessage(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Client_disconnect_remains_cancellation_through_the_full_pipeline(string path)
    {
        await using var host = new Composition(sourceMode: "cancel");
        await host.StartAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var request = host.Client.GetAsync(path, cancellation.Token);
        await host.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await host.Source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(await host.RequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.DoesNotContain(host.Sink.Events, item => item.Properties.TryGetValue("SourceContext", out var category) &&
            category.ToString() == "\"ServiceMantle.Http.ProblemDetails\"");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Console_and_request_projector_share_the_snapshot_in_both_registration_orders(bool serilogFirst)
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var original = Console.Out;
        Console.SetOut(TextWriter.Synchronized(output));
        try
        {
            await using var host = new Composition(serilogFirst: serilogFirst, console: true);
            await host.StartAsync();
            using var response = await host.SendAsync("/ok");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await host.StopAndDisposeAsync();
            var text = output.ToString();
            Assert.Contains("Composition handled", text, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
            Assert.Contains("X-Composition-Secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Correlation, text, StringComparison.Ordinal);
            Assert.Contains("composition-01", text, StringComparison.Ordinal);
            Assert.Contains("1.2.3", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public async Task Equivalent_repeated_capabilities_have_one_effective_scheme_route_and_lifecycle()
    {
        await using var host = new Composition(duplicate: true);
        await host.StartAsync();
        await host.AssertCapabilitiesAsync(true, true, true);
        using var response = await host.SendAsync(AdminPath, ManagementPermission.Admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var path in HealthPaths)
        {
            using var health = await host.SendAsync(path);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        using var logged = await host.SendAsync("/ok");
        AssertRequestLog(host.Sink.Events);
        await host.StopAndDisposeAsync();
        Assert.Equal(1, host.Sink.Created);
        Assert.Equal(1, host.Sink.Disposed);
        Assert.Equal(1, host.Runtime!.FlushInvocationCount);
    }

    [Theory]
    [InlineData("cookie-invalid", "HttpOnly")]
    [InlineData("cookie-conflict", "Conflicting ServiceMantle management cookie")]
    [InlineData("health-invalid", "health probe timeout")]
    [InlineData("health-conflict", "Conflicting ServiceMantle health")]
    [InlineData("logging-invalid", "serilog.minimum_level_invalid")]
    [InlineData("logging-conflict", "serilog.console_sink_conflict")]
    public async Task Invalid_or_conflicting_optional_configuration_prevents_HTTP_start_without_echoing_secrets(string failure, string expected)
    {
        await using var host = new Composition(failure: failure);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());
        Assert.Contains(expected, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.False(host.App.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, host.Source.Calls);
        Assert.All(host.Sink.Events, item => Assert.DoesNotContain(Secret, item.RenderMessage(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Precancelled_full_composition_does_not_report_started()
    {
        await using var host = new Composition();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.App.StartAsync(cancellation.Token));
        Assert.False(host.App.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, host.Source.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Throwing_or_blocked_sink_disposal_preserves_best_effort_shutdown(bool blocked)
    {
        using var release = new ManualResetEventSlim();
        await using var host = new Composition(sinkRelease: blocked ? release : null, throwOnDispose: !blocked);
        await host.StartAsync();
        using var response = await host.SendAsync("/ok");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        try
        {
            var watch = Stopwatch.StartNew();
            await host.StopAndDisposeAsync().WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
            await host.Sink.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(1, host.Sink.Disposed);
            Assert.Equal(1, host.Runtime!.FlushInvocationCount);
            if (blocked) Assert.False(host.Sink.DisposeFinished.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            await host.Sink.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
    }

    private static void AssertRequestLog(IEnumerable<LogEvent> events)
    {
        var item = Assert.Single(events, item => item.MessageTemplate.Text.StartsWith("Composition handled", StringComparison.Ordinal));
        foreach (var (name, value) in new Dictionary<string, string>
        {
            [ServiceLogFieldNames.ServiceName] = "composition",
            [ServiceLogFieldNames.InstanceId] = "composition-01",
            [ServiceLogFieldNames.ServiceVersion] = "1.2.3",
            [ServiceLogFieldNames.CorrelationId] = Correlation,
            ["Password"] = StructuredLogSanitizer.RedactedValue
        }) Assert.Equal(value, Assert.IsType<ScalarValue>(item.Properties[name]).Value);
        var headers = Assert.IsType<StructureValue>(item.Properties["Headers"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, Assert.IsType<ScalarValue>(Assert.Single(headers.Properties,
            property => property.Name.Equals("X-Composition-Secret", StringComparison.OrdinalIgnoreCase)).Value).Value);
        Assert.DoesNotContain(Secret, item.RenderMessage(), StringComparison.Ordinal);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, string error)
    {
        var body = await response.Content.ReadAsStringAsync(Token);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(error, json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain(Secret, body + response.Headers, StringComparison.Ordinal);
    }

    private static void AssertSecurity(HttpResponseMessage response)
    {
        Assert.Equal(Correlation, Assert.Single(response.Headers.GetValues("x-correlation-id")));
        Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }

    private sealed class Composition : IAsyncDisposable
    {
        internal WebApplication App { get; }
        internal HttpClient Client { get; } = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        internal SnapshotSource Source { get; }
        internal ControlledSink Sink { get; }
        internal ServiceMantleSerilogRuntime? Runtime { get; }
        internal TaskCompletionSource<bool> RequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int AdminCalls;
        private bool disposed;

        internal Composition(bool authentication = true, bool health = true, bool logging = true,
            string sourceMode = "ready", bool serilogFirst = false, bool console = false,
            bool duplicate = false, string? failure = null, ManualResetEventSlim? sinkRelease = null, bool throwOnDispose = false)
        {
            Source = new SnapshotSource(sourceMode);
            Sink = new ControlledSink(sinkRelease, throwOnDispose);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration["Unrelated:Password"] = Secret;
            var mantle = builder.Services.AddServiceMantle(ServiceId.Parse("composition"), InstanceId.Parse("composition-01"), serviceVersion: "1.2.3")
                .AddSecurityResponseHeaders().AddRateLimiting().AddServiceMantlePhaseGate();
            void RegisterLogging() => builder.AddServiceMantleSerilog(options =>
            {
                options.FlushTimeout = TimeSpan.FromMilliseconds(50);
                if (failure == "logging-invalid") options.MinimumLevel = Secret;
            });
            if (logging && serilogFirst) RegisterLogging();
            mantle.AddSensitiveHeaders(options => options.DeniedHeaderNames = ["X-Composition-Secret"]);
            if (logging && !serilogFirst) RegisterLogging();
            if (authentication)
            {
                builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
                mantle.AddManagementCookieAuthentication(options => options.HttpOnly = failure != "cookie-invalid");
            }
            if (health) mantle.AddServiceMantleHealthEndpoints(options => options.ProbeTimeout =
                failure == "health-invalid" ? TimeSpan.FromMilliseconds(99) :
                sourceMode == "timeout" ? ServiceMantleHealthOptions.MinimumProbeTimeout : TimeSpan.FromSeconds(10));
            if (duplicate)
            {
                mantle.AddManagementCookieAuthentication();
                mantle.AddServiceMantleHealthEndpoints(options => options.ProbeTimeout = TimeSpan.FromSeconds(10));
                RegisterLogging();
            }
            if (failure == "cookie-conflict") mantle.AddManagementCookieAuthentication(options => options.SlidingExpiration = false);
            if (failure == "health-conflict") mantle.AddServiceMantleHealthEndpoints(options => options.ProbeTimeout = TimeSpan.FromSeconds(2));
            if (failure == "logging-conflict") builder.AddServiceMantleSerilog(options => options.OutputTemplate = Secret + " {Message}");
            if (sourceMode != "missing") builder.Services.AddSingleton<IServiceHealthSnapshotSource>(_ =>
            {
                Interlocked.Increment(ref Source.Resolutions);
                return Source;
            });
            if (logging && !console) builder.Services.Replace(ServiceDescriptor.Singleton<IServiceMantleSerilogSinkFactory>(Sink));
            App = builder.Build();
            Runtime = App.Services.GetService<ServiceMantleSerilogRuntime>();
            // Observe cancellation outside the composed exception handler without changing its result.
            App.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    RequestCancelled.TrySetResult(true);
                    throw;
                }
            });
            App.UseServiceMantlePipeline();
            App.MapGet("/ok", (HttpContext context) =>
            {
                var headers = context.RequestServices.GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>().Project(context.Request.Headers);
                context.RequestServices.GetRequiredService<ILogger<ServiceMantleCoreOptionalCompositionTests>>()
                    .LogInformation("Composition handled {Password} {@Headers}", Secret, headers);
                return Results.Ok();
            }).RequireServiceMantleSecurityResponseHeaders();
            if (authentication) App.MapServiceMantleManagementGroup().MapGet("/protected", () =>
                { Interlocked.Increment(ref AdminCalls); return Results.Ok(); })
                .WithServiceMantleManagementSurface(ServiceMantleManagementSurface.Management)
                .RequireServiceMantleManagementAdmin().RequireServiceMantleSecurityResponseHeaders();
            if (health) App.MapServiceMantleHealthEndpoints();
        }

        internal async Task StartAsync()
        {
            await App.StartAsync(Token);
            Client.BaseAddress = new Uri(Assert.Single(App.Urls));
        }

        internal async Task AssertCapabilitiesAsync(bool authentication, bool health, bool logging)
        {
            var schemes = App.Services.GetService<IAuthenticationSchemeProvider>();
            if (authentication) Assert.Equal(ServiceMantleManagementSessionDefaults.AuthenticationScheme,
                Assert.Single(await schemes!.GetAllSchemesAsync()).Name);
            else Assert.Null(schemes);
            var routes = ((IEndpointRouteBuilder)App).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
            Assert.Equal(authentication ? 1 : 0, routes.Count(path => path == AdminPath));
            foreach (var path in HealthPaths) Assert.Equal(health ? 1 : 0, routes.Count(route => route == path));
            Assert.Equal(health, App.Services.GetService<ServiceReadinessContributorCombiner>() is not null);
            Assert.Equal(logging, Runtime is not null);
            Assert.Equal(logging ? 1 : 0, App.Services.GetServices<ILoggerProvider>().Count());
            Assert.Equal(logging ? 1 : 0, App.Services.GetServices<IHostedService>().Count(service => service is ServiceMantleSerilogLifecycle));
            Assert.Equal(health ? 1 : 0, App.Services.GetServices<IHostedService>().Count(service => service.GetType().Name == "ServiceMantleHealthStartupValidator"));
        }

        internal async Task<HttpResponseMessage> SendAsync(string path, ManagementPermission? permission = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("x-correlation-id", Correlation);
            request.Headers.Add("X-Composition-Secret", Secret);
            if (permission is not null)
            {
                var scheme = ServiceMantleManagementSessionDefaults.AuthenticationScheme;
                var options = App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
                var identity = ManagementIdentity.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, Secret, [permission.Value]);
                var ticket = new AuthenticationTicket(identity.ToClaimsPrincipal(), new AuthenticationProperties
                {
                    IssuedUtc = DateTimeOffset.UtcNow,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
                }, scheme);
                request.Headers.Add("Cookie", ServiceMantleManagementSessionDefaults.CookieName + "=" + options.TicketDataFormat.Protect(ticket));
            }
            return await Client.SendAsync(request, Token);
        }

        internal async Task StopAndDisposeAsync()
        {
            if (disposed) return;
            await App.StopAsync(Token);
            await App.DisposeAsync();
            disposed = true;
            Client.Dispose();
        }
        public async ValueTask DisposeAsync() => await StopAndDisposeAsync();
    }

    private sealed class SnapshotSource(string mode) : IServiceHealthSnapshotSource
    {
        internal ServiceHealthSnapshot Snapshot = Ready;
        internal int Resolutions;
        internal int Calls;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (mode == "throw") throw new InvalidOperationException(Secret);
            if (mode is "timeout" or "cancel")
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return Snapshot;
        }
    }

    private sealed class ControlledSink(ManualResetEventSlim? release, bool throwOnDispose)
        : ILogEventSink, IDisposable, IServiceMantleSerilogSinkFactory
    {
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        internal int Created;
        internal int Disposed;
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogEventSink Create(ServiceMantleSerilogConfiguration configuration, IServiceMantleStructuredLogSanitizer sanitizer)
        {
            Interlocked.Increment(ref Created);
            return new ServiceMantleSanitizingSink(sanitizer,
                new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger());
        }
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
        public void Dispose()
        {
            Interlocked.Increment(ref Disposed);
            DisposeEntered.TrySetResult();
            try
            {
                release?.Wait();
                if (throwOnDispose) throw new InvalidOperationException(Secret);
            }
            finally { DisposeFinished.TrySetResult(); }
        }
    }
}
