using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Management;
using ServiceMantle.ReferenceService.Health.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the reference service's live health wiring end to end against a real PostgreSQL server and
/// the real <see cref="ReferenceApplication"/> composition: the registration shape, the phase and
/// business-readiness matrix projected through <c>/health/live</c>, <c>/health/ready</c>, and
/// <c>/health</c>, the fail-closed exits, per-request re-reads without caching, concurrency, the
/// probe timeout and caller-cancellation boundaries, and the absence of any secret in a response or
/// a captured log line.
/// </summary>
/// <remarks>
/// The host is composed through the real <see cref="ReferenceApplication.CreateBuilder"/> seam on a
/// test server exactly as the sample registers it, so the PostgreSQL startup gate migrates the target
/// and publishes its Ready result before the host accepts a request. Database facts - the installation
/// row and the workspace rows - are then written with test SQL, never through any setup workflow, so
/// the assertions observe the running sample's own health projections rather than a test-injected
/// snapshot. This follows the shared real-database policy: <c>RUN_SERVICEMANTLE_POSTGRES_TESTS=true</c>
/// and a running Docker daemon; when the environment is explicitly required and unavailable the tests
/// fail rather than skip.
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferencePostgreSqlHealthTests : IAsyncLifetime
{
    private const string InstallationTable = "service_installations";
    private const string WorkspaceTable = "reference_workspaces";
    private const string ServiceIdValue = "reference-service";

    // Synthetic fixture secrets. They exist only inside this container and are asserted to stay out
    // of every health response body and every captured log line.
    private const string SyntheticUser = "reference_health_owner";
    private const string SyntheticPassword = "synthetic-reference-health-secret";

    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(30);

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
            .WithDatabase("reference_health_maintenance")
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

    // --- C2: registration shape ----------------------------------------------------------

    [Fact]
    public async Task An_enabled_gate_registers_exactly_one_source_and_one_business_contributor()
    {
        RequireDatabase();
        var database = DatabaseName("registration_shape");
        await CreateDatabaseAsync(database);

        await using var app = await StartAppAsync(database);

        Assert.IsType<ReferencePostgreSqlHealthSnapshotSource>(
            app.Services.GetService<IServiceHealthSnapshotSource>());
        var contributors = app.Services.GetServices<IServiceReadinessContributor>().ToArray();
        Assert.IsType<ReferencePostgreSqlWorkspaceReadinessContributor>(Assert.Single(contributors));
        // The host started, so HealthStartupValidator accepted the single-order contributor set.
        Assert.NotNull(app.Services.GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result);
    }

    [Fact]
    public async Task The_host_builds_and_starts_in_the_Development_environment_with_scope_validation()
    {
        RequireDatabase();
        var database = DatabaseName("development_environment");
        await CreateDatabaseAsync(database);

        await using var app = await StartAppAsync(database, environment: "Development");
        Assert.True(app.Environment.IsDevelopment());
        using var client = app.GetTestClient();

        using var live = await client.GetAsync("/health/live", Token);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    // --- C3: phase and business matrix ---------------------------------------------------

    [Fact]
    public async Task A_pending_installation_reports_503_pending_setup_on_both_readiness_routes()
    {
        RequireDatabase();
        var database = DatabaseName("pending_setup");
        await CreateDatabaseAsync(database);
        var recorder = new RecordingLoggerProvider();

        await using var app = await StartAppAsync(database, recorder);
        using var client = app.GetTestClient();

        foreach (var path in new[] { "/health/ready", "/health" })
        {
            var (status, body) = await GetJsonAsync(client, path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.Equal("not_ready", body.Str("status"));
            Assert.Equal("pendingSetup", body.Str("phase"));
            Assert.Equal("succeeded", body.Str("migrationStatus"));
            Assert.Equal("reachable", body.Str("databaseStatus"));
            Assert.True(body.IsNull("errorCode"));
        }

        // The live route never resolves the decision source and is always 200.
        await AssertLiveOkAsync(client);
        AssertNoSecret(recorder, await ReadBodyAsync(client, "/health/ready"), Target(database));
    }

    [Fact]
    public async Task A_completed_installation_with_a_workspace_reports_200_ready()
    {
        RequireDatabase();
        var database = DatabaseName("completed_ready");
        await CreateDatabaseAsync(database);
        var recorder = new RecordingLoggerProvider();

        await using var app = await StartAppAsync(database, recorder);
        var target = Target(database);
        await CompleteInstallationAsync(target);
        await InsertWorkspaceAsync(target);
        using var client = app.GetTestClient();

        foreach (var path in new[] { "/health/ready", "/health" })
        {
            var (status, body) = await GetJsonAsync(client, path);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ready", body.Str("status"));
            Assert.Equal("completed", body.Str("phase"));
            Assert.True(body.IsNull("errorCode"));
        }

        await AssertLiveOkAsync(client);
        AssertNoSecret(recorder, await ReadBodyAsync(client, "/health/ready"), target);
    }

    [Fact]
    public async Task A_completed_installation_with_an_empty_workspace_reports_workspace_missing()
    {
        RequireDatabase();
        var database = DatabaseName("completed_no_workspace");
        await CreateDatabaseAsync(database);

        await using var app = await StartAppAsync(database);
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();

        var (status, body) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("not_ready", body.Str("status"));
        Assert.Equal("completed", body.Str("phase"));
        Assert.Equal("reference.workspace_missing", body.Str("errorCode"));
        await AssertLiveOkAsync(client);
    }

    [Fact]
    public async Task A_completed_installation_with_an_unreadable_workspace_reports_workspace_probe_failed()
    {
        RequireDatabase();
        var database = DatabaseName("unreadable_workspace");
        await CreateDatabaseAsync(database);

        await using var app = await StartAppAsync(database);
        var target = Target(database);
        await CompleteInstallationAsync(target);
        // Drop the business table so the contributor's existence read cannot be made; the source's
        // installation read is untouched, so the base snapshot is still a completed phase.
        await ExecuteAsync(target, $"DROP TABLE public.\"{WorkspaceTable}\"");
        using var client = app.GetTestClient();

        var (status, body) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("completed", body.Str("phase"));
        Assert.Equal("reference.workspace_probe_failed", body.Str("errorCode"));
        await AssertLiveOkAsync(client);
    }

    // --- C4: fail closed -----------------------------------------------------------------

    [Fact]
    public async Task A_deleted_installation_row_fails_closed_with_a_null_phase_then_recovers()
    {
        RequireDatabase();
        var database = DatabaseName("deleted_row");
        await CreateDatabaseAsync(database);

        await using var app = await StartAppAsync(database);
        var target = Target(database);
        using var client = app.GetTestClient();

        await ExecuteAsync(target, $"DELETE FROM public.\"{InstallationTable}\" WHERE service_id = '{ServiceIdValue}'");
        var (status, body) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.True(body.IsNull("phase"));
        Assert.Equal("health.probe_failed", body.Str("errorCode"));

        // Restoring a row recovers on the next request without a restart. A restored pending row is
        // reported as pending setup; the deleted row was never silently resolved to a phase.
        await InsertPendingInstallationAsync(target);
        var (recovered, recoveredBody) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, recovered);
        Assert.Equal("pendingSetup", recoveredBody.Str("phase"));
    }

    [Fact]
    public async Task An_invalid_installation_status_fails_closed_with_a_null_phase_then_recovers()
    {
        RequireDatabase();
        var database = DatabaseName("invalid_status");
        await CreateDatabaseAsync(database);

        await using var app = await StartAppAsync(database);
        var target = Target(database);
        using var client = app.GetTestClient();

        await ExecuteAsync(target, $"UPDATE public.\"{InstallationTable}\" SET status = 99 WHERE service_id = '{ServiceIdValue}'");
        var (status, body) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.True(body.IsNull("phase"));
        Assert.Equal("health.probe_failed", body.Str("errorCode"));

        await ExecuteAsync(target, $"UPDATE public.\"{InstallationTable}\" SET status = 0 WHERE service_id = '{ServiceIdValue}'");
        var (recovered, recoveredBody) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, recovered);
        Assert.Equal("pendingSetup", recoveredBody.Str("phase"));
    }

    [Fact]
    public async Task A_database_that_refuses_connections_fails_closed_then_recovers()
    {
        RequireDatabase();
        var database = DatabaseName("refused_connections");
        await CreateDatabaseAsync(database);
        var recorder = new RecordingLoggerProvider();

        await using var app = await StartAppAsync(database, recorder);
        using var client = app.GetTestClient();

        // Refuse new connections to the target and terminate the pooled sessions the app holds.
        await ExecuteOnMaintenanceAsync($"""UPDATE pg_database SET datallowconn = false WHERE datname = '{database}'""");
        await ExecuteOnMaintenanceAsync(
            $"""SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}' AND pid <> pg_backend_pid()""");

        var (status, body) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.True(body.IsNull("phase"));
        Assert.Equal("health.probe_failed", body.Str("errorCode"));
        // The connection-refusal error path is the highest leak risk: neither the response nor any
        // captured log line may carry the connection string, the password, or the user name.
        AssertNoSecret(recorder, await ReadBodyAsync(client, "/health/ready"), Target(database));

        await ExecuteOnMaintenanceAsync($"""UPDATE pg_database SET datallowconn = true WHERE datname = '{database}'""");
        var (recovered, recoveredBody) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, recovered);
        Assert.Equal("pendingSetup", recoveredBody.Str("phase"));
    }

    // --- C5: no caching ------------------------------------------------------------------

    [Fact]
    public async Task Completing_the_installation_while_running_is_reflected_without_a_restart()
    {
        RequireDatabase();
        var database = DatabaseName("no_caching");
        await CreateDatabaseAsync(database);

        await using var app = await StartAppAsync(database);
        var target = Target(database);
        using var client = app.GetTestClient();

        var (before, beforeBody) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before);
        Assert.Equal("pendingSetup", beforeBody.Str("phase"));

        // One atomic flip: the row becomes Completed and a workspace appears together, so the next
        // request observes a single complete projection rather than an in-between state.
        await FlipToCompletedWithWorkspaceAsync(target);

        var (after, afterBody) = await GetJsonAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.OK, after);
        Assert.Equal("ready", afterBody.Str("status"));
        Assert.Equal("completed", afterBody.Str("phase"));
    }

    // --- C6: concurrency -----------------------------------------------------------------

    [Fact]
    public async Task Concurrent_reads_during_a_flip_are_complete_projections_and_own_their_contexts()
    {
        RequireDatabase();
        var database = DatabaseName("concurrency");
        await CreateDatabaseAsync(database);
        var target = Target(database);
        var factory = new HealthProbeContextFactory(target);

        await using var app = await StartAppAsync(
            database,
            extraServices: services =>
                services.AddSingleton<IDbContextFactory<ReferencePostgreSqlDbContext>>(factory));
        using var client = app.GetTestClient();
        factory.Reset();

        var responses = new List<(HttpStatusCode Status, JsonElement Body)>();
        var requests = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            var result = await GetJsonAsync(client, "/health/ready");
            lock (responses)
            {
                responses.Add(result);
            }
        })).Append(FlipToCompletedWithWorkspaceAsync(target)).ToArray();
        await Task.WhenAll(requests);

        Assert.Equal(16, responses.Count);
        foreach (var (status, body) in responses)
        {
            // Each response is one of the two complete projections the model allows during the flip:
            // either the pre-flip pending setup or the post-flip ready completed, never a mix.
            if (status == HttpStatusCode.OK)
            {
                Assert.Equal("ready", body.Str("status"));
                Assert.Equal("completed", body.Str("phase"));
                Assert.True(body.IsNull("errorCode"));
            }
            else
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
                Assert.Equal("pendingSetup", body.Str("phase"));
                Assert.Equal("succeeded", body.Str("migrationStatus"));
                Assert.True(body.IsNull("errorCode"));
            }
        }

        // Every context a read created was released; the source never captured or shared one.
        Assert.True(factory.Creations >= 16, $"expected at least one context per read, saw {factory.Creations}");
        Assert.Equal(factory.Creations, factory.Closes);
    }

    // --- C7: timeout and caller cancellation at the endpoint -----------------------------

    [Fact]
    public async Task A_read_that_outlives_the_probe_budget_is_a_probe_timeout_observed_by_the_double()
    {
        RequireDatabase();
        var database = DatabaseName("probe_timeout");
        await CreateDatabaseAsync(database);
        var factory = new HealthProbeContextFactory(Target(database));

        await using var app = await StartAppAsync(
            database,
            extraServices: services =>
                services.AddSingleton<IDbContextFactory<ReferencePostgreSqlDbContext>>(factory));
        using var client = app.GetTestClient();
        try
        {
            // Block only the health reads, after the gate has already migrated and started.
            factory.BlockCommands = true;
            var (status, body) = await GetJsonAsync(client, "/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.True(body.IsNull("phase"));
            Assert.Equal("health.probe_timeout", body.Str("errorCode"));
            // The double observed the budget cancellation on the token it received.
            Assert.True(factory.ObservedToken.IsCancellationRequested);
        }
        finally
        {
            await factory.ReleaseAsync();
        }
    }

    [Fact]
    public async Task A_caller_cancellation_ends_the_request_with_its_own_token()
    {
        RequireDatabase();
        var database = DatabaseName("caller_cancellation");
        await CreateDatabaseAsync(database);
        var factory = new HealthProbeContextFactory(Target(database));
        var observation = new HandlerObservation();

        await using var app = await StartAppAsync(
            database,
            extraServices: services =>
                services.AddSingleton<IDbContextFactory<ReferencePostgreSqlDbContext>>(factory),
            observe: observation);
        factory.BlockCommands = true;
        using var abort = new CancellationTokenSource();

        var request = app.GetTestServer().SendAsync(
            context =>
            {
                context.Request.Method = "GET";
                context.Request.Path = "/health/ready";
                context.RequestAborted = abort.Token;
            },
            Token);
        await factory.WaitForBlockedCommandAsync();
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var failure = await observation.Failure.Task.WaitAsync(Observation, Token);
        Assert.True(failure.CarriesRequestToken);
        Assert.Equal(abort.Token, failure.Error.CancellationToken);
        await factory.ReleaseAsync();
    }

    // --- Helpers -------------------------------------------------------------------------

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private async Task<WebApplication> StartAppAsync(
        string database,
        RecordingLoggerProvider? recorder = null,
        Action<IServiceCollection>? extraServices = null,
        string? environment = null,
        HandlerObservation? observe = null)
    {
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, Target(database),
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
            // The management session rides the gate, so the root key is a required input here too.
            "--" + ReferenceManagementOptions.RootKeySetting, "synthetic-reference-management-root-key",
        };
        if (environment is not null)
        {
            arguments.AddRange(["--environment", environment]);
        }

        var builder = ReferenceApplication.CreateBuilder([.. arguments]);
        builder.WebHost.UseTestServer();
        if (recorder is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(recorder);
            builder.Logging.SetMinimumLevel(LogLevel.Information);
        }

        extraServices?.Invoke(builder.Services);
        var app = ReferenceApplication.Build(builder);
        if (observe is not null)
        {
            app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (OperationCanceledException error)
                {
                    observe.Failure.TrySetResult(
                        new HandlerFailure(error, error.CancellationToken == context.RequestAborted));
                    throw;
                }
            });
        }

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

    private string Target(string database) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
            IncludeErrorDetail = false,
        }.ConnectionString;

    private static string DatabaseName(string name) => $"reference_health_{name}";

    private async Task CreateDatabaseAsync(string database)
    {
        RequireDatabase();
        await ExecuteOnMaintenanceAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        await ExecuteOnMaintenanceAsync($"""CREATE DATABASE "{database}" """);
    }

    private Task ExecuteOnMaintenanceAsync(string statement) =>
        ExecuteAsync(maintenanceConnectionString!, statement);

    private static async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static Task CompleteInstallationAsync(string target) =>
        ExecuteAsync(target, $"""
            UPDATE public."{InstallationTable}" SET status = 1, completed_at_utc = now() WHERE service_id = '{ServiceIdValue}'
            """);

    private static Task InsertPendingInstallationAsync(string target) =>
        ExecuteAsync(target, $"""
            INSERT INTO public."{InstallationTable}"
                (service_id, status, created_at_utc, completed_at_utc, version, setup_code_generation)
            VALUES ('{ServiceIdValue}', 0, now(), NULL, 1, 0)
            """);

    private static Task InsertWorkspaceAsync(string target) =>
        ExecuteAsync(target, $"""
            INSERT INTO public."{WorkspaceTable}" ("Id", "DisplayName") VALUES (gen_random_uuid(), 'seeded')
            """);

    private static Task FlipToCompletedWithWorkspaceAsync(string target) =>
        ExecuteAsync(target, $"""
            BEGIN;
            UPDATE public."{InstallationTable}" SET status = 1, completed_at_utc = now() WHERE service_id = '{ServiceIdValue}';
            INSERT INTO public."{WorkspaceTable}" ("Id", "DisplayName") VALUES (gen_random_uuid(), 'seeded');
            COMMIT;
            """);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetJsonAsync(
        HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        var body = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text).RootElement.Clone();
        return (response.StatusCode, body);
    }

    private static async Task<string> ReadBodyAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Token);
        return await response.Content.ReadAsStringAsync(Token);
    }

    /// <summary>The live route is stateless: it is 200 on every phase and never reads the database.</summary>
    private static async Task AssertLiveOkAsync(HttpClient client)
    {
        using var live = await client.GetAsync("/health/live", Token);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("live", JsonDocument.Parse(await live.Content.ReadAsStringAsync(Token)).RootElement.Str("status"));
    }

    private static void AssertNoSecret(
        RecordingLoggerProvider recorder, string body, string targetConnectionString)
    {
        Assert.DoesNotContain(targetConnectionString, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPassword, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticUser, body, StringComparison.Ordinal);
        foreach (var line in recorder.Lines)
        {
            Assert.DoesNotContain(targetConnectionString, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticUser, line, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    private sealed record HandlerFailure(OperationCanceledException Error, bool CarriesRequestToken);

    private sealed class HandlerObservation
    {
        internal TaskCompletionSource<HandlerFailure> Failure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Records every line the host writes during the run under test.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> lines = [];

        internal IReadOnlyList<string> Lines
        {
            get
            {
                lock (lines)
                {
                    return [.. lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Recorder(lines);

        public void Dispose()
        {
        }

        private sealed class Recorder(List<string> lines) : ILogger
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
                lock (lines)
                {
                    lines.Add(formatter(state, exception) + " " + exception);
                }
            }
        }
    }

    /// <summary>
    /// A context factory over the real target connection that counts creations and releases, and can
    /// hold a command open on the caller's own token so the pipeline's budget or the caller decides
    /// how the read ends. It creates real contexts, so the startup gate migrates normally while
    /// <see cref="BlockCommands"/> is off; only the health reads are held once it is on.
    /// </summary>
    private sealed class HealthProbeContextFactory(string connectionString)
        : IDbContextFactory<ReferencePostgreSqlDbContext>
    {
        private readonly TaskCompletionSource blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task blockedCommand = Task.CompletedTask;

        private int creations;

        private int closes;

        internal volatile bool BlockCommands;

        internal int Creations => Volatile.Read(ref creations);

        internal int Closes => Volatile.Read(ref closes);

        internal CancellationToken ObservedToken { get; private set; }

        public ReferencePostgreSqlDbContext CreateDbContext()
        {
            Interlocked.Increment(ref creations);
            return new ReferencePostgreSqlDbContext(
                new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
                    .UseNpgsql(connectionString)
                    .AddInterceptors(new ProbeInterceptor(this))
                    .Options);
        }

        internal async Task WaitForBlockedCommandAsync() =>
            await blocked.Task.WaitAsync(Observation, Token);

        internal async Task ReleaseAsync()
        {
            release.TrySetResult();
            try
            {
                await blockedCommand.WaitAsync(Observation, Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref creations, 0);
            Interlocked.Exchange(ref closes, 0);
        }

        private async Task OnCommandAsync(CancellationToken cancellationToken)
        {
            if (!BlockCommands)
            {
                return;
            }

            ObservedToken = cancellationToken;
            blocked.TrySetResult();
            blockedCommand = release.Task.WaitAsync(cancellationToken);
            await blockedCommand.ConfigureAwait(false);
        }

        private sealed class ProbeInterceptor(HealthProbeContextFactory factory)
            : DbCommandInterceptor, IDbConnectionInterceptor
        {
            public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                await factory.OnCommandAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }

            public ValueTask<InterceptionResult> ConnectionClosingAsync(
                DbConnection connection,
                ConnectionEventData eventData,
                InterceptionResult result)
            {
                Interlocked.Increment(ref factory.closes);
                return ValueTask.FromResult(result);
            }
        }
    }
}

/// <summary>Reads the fixed health response fields without tripping the instance GetString().</summary>
internal static class HealthResponseJson
{
    internal static string? Str(this JsonElement body, string name) =>
        body.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    internal static bool IsNull(this JsonElement body, string name) =>
        body.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Null;
}
