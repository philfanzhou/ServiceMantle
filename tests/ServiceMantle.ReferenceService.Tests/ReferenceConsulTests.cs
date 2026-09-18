using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.Configuration;
using ServiceMantle.Consul;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Discovery;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Drives the sample's optional Consul registration wiring against a real PostgreSQL database
/// with the transport replaced by a recording factory: the disabled path with zero clients, the
/// combination validation of discovery settings, the snapshot activation boundary, the
/// readiness-gated registration and deregistration, retry, shutdown, the restart-only setting
/// refresh, and the sensitive-token boundary.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceConsulTests : IAsyncLifetime
{
    private const string SettingsPath = "/management/v1/settings";
    private const string LoginPath = "/management/v1/session/login";
    private const string InstallationTable = "service_installations";
    private const string WorkspacesTable = "reference_workspaces";
    private const string UnsafeRequestHeader = "X-ServiceMantle-Request";

    private const string AdminId = "ops-admin";
    private const string AdminCredential = "synthetic-consul-admin-secret";
    private const string SyntheticUser = "reference_consul_owner";
    private const string SyntheticPassword = "synthetic-reference-consul-secret";
    private const string RootKey = "synthetic-reference-management-root-key";
    private const string OtherRootKey = "synthetic-reference-management-other-root-key";

    // The ACL token is asserted to stay out of every log line, exception, and response.
    private const string Token = "synthetic-consul-acl-token-canary";

    private static CancellationToken Token0 => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? container;
    private string? maintenanceConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("reference_consul_maintenance")
            .WithUsername(SyntheticUser)
            .WithPassword(SyntheticPassword)
            .Build();
        await container.StartAsync(Token0);
        maintenanceConnectionString = container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(Token0);
            await container.DisposeAsync();
        }
    }

    // --- D1: the gate on, the registration off --------------------------------------------------

    [Fact]
    public async Task D1_WithTheGateOnAndTheRegistrationOffNoConsulTypeExists()
    {
        var database = await CreateTargetAsync("switch-off");
        await using var app = await StartAppAsync(database, consul: false, new RecordingFactory());

        Assert.DoesNotContain(
            app.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "ConsulRegistrationLifecycle");
        Assert.Null(app.Services.GetService<ConsulClientProvider>());
        Assert.Null(app.Services.GetService<IConsulClientFactory>());
    }

    // --- D3: disabled means zero clients and zero remote calls -----------------------------------

    [Fact]
    public async Task D3_DisabledDiscoveryCreatesNoClientAndMakesNoRemoteCall()
    {
        var database = await CreateTargetAsync("disabled");
        var factory = new RecordingFactory();
        await using var app = await StartAppAsync(database, consul: true, factory);

        // Three readiness sampling intervals pass with the default one-second interval.
        await Task.Delay(TimeSpan.FromSeconds(3.5), Token0);

        Assert.Equal(0, factory.Creates);
        Assert.Empty(factory.Calls);
    }

    // --- D4: the combination validation of discovery settings -----------------------------------

    [Fact]
    public async Task D4_AnIncompleteEnabledCombinationIsRejectedAndACompleteOnePersists()
    {
        var (app, client, cookie, database) = await ReadyAsync("composite");
        await using var ownedApp = app;

        using var incomplete = await PostSettingsAsync(client, cookie, """
            {"expectedVersion":0,"changes":[{"key":"discovery.enabled","value":"true"}]}
            """);
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        var body = await incomplete.Content.ReadAsStringAsync(Token0);
        Assert.Contains("management.request.invalid", body, StringComparison.Ordinal);
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), "SELECT count(*)::text FROM service_settings"));

        using var complete = await PostSettingsAsync(client, cookie, """
            {"expectedVersion":0,"changes":[
                {"key":"discovery.enabled","value":"true"},
                {"key":"discovery.endpoint","value":"http://127.0.0.1:8500/"},
                {"key":"discovery.credential","value":"REPLACED_TOKEN"},
                {"key":"discovery.service-name","value":"reference-service"},
                {"key":"discovery.address","value":"10.0.0.5"},
                {"key":"discovery.port","value":"8080"}]}
            """.Replace("REPLACED_TOKEN", Token));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var stored = Assert.Single(await ReadStringsAsync(
            Target(database),
            "SELECT values_json::text FROM service_settings"));
        Assert.Contains("sm:v1:", stored, StringComparison.Ordinal);
        Assert.Contains("discovery.credential", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, stored, StringComparison.Ordinal);
    }

    // --- D5: the snapshot activation boundary ---------------------------------------------------

    [Fact]
    public async Task D5_AnUndecryptableCredentialFailsTheConsulStartupWithCodesOnly()
    {
        var database = await CreateTargetAsync("activation");

        // The credential is written under one root key, then the host restarts under another.
        await using (var writer = await StartAppAsync(database, consul: true, new RecordingFactory(), RootKey))
        {
            await ExecuteAsync(Target(database), CompleteInstallationSql());
            var client = writer.GetTestClient();
            var cookie = await LoginAsync(client);
            using var write = await PostSettingsAsync(client, cookie, FullCombo(version: 0));
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAppAsync(database, consul: true, new RecordingFactory(), OtherRootKey));
        Assert.Contains("configuration.snapshot", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, failure.Message, StringComparison.Ordinal);

        // With the registration off, the same database starts fine: nothing else reads the snapshot.
        await using (var unaffected = await StartAppAsync(database, consul: false, new RecordingFactory(), OtherRootKey))
        {
            Assert.NotNull(unaffected.Services.GetService<IServiceSettingCurrentSnapshotAccessor>());
        }
    }

    // --- D6 + D7: readiness-gated registration and deregistration --------------------------------

    [Fact]
    public async Task D6_RegistrationWaitsForReadinessAndCarriesTheInstanceId()
    {
        var database = await CreateTargetAsync("register");
        await WriteFullComboAsync(database);
        var factory = new RecordingFactory();
        await using var app = await StartAppAsync(database, consul: true, factory);

        // The client exists with its token, but a not-ready host never registers.
        await WaitAsync(() => factory.Creates >= 1);
        Assert.True(factory.LastHasToken);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Token0);
        Assert.DoesNotContain(
            factory.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));

        await ExecuteAsync(Target(database), $"INSERT INTO {WorkspacesTable} (\"Id\", \"DisplayName\") VALUES (gen_random_uuid(), 'w')");
        await WaitAsync(() => factory.RegisterCalls >= 1);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Token0);
        Assert.Equal(["register:reference-service:reference-local:reference-service"], factory.Registrations);

        // A second instance identity changes the registration id and nothing else.
        var databaseB = await CreateTargetAsync("register-b");
        await WriteFullComboAsync(databaseB);
        var factoryB = new RecordingFactory();
        await using var appB = await StartAppAsync(
            databaseB, consul: true, factoryB, instanceId: "reference-b");
        await WaitAsync(() => factoryB.Creates >= 1);
        await ExecuteAsync(Target(databaseB), $"INSERT INTO {WorkspacesTable} (\"Id\", \"DisplayName\") VALUES (gen_random_uuid(), 'w')");
        await WaitAsync(() => factoryB.RegisterCalls >= 1);
        Assert.Equal(["register:reference-service:reference-b:reference-service"], factoryB.Registrations);
    }

    [Fact]
    public async Task D7_LosingReadinessDeregistersAndRecoveryRegistersAgain()
    {
        var database = await CreateTargetAsync("ready-cycle");
        await WriteFullComboAsync(database);
        var factory = new RecordingFactory();
        await using var app = await StartAppAsync(database, consul: true, factory);
        await ExecuteAsync(Target(database), $"INSERT INTO {WorkspacesTable} (\"Id\", \"DisplayName\") VALUES (gen_random_uuid(), 'w')");
        await WaitAsync(() => factory.RegisterCalls >= 1);

        await ExecuteAsync(Target(database), $"DELETE FROM {WorkspacesTable}");
        await WaitAsync(() => factory.DeregisterCalls >= 1);
        Assert.Equal(
            "deregister:reference-service:reference-local",
            factory.Calls.ElementAt(1));

        await ExecuteAsync(Target(database), $"INSERT INTO {WorkspacesTable} (\"Id\", \"DisplayName\") VALUES (gen_random_uuid(), 'w')");
        await WaitAsync(() => factory.RegisterCalls >= 2);
    }

    // --- D8: transport retry ---------------------------------------------------------------------

    [Fact]
    public async Task D8_AnUnavailableRegisterRetriesUntilItSucceedsWithoutOverlap()
    {
        var database = await CreateTargetAsync("retry");
        await WriteFullComboAsync(database);
        var factory = new RecordingFactory
        {
            RegisterResults =
            [
                ConsulClientResult.Unavailable,
                ConsulClientResult.Success,
            ],
            RegisterDelay = TimeSpan.FromMilliseconds(150),
        };
        await using var app = await StartAppAsync(database, consul: true, factory);
        await ExecuteAsync(Target(database), $"INSERT INTO {WorkspacesTable} (\"Id\", \"DisplayName\") VALUES (gen_random_uuid(), 'w')");

        await WaitAsync(() => factory.RegisterCalls >= 2 && factory.FinalState == ConsulClientResult.Success);
        await Task.Delay(TimeSpan.FromSeconds(1), Token0);
        Assert.Equal(2, factory.RegisterCalls);
        Assert.Equal(1, factory.MaxInFlight);
    }

    // --- D9: shutdown deregisters ----------------------------------------------------------------

    [Fact]
    public async Task D9_StoppingARegisteredHostDeregistersTheSameIdAndDisposesTheSession()
    {
        var database = await CreateTargetAsync("shutdown");
        await WriteFullComboAsync(database);
        var factory = new RecordingFactory();
        var app = await StartAppAsync(database, consul: true, factory);
        await ExecuteAsync(Target(database), $"INSERT INTO {WorkspacesTable} (\"Id\", \"DisplayName\") VALUES (gen_random_uuid(), 'w')");
        await WaitAsync(() => factory.RegisterCalls >= 1);

        await app.StopAsync(Token0);
        await using (app)
        {
            Assert.Equal(
                "deregister:reference-service:reference-local",
                factory.Calls.ElementAt(^1));
            Assert.True(factory.ClientDisposed);
        }
    }

    // --- D10: settings only take effect after a restart ------------------------------------------

    [Fact]
    public async Task D10_ARuntimeSettingWriteDoesNotChangeTheLiveRegistration()
    {
        var database = await CreateTargetAsync("hot-reload");
        await WriteFullComboAsync(database);
        var factory = new RecordingFactory();
        await using var app = await StartAppAsync(database, consul: true, factory);
        await ExecuteAsync(Target(database), $"INSERT INTO {WorkspacesTable} (\"Id\", \"DisplayName\") VALUES (gen_random_uuid(), 'w')");
        await WaitAsync(() => factory.RegisterCalls >= 1);

        var client = app.GetTestClient();
        var cookie = await LoginAsync(client);
        var version = Assert.Single(await ReadStringsAsync(
            Target(database),
            "SELECT version::text FROM service_settings"));
        using var renamed = await PostSettingsAsync(client, cookie, $$"""
            {"expectedVersion":{{version}},"changes":[{"key":"discovery.service-name","value":"renamed-service"}]}
            """);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(3), Token0);
        Assert.Equal(["reference-service"], factory.RegisteredNames);

        var factoryAfterRestart = new RecordingFactory();
        await using var restarted = await StartAppAsync(database, consul: true, factoryAfterRestart);
        await WaitAsync(() => factoryAfterRestart.RegisterCalls >= 1);
        Assert.Equal(["renamed-service"], factoryAfterRestart.RegisteredNames);
    }

    // --- D11: the sensitive-token boundary -------------------------------------------------------

    [Fact]
    public async Task D11_TheTokenReachesNeitherLogsNorExceptions()
    {
        var database = await CreateTargetAsync("secrets");
        await WriteFullComboAsync(database);
        var logs = new CapturingLoggerProvider();
        var factory = new RecordingFactory
        {
            RegisterResults = [ConsulClientResult.Unavailable, ConsulClientResult.Success],
        };
        await using var app = await StartAppAsync(database, consul: true, factory, loggerProvider: logs);
        await ExecuteAsync(Target(database), $"INSERT INTO {WorkspacesTable} (\"Id\", \"DisplayName\") VALUES (gen_random_uuid(), 'w')");
        await WaitAsync(() => factory.RegisterCalls >= 2);

        foreach (var message in logs.Messages)
        {
            Assert.DoesNotContain(Token, message, StringComparison.Ordinal);
        }
    }

    // --- helpers --------------------------------------------------------------------------------

    /// <summary>Starts a migrated host, completes the installation by SQL, and writes the full
    /// discovery combination through the management API.</summary>
    private async Task WriteFullComboAsync(string database)
    {
        // The discovery definitions exist only while the Consul switch is on, so the writer runs
        // with the registration enabled; with discovery disabled by default it stays inert.
        await using var writer = await StartAppAsync(database, consul: true, new RecordingFactory());
        await ExecuteAsync(Target(database), CompleteInstallationSql());
        var client = writer.GetTestClient();
        var cookie = await LoginAsync(client);
        using var write = await PostSettingsAsync(client, cookie, FullCombo(version: 0));
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
    }

    private static string FullCombo(int version) => """
        {"expectedVersion":0,"changes":[
            {"key":"discovery.enabled","value":"true"},
            {"key":"discovery.endpoint","value":"http://127.0.0.1:8500/"},
            {"key":"discovery.credential","value":"REPLACED_TOKEN"},
            {"key":"discovery.service-name","value":"reference-service"},
            {"key":"discovery.address","value":"10.0.0.5"},
            {"key":"discovery.port","value":"8080"}]}
        """.Replace("REPLACED_TOKEN", Token).Replace("\"expectedVersion\":0", $"\"expectedVersion\":{version}");

    private async Task<(WebApplication App, HttpClient Client, string Cookie, string Database)> ReadyAsync(
        string name)
    {
        var database = await CreateTargetAsync(name);
        var app = await StartAppAsync(database, consul: true, new RecordingFactory());
        await ExecuteAsync(Target(database), CompleteInstallationSql());
        var client = app.GetTestClient();
        var cookie = await LoginAsync(client);
        return (app, client, cookie, database);
    }

    private async Task<WebApplication> StartAppAsync(
        string database,
        bool consul,
        RecordingFactory factory,
        string? rootKey = null,
        string? instanceId = null,
        ILoggerProvider? loggerProvider = null)
    {
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, Target(database),
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
            "--environment", "Production",
            "--ReferenceService:Management:RootKey", rootKey ?? RootKey,
            "--ReferenceService:Management:Operators:0:Id", AdminId,
            "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
            "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
            "--ReferenceService:Management:Operators:0:Credential", AdminCredential,
        };
        if (consul)
        {
            arguments.Add("--" + ReferenceConsulDefaults.EnabledKey);
            arguments.Add("true");
        }

        if (instanceId is not null)
        {
            arguments.Add("--" + ReferenceConsulDefaults.InstanceIdKey);
            arguments.Add(instanceId);
        }

        var builder = ReferenceApplication.CreateBuilder([.. arguments]);
        builder.WebHost.UseTestServer();
        if (consul)
        {
            builder.Services.AddSingleton<IConsulClientFactory>(factory);
        }
        if (loggerProvider is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(loggerProvider);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
        }

        var app = ReferenceApplication.Build(builder);
        try
        {
            await app.StartAsync(Token0);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static string CompleteInstallationSql() =>
        $"""
        UPDATE public."{InstallationTable}" SET status = 1, completed_at_utc = now()
        WHERE service_id = 'reference-service'
        """;

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(
                $$"""{"username":"{{AdminId}}","secret":"{{AdminCredential}}"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        using var login = await client.SendAsync(request, Token0);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
    }

    private static Task<HttpResponseMessage> PostSettingsAsync(HttpClient client, string cookie, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SettingsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request, Token0);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "the expected Consul interaction did not happen in time");
            await Task.Delay(TimeSpan.FromMilliseconds(50), Token0);
        }
    }

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private async Task<string> CreateTargetAsync(string name)
    {
        RequireDatabase();
        var database = DatabaseName(name);
        await ExecuteAsync(maintenanceConnectionString!, $"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        await ExecuteAsync(maintenanceConnectionString!, $"""CREATE DATABASE "{database}" """);
        return database;
    }

    private static string DatabaseName(string name) => $"reference_consul_{name}";

    private string Target(string database) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
        }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token0);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token0);
    }

    private static async Task<List<string>> ReadStringsAsync(string connectionString, string query)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token0);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token0);
        while (await reader.ReadAsync(Token0))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    /// <summary>Records client creations and remote calls without any real transport.</summary>
    private sealed class RecordingFactory : IConsulClientFactory
    {
        private readonly ConcurrentQueue<string> calls = [];
        private readonly ConcurrentQueue<string> registrations = [];
        private int creates;
        private int registerCalls;
        private int deregisterCalls;
        private int inFlight;
        private int maxInFlight;
        private bool clientDisposed;

        internal IReadOnlyList<ConsulClientResult>? RegisterResults { get; set; }

        internal TimeSpan RegisterDelay { get; set; } = TimeSpan.Zero;

        internal int Creates => Volatile.Read(ref creates);

        internal int RegisterCalls => Volatile.Read(ref registerCalls);

        internal int DeregisterCalls => Volatile.Read(ref deregisterCalls);

        internal bool LastHasToken { get; private set; }

        internal ConsulClientResult FinalState { get; private set; } = ConsulClientResult.Unavailable;

        internal int MaxInFlight => Volatile.Read(ref maxInFlight);

        internal bool ClientDisposed => Volatile.Read(ref clientDisposed);

        internal IReadOnlyList<string> Calls
        {
            get
            {
                lock (calls)
                {
                    return [.. calls];
                }
            }
        }

        internal IReadOnlyList<string> Registrations
        {
            get
            {
                lock (registrations)
                {
                    return [.. registrations];
                }
            }
        }

        internal IReadOnlyList<string> RegisteredNames =>
            Registrations.Select(call => call.Split(':')[^1]).ToList();

        public IConsulClient Create(ConsulClientConfiguration configuration)
        {
            Interlocked.Increment(ref creates);
            LastHasToken = configuration.HasToken;
            return new Client(this);
        }

        private sealed class Client(RecordingFactory owner) : IConsulClient
        {
            private int registerIndex;

            public async ValueTask<ConsulClientResult> RegisterAsync(
                ConsulServiceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                var current = Interlocked.Increment(ref owner.registerCalls);
                Enter();
                try
                {
                    if (owner.RegisterDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(owner.RegisterDelay, cancellationToken);
                    }

                    lock (owner.calls)
                    {
                        owner.calls.Enqueue("register:" + registration.Id + ":" + registration.Name);
                    }

                    lock (owner.registrations)
                    {
                        owner.registrations.Enqueue("register:" + registration.Id + ":" + registration.Name);
                    }

                    var result = owner.RegisterResults is { } results && registerIndex < results.Count
                        ? results[registerIndex++]
                        : ConsulClientResult.Success;
                    owner.FinalState = result;
                    return result;
                }
                finally
                {
                    Exit();
                }
            }

            public ValueTask<ConsulClientResult> DeregisterAsync(
                string registrationId,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner.deregisterCalls);
                lock (owner.calls)
                {
                    owner.calls.Enqueue("deregister:" + registrationId);
                }

                return ValueTask.FromResult(ConsulClientResult.Success);
            }

            public void Dispose() => Volatile.Write(ref owner.clientDisposed, true);

            private void Enter()
            {
                var current = Interlocked.Increment(ref owner.inFlight);
                int observed;
                do
                {
                    observed = Volatile.Read(ref owner.maxInFlight);
                }
                while (current > observed && Interlocked.CompareExchange(ref owner.maxInFlight, current, observed) != observed);
            }

            private void Exit() => Interlocked.Decrement(ref owner.inFlight);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> messages = [];

        internal IReadOnlyList<string> Messages
        {
            get
            {
                lock (messages)
                {
                    return [.. messages];
                }
            }
        }

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
