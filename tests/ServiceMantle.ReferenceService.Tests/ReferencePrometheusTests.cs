using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Management;
using ServiceMantle.ReferenceService.Telemetry;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Accepts the sample's authorized Prometheus scrape endpoint through its real
/// <see cref="ReferenceApplication"/> path against a real PostgreSQL server and real management
/// logins: the explicit switch, the gate refusal, the phase gate ordering, the authorization
/// matrix, the verb and cancellation boundaries, host stopping, disposal, and the absence of any
/// secret in a response or a captured log line.
/// </summary>
/// <remarks>
/// The class runs in the serialized telemetry collection because meter listener state is
/// process-wide.
/// </remarks>
[Collection(ReferenceTelemetryCollection.Name)]
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferencePrometheusTests : IAsyncLifetime
{
    private const string MetricsPath = "/metrics";
    private const string LoginPath = "/management/v1/session/login";
    private const string InstallationTable = "service_installations";
    private const string WorkspaceTable = "reference_workspaces";
    private const string ServiceIdValue = "reference-service";

    private const string AdminId = "ops-admin";
    private const string AdminCredential = "synthetic-prometheus-admin-secret";
    private const string ReaderId = "ops-reader";
    private const string ReaderCredential = "synthetic-prometheus-reader-secret";

    // Synthetic fixture secrets, asserted to stay out of every scrape response and captured log.
    private const string SyntheticUser = "reference_prometheus_owner";
    private const string SyntheticPassword = "synthetic-reference-prometheus-secret";
    private const string SyntheticRootKey = "synthetic-reference-prometheus-root-key";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? container;
    private string? maintenanceConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("reference_prometheus_maintenance")
            .WithUsername(SyntheticUser)
            .WithPassword(SyntheticPassword)
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        maintenanceConnectionString = container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(TestContext.Current.CancellationToken);
            await container.DisposeAsync();
        }
    }

    // --- A1 (P1): the off switch maps nothing --------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    public async Task An_unauthorized_switch_maps_nothing_and_registers_no_prometheus_service(
        string? value)
    {
        RequireDatabase();
        var database = DatabaseName("off_" + (value ?? "missing"));
        await CreateDatabaseAsync(database);
        var arguments = BaseArguments(database, prometheus: value);

        var builder = ReferenceApplication.CreateBuilder([.. arguments]);
        builder.WebHost.UseTestServer();
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(ReferencePrometheusRegistration) ||
            IsPrometheusOwned(descriptor.ServiceType) ||
            IsPrometheusOwned(descriptor.ImplementationType) ||
            IsPrometheusOwned(descriptor.ImplementationInstance?.GetType()));
        await using var app = await StartAppAsync(builder);
        using var client = app.GetTestClient();

        using var scrape = await client.GetAsync(MetricsPath, Token);
        Assert.Equal(HttpStatusCode.NotFound, scrape.StatusCode);
        Assert.Null(app.Services.GetService<ReferencePrometheusRegistration>());
    }

    // --- A2 (P2): the switch without the gate is refused before anything happens ----------

    [Fact]
    public void The_switch_without_the_gate_is_refused_before_any_side_effect()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sm-reference-prometheus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() => ReferenceApplication.CreateBuilder(
            [
                "--ReferenceService:DatabasePath", Path.Combine(directory, "reference.db"),
                "--" + ReferenceTelemetryDefaults.PrometheusEnabledKey, "true",
            ]));

            Assert.Contains(ReferenceTelemetryDefaults.PrometheusEnabledKey, failure.Message, StringComparison.Ordinal);
            Assert.Contains(ReferencePostgreSqlStartupOptions.EnabledKey, failure.Message, StringComparison.Ordinal);
            // The configured value itself is never echoed back.
            // Refused before any registration ran: no database file, no provider, nothing started.
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // --- A3-A6 (P3-P8): the scrape matrix over real logins --------------------------------

    [Fact]
    public async Task The_scrape_matrix_over_real_logins()
    {
        RequireDatabase();
        var database = DatabaseName("matrix");
        await CreateDatabaseAsync(database);
        var recorder = new CapturingLoggerProvider();
        await using var app = await StartAppAsync(
            BaseArguments(database, telemetry: "true"),
            recorder);
        using var client = app.GetTestClient();

        // P3: the phase gate answers before authentication while the installation is pending.
        using (var pending = await client.GetAsync(MetricsPath, Token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
            var body = await pending.Content.ReadAsStringAsync(Token);
            Assert.Contains("service.phase.unavailable", body, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet_", body, StringComparison.Ordinal);
        }

        await MakeReadyAsync(database);

        // P4: a completed installation still requires a session.
        using (var anonymous = await client.GetAsync(MetricsPath, Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        // P5: a read-only operator holds a valid session but not the admin permission.
        var readerCookie = await LoginAsync(client, ReaderId, ReaderCredential);
        using (var forbidden = await SendAsync(client, HttpMethod.Get, MetricsPath, readerCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            var body = await forbidden.Content.ReadAsStringAsync(Token);
            Assert.Contains("management.session.forbidden", body, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet_", body, StringComparison.Ordinal);
            Assert.DoesNotContain("http_server_", body, StringComparison.Ordinal);
        }

        // P6: the admin session scrapes the Prometheus text format with the runtime series.
        var adminCookie = await LoginAsync(client, AdminId, AdminCredential);
        using (var scrape = await SendAsync(client, HttpMethod.Get, MetricsPath, adminCookie))
        {
            Assert.Equal(HttpStatusCode.OK, scrape.StatusCode);
            Assert.Equal(
                "text/plain",
                scrape.Content.Headers.ContentType?.MediaType?.Split(';')[0] ?? string.Empty,
                ignoreCase: true);
            var body = await scrape.Content.ReadAsStringAsync(Token);
            Assert.Contains("dotnet_", body, StringComparison.Ordinal);
            AssertNoSecret(body);
        }

        // P8: HEAD is the same authorized read without a body; POST is refused.
        using (var head = await SendAsync(client, HttpMethod.Head, MetricsPath, adminCookie))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(0, head.Content.Headers.ContentLength ?? 0);
        }

        using (var post = await SendAsync(client, HttpMethod.Post, MetricsPath, adminCookie))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        }

        Assert.All(recorder.Messages, message => Assert.DoesNotContain(SyntheticPassword, message, StringComparison.Ordinal));
        Assert.All(recorder.Messages, message => Assert.DoesNotContain(AdminCredential, message, StringComparison.Ordinal));
        Assert.All(recorder.Messages, message => Assert.DoesNotContain(SyntheticRootKey, message, StringComparison.Ordinal));
        Assert.All(recorder.Messages, message => Assert.DoesNotContain("Set-Cookie", message, StringComparison.Ordinal));
    }

    // --- A5 (P7): without base telemetry the scrape carries no series ---------------------

    [Fact]
    public async Task A_scrape_without_base_telemetry_carries_no_series()
    {
        RequireDatabase();
        var database = DatabaseName("no_series");
        await CreateDatabaseAsync(database);
        await using var app = await StartAppAsync(BaseArguments(database, telemetry: "false"));
        using var client = app.GetTestClient();
        await MakeReadyAsync(database);
        var adminCookie = await LoginAsync(client, AdminId, AdminCredential);

        using var scrape = await SendAsync(client, HttpMethod.Get, MetricsPath, adminCookie);

        Assert.Equal(HttpStatusCode.OK, scrape.StatusCode);
        var body = await scrape.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("dotnet_", body, StringComparison.Ordinal);
        Assert.DoesNotContain("http_", body, StringComparison.Ordinal);
        Assert.DoesNotContain("servicemantle_", body, StringComparison.Ordinal);
    }

    // --- A7 (P9): an aborted scrape stays cancelled and the host keeps serving ------------

    [Fact]
    public async Task An_aborted_scrape_stays_cancelled_and_the_host_keeps_serving()
    {
        RequireDatabase();
        var database = DatabaseName("aborted");
        await CreateDatabaseAsync(database);
        await using var app = await StartAppAsync(BaseArguments(database, telemetry: "true"));
        using var client = app.GetTestClient();
        await MakeReadyAsync(database);
        var adminCookie = await LoginAsync(client, AdminId, AdminCredential);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SendAsync(client, HttpMethod.Get, MetricsPath, adminCookie, abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        using (var afterwards = await SendAsync(client, HttpMethod.Get, MetricsPath, adminCookie))
        {
            Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);
        }
    }

    // --- P10: a scrape during host stopping is refused with an empty 503 ------------------

    [Fact]
    public async Task A_scrape_during_host_stopping_is_refused_with_an_empty_503()
    {
        RequireDatabase();
        var database = DatabaseName("stopping");
        await CreateDatabaseAsync(database);
        var probe = new ScrapeDuringStopProbe();
        await using var app = await StartAppAsync(
            BaseArguments(database, telemetry: "true"),
            extraServices: services => services.AddHostedService(_ => probe));
        using var client = app.GetTestClient();
        await MakeReadyAsync(database);
        var adminCookie = await LoginAsync(client, AdminId, AdminCredential);
        probe.Attach(client, adminCookie);

        // StopAsync fires ApplicationStopping before hosted services settle, so the probe's scrape
        // runs while the endpoint gate observes the stopping host.
        await app.StopAsync(Token);

        var (status, body) = await probe.Result;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(string.Empty, body);
        await app.DisposeAsync();
    }

    // --- A9: the disposed host releases the meter listeners -------------------------------

    [Fact]
    public async Task The_disposed_host_releases_the_meter_listeners()
    {
        RequireDatabase();
        var database = DatabaseName("dispose");
        await CreateDatabaseAsync(database);
        await using var app = await StartAppAsync(BaseArguments(database, telemetry: "true"));
        using var client = app.GetTestClient();
        await MakeReadyAsync(database);
        using var meter = new Meter("System.Runtime");
        var counter = meter.CreateCounter<long>("prometheus_dispose_probe");

        Assert.True(counter.Enabled);
        await app.StopAsync(Token);
        await app.DisposeAsync();

        Assert.False(counter.Enabled);
    }

    private static bool IsPrometheusOwned(Type? type) =>
        type?.Namespace is { } space &&
        space.StartsWith("ServiceMantle.OpenTelemetry.Prometheus", StringComparison.Ordinal);

    private static void AssertNoSecret(string body)
    {
        Assert.DoesNotContain(SyntheticPassword, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticRootKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminCredential, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ReaderCredential, body, StringComparison.Ordinal);
    }

    private List<string> BaseArguments(string database, string? telemetry = "true", string? prometheus = "true")
    {
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, Target(database),
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
            "--environment", "Production",
            "--" + ReferenceManagementOptions.RootKeySetting, SyntheticRootKey,
            "--ReferenceService:Management:Operators:0:Id", AdminId,
            "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
            "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
            "--ReferenceService:Management:Operators:0:Credential", AdminCredential,
            "--ReferenceService:Management:Operators:1:Id", ReaderId,
            "--ReferenceService:Management:Operators:1:DisplayName", "Ops Reader",
            "--ReferenceService:Management:Operators:1:Permissions", "management.read",
            "--ReferenceService:Management:Operators:1:Credential", ReaderCredential,
        };
        if (prometheus is not null)
        {
            arguments.AddRange(["--" + ReferenceTelemetryDefaults.PrometheusEnabledKey, prometheus]);
        }

        if (telemetry is not null)
        {
            arguments.AddRange(["--" + ReferenceTelemetryDefaults.EnabledKey, telemetry]);
        }

        return arguments;
    }

    private async Task<WebApplication> StartAppAsync(
        List<string> arguments,
        ILoggerProvider? recorder = null,
        Action<IServiceCollection>? extraServices = null)
    {
        var builder = ReferenceApplication.CreateBuilder([.. arguments]);
        builder.WebHost.UseTestServer();
        return await StartAppAsync(builder, recorder, extraServices);
    }

    private async Task<WebApplication> StartAppAsync(
        WebApplicationBuilder builder,
        ILoggerProvider? recorder = null,
        Action<IServiceCollection>? extraServices = null)
    {
        if (recorder is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(recorder);
        }

        extraServices?.Invoke(builder.Services);
        var app = ReferenceApplication.Build(builder);
        try
        {
            await app.StartAsync(Token);
            return app;
        }
        catch (Exception)
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string secret)
    {
        using var login = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(
                $$"""{"username":"{{username}}","secret":"{{secret}}"}""",
                Encoding.UTF8,
                "application/json"),
        };
        login.Headers.TryAddWithoutValidation("X-ServiceMantle-Request", "1");
        using var response = await client.SendAsync(login, Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").First().Split(';', 2)[0];
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string cookie,
        CancellationToken? cancellationToken = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request, cancellationToken ?? Token);
    }

    private async Task MakeReadyAsync(string database)
    {
        var target = Target(database);
        await ExecuteAsync(target, $"""
            UPDATE public."{InstallationTable}"
            SET status = 1, completed_at_utc = now(), version = version + 1
            WHERE service_id = '{ServiceIdValue}'
            """);
        await ExecuteAsync(target, $"""
            INSERT INTO public."{WorkspaceTable}" ("Id", "DisplayName")
            VALUES (gen_random_uuid(), 'prometheus')
            """);
    }

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private async Task CreateDatabaseAsync(string database) =>
        await ExecuteAsync(maintenanceConnectionString!, $"""CREATE DATABASE "{database}" """);

    private string Target(string database) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
        }.ConnectionString;

    private async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static string DatabaseName(string name) => $"reference_prometheus_{name.ToLowerInvariant()}";

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    /// <summary>
    /// Scrapes once from inside the host's own stopping sequence, when ApplicationStopping has
    /// fired but the test server still answers in-memory requests.
    /// </summary>
    private sealed class ScrapeDuringStopProbe : IHostedService
    {
        private readonly TaskCompletionSource<(HttpStatusCode, string)> result = new();
        private HttpClient? client;
        private string cookie = string.Empty;

        internal Task<(HttpStatusCode Status, string Body)> Result => result.Task;

        internal void Attach(HttpClient client, string cookie)
        {
            this.client = client;
            this.cookie = cookie;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (client is null)
            {
                return;
            }

            try
            {
                using var scrape = await SendAsync(client, HttpMethod.Get, MetricsPath, cookie, cancellationToken);
                var body = await scrape.Content.ReadAsStringAsync(cancellationToken);
                result.TrySetResult((scrape.StatusCode, body));
            }
            catch (Exception exception)
            {
                result.TrySetException(exception);
            }
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
