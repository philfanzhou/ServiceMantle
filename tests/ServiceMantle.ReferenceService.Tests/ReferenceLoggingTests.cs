using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.AspNetCore;
using ServiceMantle.Logging;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Logging;
using ServiceMantle.Serilog;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Fixes the sample's logging boundary: the switch is explicit, the request log carries service and
/// Correlation context, and known secret shapes never reach the real Console or the response.
/// </summary>
public sealed class ReferenceLoggingTests
{
    private const string BootstrapSecret = "reference-bootstrap-root-key-sentinel";
    private const string SetupSecret = "reference-setup-code-sentinel";
    private const string DatabaseSecret = "reference-database-credential-sentinel";
    private const string ConfigurationSecret = "reference-configuration-secret-sentinel";
    private const string HeaderSecret = "reference-header-sentinel";
    private const string RequestLine = "Reference request handled";
    private static readonly string[] AllSecrets =
        [BootstrapSecret, SetupSecret, DatabaseSecret, ConfigurationSecret, HeaderSecret];
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Default_and_explicit_false_serve_requests_without_the_serilog_host(bool? enabled)
    {
        await using var host = await StartAsync(enabled);

        using (var client = host.App.GetTestClient())
        {
            using var response = await client.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("skeleton", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        Assert.False(HasSerilogHost(host.App));
        Assert.Null(host.App.Services.GetService<ServiceMantleRequestHeaderDiagnosticProjector>());
        var text = await host.StopAndReadAsync();
        Assert.DoesNotContain(RequestLine, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_activates_only_the_console_pipeline_and_one_owned_sanitizer()
    {
        await using var host = await StartAsync(enabled: true);

        Assert.True(HasSerilogHost(host.App));
        Assert.NotNull(host.App.Services.GetService<ServiceMantleRequestHeaderDiagnosticProjector>());
        Assert.Single(host.App.Services.GetServices<StructuredLogSanitizer>());
        Assert.Single(host.App.Services.GetServices<ILoggerProvider>());
        using (var client = host.App.GetTestClient())
        {
            using var response = await client.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var context = host.App.Services.GetRequiredService<ServiceLogContext>();
        Assert.Equal("reference-service", context.ServiceName);
        Assert.Equal("reference-local", context.InstanceId);
        Assert.False(string.IsNullOrWhiteSpace(context.ServiceVersion));
        var line = SingleRequestLine(await host.StopAndReadAsync(), "\"/\"");
        Assert.Contains("success", line, StringComparison.Ordinal);
        Assert.Contains(context.ServiceName, line, StringComparison.Ordinal);
        Assert.Contains(context.InstanceId, line, StringComparison.Ordinal);
        Assert.Contains(context.ServiceVersion, line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/probe/ok", HttpStatusCode.OK, "success")]
    [InlineData("/probe/invalid", HttpStatusCode.BadRequest, "client_error")]
    [InlineData("/probe/denied", HttpStatusCode.Forbidden, "client_error")]
    [InlineData("/probe/boom", HttpStatusCode.InternalServerError, "server_error")]
    public async Task Each_result_class_produces_one_safe_request_log_with_matching_correlation(
        string path,
        HttpStatusCode expected,
        string result)
    {
        const string correlation = "reference-result-probe";
        await using var host = await StartAsync(enabled: true, map: MapProbes);

        using (var client = host.App.GetTestClient())
        {
            using var request = CreateRequest(path, correlation);
            using var response = await client.SendAsync(request, Token);
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(correlation, Assert.Single(response.Headers.GetValues("x-correlation-id")));
            var body = await response.Content.ReadAsStringAsync(Token);
            if (expected == HttpStatusCode.InternalServerError)
            {
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                Assert.Contains("errorCode", body, StringComparison.Ordinal);
            }

            AssertNoSecrets(body);
        }

        var line = SingleRequestLine(await host.StopAndReadAsync(), correlation);
        Assert.Contains(result, line, StringComparison.Ordinal);
        Assert.Contains(((int)expected).ToString(CultureInfo.InvariantCulture), line, StringComparison.Ordinal);
        Assert.Contains(path, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_headers_reach_the_console_and_the_projector_only_as_redaction_markers()
    {
        const string correlation = "reference-header-probe";
        await using var host = await StartAsync(enabled: true, map: MapProbes);
        var projector = host.App.Services.GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>();

        using (var client = host.App.GetTestClient())
        {
            using var request = CreateRequest("/probe/ok", correlation);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", HeaderSecret);
            request.Headers.Add("Cookie", $"session={HeaderSecret}");
            request.Headers.Add("x-reference-secret", [HeaderSecret, HeaderSecret + "-second"]);
            using var response = await client.SendAsync(request, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = HeaderSecret;
        context.Request.Headers.Cookie = HeaderSecret;
        context.Request.Headers[ReferenceLoggingDefaults.SecretHeaderName.ToLowerInvariant()] =
            new[] { HeaderSecret, HeaderSecret + "-second" };
        var projected = projector.Project(context.Request.Headers);
        Assert.All(projected.Values, value =>
            Assert.Equal(StructuredLogSanitizer.RedactedValue, Assert.IsType<string>(value)));
        Assert.Equal(3, projected.Count);

        var line = SingleRequestLine(await host.StopAndReadAsync(), correlation);
        Assert.Contains(StructuredLogSanitizer.RedactedValue, line, StringComparison.Ordinal);
        Assert.Contains(ReferenceLoggingDefaults.SecretHeaderName, line, StringComparison.OrdinalIgnoreCase);
        AssertNoSecrets(line);
    }

    [Fact]
    public async Task Named_secrets_are_redacted_as_structured_fields_exceptions_and_recognized_free_text()
    {
        const string correlation = "reference-secret-probe";
        await using var host = await StartAsync(enabled: true, map: MapProbes);
        var sanitizer = host.App.Services.GetRequiredService<StructuredLogSanitizer>();

        using (var client = host.App.GetTestClient())
        {
            using var fields = CreateRequest("/probe/secrets", correlation);
            using var logged = await client.SendAsync(fields, Token);
            Assert.Equal(HttpStatusCode.OK, logged.StatusCode);
            using var failing = CreateRequest("/probe/boom", "reference-failure-probe");
            using var failure = await client.SendAsync(failing, Token);
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
            AssertNoSecrets(await failure.Content.ReadAsStringAsync(Token));
        }

        // The supported sensitive value type and the explicitly recognized free-text shapes are
        // verified through the same DI-owned sanitizer the sample uses.
        Assert.Equal(StructuredLogSanitizer.RedactedValue, sanitizer.Sanitize(new ReferenceSecretValue(DatabaseSecret)));
        foreach (var text in new[]
        {
            $"BootstrapRootKey={BootstrapSecret}",
            $"setup_code: {SetupSecret}",
            $"Server=db.internal;Database=reference;Password={DatabaseSecret};",
            $"postgres://reference:{DatabaseSecret}@db.internal:5432/reference",
            $"Authorization: Bearer {ConfigurationSecret}"
        })
        {
            AssertNoSecrets(sanitizer.SanitizeFreeText(text)!);
        }

        var jwt = sanitizer.SanitizeFreeText("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJyZWZlcmVuY2UifQ.reference-signature");
        Assert.DoesNotContain("reference-signature", jwt, StringComparison.Ordinal);
        var pem = sanitizer.SanitizeFreeText(
            $"-----BEGIN PRIVATE KEY-----{ConfigurationSecret}-----END PRIVATE KEY-----");
        AssertNoSecrets(pem!);

        var console = await host.StopAndReadAsync();
        var probe = Assert.Single(console.Split('\n')
            .Where(line => line.Contains("Reference probe", StringComparison.Ordinal)));
        // The denied field names are redacted by the mandatory sink, not by an explicit call site.
        Assert.Contains(StructuredLogSanitizer.RedactedValue, probe, StringComparison.Ordinal);
        Assert.Contains("success", SingleRequestLine(console, correlation), StringComparison.Ordinal);
        Assert.Contains("server_error", SingleRequestLine(console, "reference-failure-probe"), StringComparison.Ordinal);
        AssertNoSecrets(console);
    }

    [Fact]
    public async Task Concurrent_requests_keep_their_own_correlation_in_logs_and_responses()
    {
        var barrier = new RequestBarrier(expected: 2);
        await using var host = await StartAsync(enabled: true, map: app => MapProbes(app, barrier));
        using var client = host.App.GetTestClient();

        var first = client.SendAsync(CreateRequest("/probe/barrier", "reference-alpha"), Token);
        var second = client.SendAsync(CreateRequest("/probe/barrier", "reference-beta"), Token);
        await barrier.Entered.Task.WaitAsync(Observation, Token);
        barrier.Release();
        using var alpha = await first.WaitAsync(Observation, Token);
        using var beta = await second.WaitAsync(Observation, Token);

        Assert.Equal(HttpStatusCode.OK, alpha.StatusCode);
        Assert.Equal(HttpStatusCode.OK, beta.StatusCode);
        Assert.Equal("reference-alpha", Assert.Single(alpha.Headers.GetValues("x-correlation-id")));
        Assert.Equal("reference-beta", Assert.Single(beta.Headers.GetValues("x-correlation-id")));
        var text = await host.StopAndReadAsync();
        var alphaLine = SingleRequestLine(text, "reference-alpha");
        var betaLine = SingleRequestLine(text, "reference-beta");
        Assert.DoesNotContain("reference-beta", alphaLine, StringComparison.Ordinal);
        Assert.DoesNotContain("reference-alpha", betaLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_stays_cancellation_in_the_request_log()
    {
        var barrier = new RequestBarrier(expected: 1);
        await using var host = await StartAsync(enabled: true, map: app => MapProbes(app, barrier));
        using var client = host.App.GetTestClient();
        using var abort = new CancellationTokenSource();
        try
        {
            var request = client.SendAsync(CreateRequest("/probe/barrier", "reference-cancelled"), abort.Token);
            await barrier.Entered.Task.WaitAsync(Observation, Token);

            abort.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        finally
        {
            barrier.Release();
        }

        var line = SingleRequestLine(await host.StopAndReadAsync(), "reference-cancelled");
        Assert.Contains("cancelled", line, StringComparison.Ordinal);
        Assert.DoesNotContain("success", line, StringComparison.Ordinal);
        Assert.DoesNotContain("server_error", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_logging_still_creates_no_database_and_stages_no_business_work()
    {
        await using var host = await StartAsync(enabled: true);

        using (var client = host.App.GetTestClient())
        {
            using var response = await client.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ReferenceDbContext>();
            Assert.Empty(context.ChangeTracker.Entries());
        }

        await host.StopAndReadAsync();
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.Root));
    }

    /// <summary>Detects the ServiceMantle Serilog host without reaching into package internals.</summary>
    private static bool HasSerilogHost(WebApplication app) => app.Services
        .GetServices<ILoggerProvider>()
        .Any(provider => provider.GetType().Assembly == typeof(ServiceMantleSerilogOptions).Assembly);

    private static HttpRequestMessage CreateRequest(string path, string correlation)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("x-correlation-id", correlation);
        return request;
    }

    private static void AssertNoSecrets(string value)
    {
        foreach (var secret in AllSecrets)
        {
            Assert.DoesNotContain(secret, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string SingleRequestLine(string text, string marker) => Assert.Single(text
        .Split('\n')
        .Where(line => line.Contains(RequestLine, StringComparison.Ordinal) &&
            line.Contains(marker, StringComparison.Ordinal)));

    private static void MapProbes(WebApplication app) => MapProbes(app, barrier: null);

    private static void MapProbes(WebApplication app, RequestBarrier? barrier)
    {
        app.MapGet("/probe/ok", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/probe/invalid", () => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["workspace.display_name"] = ["The value is required."]
        }));
        app.MapGet("/probe/denied", () => Results.StatusCode(StatusCodes.Status403Forbidden));
        app.MapGet("/probe/boom", () =>
        {
            var failure = new InvalidOperationException(BootstrapSecret);
            failure.Data["SetupCode"] = SetupSecret;
            throw failure;
        });
        app.MapGet("/probe/secrets", (HttpContext context) =>
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ServiceMantle.ReferenceService.Probe")
                .LogInformation(
                    "Reference probe {BootstrapRootKey} {SetupCode} {DatabaseConnectionString} {ConfigurationSecret}",
                    BootstrapSecret,
                    SetupSecret,
                    DatabaseSecret,
                    ConfigurationSecret);
            return Results.Ok();
        });
        if (barrier is not null)
        {
            app.MapGet("/probe/barrier", async (HttpContext context) =>
            {
                await barrier.WaitAsync(context.RequestAborted);
                return Results.Ok();
            });
        }
    }

    private static async Task<ReferenceHost> StartAsync(bool? enabled, Action<WebApplication>? map = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sm-reference-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var original = Console.Out;
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        // The Console sink is created while the host starts, so the capture must be installed first.
        Console.SetOut(TextWriter.Synchronized(writer));
        try
        {
            List<string> args =
            [
                "--environment", "Production",
                "--ReferenceService:DatabasePath", Path.Combine(root, "reference.db")
            ];
            if (enabled is not null)
            {
                args.Add("--" + ReferenceLoggingDefaults.EnabledKey);
                args.Add(enabled.Value ? "true" : "false");
            }

            var builder = ReferenceApplication.CreateBuilder([.. args]);
            builder.WebHost.UseTestServer();
            var app = ReferenceApplication.Build(builder);
            // Probe endpoints are mapped by the test on the public seam, never by the running sample.
            map?.Invoke(app);
            await app.StartAsync(Token);
            return new ReferenceHost(app, root, writer, original);
        }
        catch
        {
            Console.SetOut(original);
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private sealed class ReferenceHost(WebApplication app, string root, StringWriter writer, TextWriter original)
        : IAsyncDisposable
    {
        private bool stopped;

        internal WebApplication App { get; } = app;

        internal string Root { get; } = root;

        /// <summary>Stops the host so the Console pipeline flushes, then returns the captured text.</summary>
        internal async Task<string> StopAndReadAsync()
        {
            if (!stopped)
            {
                stopped = true;
                await App.StopAsync(Token);
                await App.DisposeAsync();
            }

            return writer.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAndReadAsync();
            }
            finally
            {
                Console.SetOut(original);
                writer.Dispose();
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RequestBarrier(int expected)
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entered;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref entered) == expected) Entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        internal void Release() => release.TrySetResult();
    }

    private sealed class ReferenceSecretValue(string value) : ISensitiveLogValue
    {
        public string Value { get; } = value;

        public override string ToString() => Value;
    }
}
