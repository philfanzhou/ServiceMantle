using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.Configuration;
using ServiceMantle.Management;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Configuration;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Management;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Accepts the sample's read-only setting query endpoints through its real
/// <see cref="ReferenceApplication"/> path against a real PostgreSQL server and real management
/// logins: the gate refusal, the definition and version-zero projections, the authorization
/// matrix without a store read, the sensitive-value projection, the wrong-root-key and corrupt
/// storage failures, caller cancellation, and the single-registry invariant.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceSettingQueryTests : IAsyncLifetime
{
    private const string DefinitionsPath = "/management/v1/settings/definitions";
    private const string ValuesPath = "/management/v1/settings";
    private const string LoginPath = "/management/v1/session/login";
    private const string InstallationTable = "service_installations";
    private const string WorkspaceTable = "reference_workspaces";
    private const string SettingsTable = "service_settings";
    private const string ServiceIdValue = "reference-service";
    private const string UnsafeRequestHeader = "X-ServiceMantle-Request";

    private const string AdminId = "ops-admin";
    private const string AdminCredential = "synthetic-settings-admin-secret";
    private const string ReaderId = "ops-reader";
    private const string ReaderCredential = "synthetic-settings-reader-secret";

    // Synthetic fixture secrets, asserted to stay out of every response and captured log line.
    private const string SyntheticUser = "reference_settings_owner";
    private const string SyntheticPassword = "synthetic-reference-settings-secret";
    private const string RootKey = "synthetic-reference-settings-root-key-value";
    private const string OtherRootKey = "another-synthetic-reference-root-key";
    private const string TokenPlaintext = "synthetic-integration-token-plaintext";

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
            .WithDatabase("reference_settings_maintenance")
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

    // --- A1: the gate off registers and maps nothing new ----------------------------------

    [Fact]
    public async Task The_gate_off_registers_and_maps_nothing_new()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sm-reference-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var builder = ReferenceApplication.CreateBuilder(
            [
                "--environment", "Production",
                "--ReferenceService:DatabasePath", Path.Combine(directory, "reference.db"),
            ]);
            builder.WebHost.UseTestServer();
            await using var app = await BuildAndStartAsync(builder);

            Assert.Null(app.Services.GetService<IServiceSettingStore>());
            Assert.Null(app.Services.GetService<IServiceSettingRootKeySource>());
            Assert.Null(app.Services.GetService<ServiceSettingQueryService>());
            var routes = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
                .ToList();
            Assert.Equal(["/"], routes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // --- A9: exactly one definition registry ------------------------------------------------

    [Fact]
    public async Task The_definition_registry_is_registered_exactly_once()
    {
        var database = await CreateReadyTargetAsync("registry");
        await using var app = await StartAppAsync(BaseArguments(database));

        Assert.Single(app.Services.GetServices<ServiceSettingDefinitionRegistry>());
    }

    // --- A2/A3: the definition catalog and the version-zero projection ----------------------

    [Fact]
    public async Task Definitions_and_version_zero_values_project_the_fixed_shapes()
    {
        var database = await CreateReadyTargetAsync("v0");
        var recorder = new CapturingLoggerProvider();
        await using var app = await StartAppAsync(BaseArguments(database), recorder);
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();
        var cookie = await LoginAsync(client, AdminId, AdminCredential);

        using var definitions = await SendAsync(client, HttpMethod.Get, DefinitionsPath, cookie);
        var definitionsBody = await definitions.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.OK, definitions.StatusCode);
        using var document = JsonDocument.Parse(definitionsBody);
        var definitionsRoot = document.RootElement;
        Assert.Equal("definitions", Assert.Single(definitionsRoot.EnumerateObject()).Name);
        var items = definitionsRoot.GetProperty("definitions").EnumerateArray().ToList();
        Assert.Equal(
        [
            "workspace.display_name",
            "workspace.integration_token",
            "workspace.item_limit",
        ], items.Select(item => item.GetProperty("key").GetString()));
        // Each item carries exactly the six fixed fields: no default value, no constraint object.
        Assert.All(items, item => Assert.Equal(6, item.EnumerateObject().Count()));
        Assert.True(items[1].GetProperty("isSensitive").GetBoolean());
        Assert.False(items[0].GetProperty("isSensitive").GetBoolean());
        Assert.False(items[1].GetProperty("hasDefault").GetBoolean());

        using var values = await SendAsync(client, HttpMethod.Get, ValuesPath, cookie);
        var valuesBody = await values.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.OK, values.StatusCode);
        using var valuesDocument = JsonDocument.Parse(valuesBody);
        var valuesRoot = valuesDocument.RootElement;
        Assert.Equal(0, valuesRoot.GetProperty("version").GetInt64());
        var valueItems = valuesRoot.GetProperty("values").EnumerateArray().ToList();
        Assert.Equal(3, valueItems.Count);
        var displayName = valueItems.Single(item =>
            item.GetProperty("key").GetString() == "workspace.display_name");
        Assert.Equal("default", displayName.GetProperty("source").GetString());
        Assert.Equal(ReferenceSettingDefinitions.DefaultDisplayName,
            displayName.GetProperty("value").GetString());
        var integrationToken = valueItems.Single(item =>
            item.GetProperty("key").GetString() == ReferenceSettingDefinitions.IntegrationTokenKey);
        Assert.False(integrationToken.GetProperty("hasValue").GetBoolean());
        Assert.Equal("missing", integrationToken.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, integrationToken.GetProperty("value").ValueKind);

        using var group = await SendAsync(client, HttpMethod.Get, ValuesPath + "?group=workspace.item_limit", cookie);
        Assert.Equal(HttpStatusCode.OK, group.StatusCode);
        using var groupDocument = JsonDocument.Parse(await group.Content.ReadAsStringAsync(Token));
        var groupItems = groupDocument.RootElement.GetProperty("values").EnumerateArray().ToList();
        var groupItem = Assert.Single(groupItems);
        Assert.Equal("workspace.item_limit", groupItem.GetProperty("key").GetString());

        AssertNoSecret(recorder, definitionsBody);
        AssertNoSecret(recorder, valuesBody);
    }

    // --- A4: unauthenticated and read-only operators never touch the store ------------------

    [Fact]
    public async Task Unauthenticated_is_401_and_read_only_is_403_without_a_store_read()
    {
        var database = await CreateReadyTargetAsync("authz");
        var probe = new StoreProbe();
        await using var app = await StartAppAsync(
            BaseArguments(database),
            extraServices: services => probe.Install(services));
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();

        using var anonymous = await client.GetAsync(ValuesPath, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var anonymousDefinitions = await client.GetAsync(DefinitionsPath, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousDefinitions.StatusCode);

        var readerCookie = await LoginAsync(client, ReaderId, ReaderCredential);
        using var reader = await SendAsync(client, HttpMethod.Get, ValuesPath, readerCookie);
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        Assert.Contains(
            "management.session.forbidden",
            await reader.Content.ReadAsStringAsync(Token),
            StringComparison.Ordinal);
        using var readerDefinitions = await SendAsync(client, HttpMethod.Get, DefinitionsPath, readerCookie);
        Assert.Equal(HttpStatusCode.Forbidden, readerDefinitions.StatusCode);

        Assert.Equal(0, probe.Loads);
    }

    // --- A5: the sensitive value is ciphertext at rest and null in the projection ------------

    [Fact]
    public async Task The_sensitive_value_is_ciphertext_at_rest_and_null_in_the_projection()
    {
        var database = await CreateReadyTargetAsync("sensitive");
        var recorder = new CapturingLoggerProvider();
        await using var app = await StartAppAsync(BaseArguments(database), recorder);
        await CompleteInstallationAsync(Target(database));
        await SeedTokenAsync(app, RootKey);

        var rawValues = await ReadStringsAsync(Target(database),
            $"SELECT values_json FROM public.{SettingsTable}");
        var raw = Assert.Single(rawValues);
        Assert.Contains("sm:v1:", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenPlaintext, raw, StringComparison.Ordinal);

        using var client = app.GetTestClient();
        var cookie = await LoginAsync(client, AdminId, AdminCredential);
        using var values = await SendAsync(client, HttpMethod.Get, ValuesPath, cookie);
        var body = await values.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.OK, values.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt64());
        var token = document.RootElement.GetProperty("values").EnumerateArray().Single(item =>
            item.GetProperty("key").GetString() == ReferenceSettingDefinitions.IntegrationTokenKey);
        Assert.True(token.GetProperty("hasValue").GetBoolean());
        Assert.Equal("persisted", token.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, token.GetProperty("value").ValueKind);
        Assert.True(token.GetProperty("isSensitive").GetBoolean());

        Assert.DoesNotContain(TokenPlaintext, body, StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, body, StringComparison.Ordinal);
        AssertNoSecret(recorder, body);
    }

    // --- A6: a value sealed with another root key is the fixed 503 ---------------------------

    [Fact]
    public async Task A_value_sealed_with_another_root_key_is_the_fixed_503()
    {
        var database = await CreateReadyTargetAsync("wrongkey");
        var recorder = new CapturingLoggerProvider();
        await using var app = await StartAppAsync(BaseArguments(database), recorder);
        await CompleteInstallationAsync(Target(database));
        await SeedTokenAsync(app, OtherRootKey);
        using var client = app.GetTestClient();
        var cookie = await LoginAsync(client, AdminId, AdminCredential);

        using var values = await SendAsync(client, HttpMethod.Get, ValuesPath, cookie);
        var body = await values.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, values.StatusCode);
        Assert.Equal("""{"errorCode":"management.settings.unavailable"}""", body);

        using var definitions = await SendAsync(client, HttpMethod.Get, DefinitionsPath, cookie);
        Assert.Equal(HttpStatusCode.OK, definitions.StatusCode);
        AssertNoSecret(recorder, body);
    }

    // --- The documented consequence of reusing the root key: a rotated deployment key closes login

    [Fact]
    public async Task A_restart_with_a_different_deployment_root_key_fails_login_closed()
    {
        var database = await CreateReadyTargetAsync("rotatedkey");
        await using (var first = await StartAppAsync(BaseArguments(database)))
        {
            await CompleteInstallationAsync(Target(database));
            using var firstClient = first.GetTestClient();
            _ = await LoginAsync(firstClient, AdminId, AdminCredential);
        }

        await using var app = await StartAppAsync(BaseArguments(database, rootKey: OtherRootKey));
        using var client = app.GetTestClient();
        using var login = await client.SendAsync(LoginRequest(AdminId, AdminCredential), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.Contains(
            "management.session.unavailable",
            await login.Content.ReadAsStringAsync(Token),
            StringComparison.Ordinal);
    }

    // --- A7: corrupt storage is the same fixed 503 -------------------------------------------

    [Fact]
    public async Task Corrupt_storage_is_the_fixed_503()
    {
        var database = await CreateReadyTargetAsync("corrupt");
        await using var app = await StartAppAsync(BaseArguments(database));
        await CompleteInstallationAsync(Target(database));
        await SeedTokenAsync(app, RootKey);
        await ExecuteAsync(Target(database),
            $"UPDATE public.{SettingsTable} SET values_json = '{{not json'");

        using var client = app.GetTestClient();
        var cookie = await LoginAsync(client, AdminId, AdminCredential);
        using var values = await SendAsync(client, HttpMethod.Get, ValuesPath, cookie);
        var body = await values.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, values.StatusCode);
        Assert.Equal("""{"errorCode":"management.settings.unavailable"}""", body);
    }

    // --- A8: caller cancellation during the store read is not a 503 or a 500 ------------------

    [Fact]
    public async Task Caller_cancellation_during_the_store_read_is_not_a_503_or_a_500()
    {
        var database = await CreateReadyTargetAsync("cancel");
        var probe = new StoreProbe { Park = true };
        var recorder = new CapturingLoggerProvider();
        await using var app = await StartAppAsync(
            BaseArguments(database),
            recorder,
            services => probe.Install(services));
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();
        var cookie = await LoginAsync(client, AdminId, AdminCredential);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = SendAsync(client, HttpMethod.Get, ValuesPath, cookie, cancellation.Token);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var observed = await probe.Outcome.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.Equal("canceled", observed);

        Assert.DoesNotContain(recorder.Messages, message =>
            message.Contains("unhandled", StringComparison.OrdinalIgnoreCase));
        probe.Park = false;
        using var after = await SendAsync(client, HttpMethod.Get, ValuesPath, cookie);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    private static async Task<WebApplication> StartAppAsync(
        List<string> arguments,
        ILoggerProvider? recorder = null,
        Action<IServiceCollection>? extraServices = null)
    {
        var builder = ReferenceApplication.CreateBuilder([.. arguments]);
        builder.WebHost.UseTestServer();
        return await BuildAndStartAsync(builder, recorder, extraServices);
    }

    private static async Task<WebApplication> BuildAndStartAsync(
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

    private List<string> BaseArguments(string database, string rootKey = RootKey) =>
    [
        "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
        "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, Target(database),
        "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
        "--environment", "Production",
        "--" + ReferenceManagementOptions.RootKeySetting, rootKey,
        "--ReferenceService:Management:Operators:0:Id", AdminId,
        "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
        "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
        "--ReferenceService:Management:Operators:0:Credential", AdminCredential,
        "--ReferenceService:Management:Operators:1:Id", ReaderId,
        "--ReferenceService:Management:Operators:1:DisplayName", "Ops Reader",
        "--ReferenceService:Management:Operators:1:Permissions", "management.read",
        "--ReferenceService:Management:Operators:1:Credential", ReaderCredential,
    ];

    /// <summary>
    /// Seeds the sensitive token through the public protector and store - the sample itself has no
    /// write path - so the projection observes a persisted, correctly sealed value.
    /// </summary>
    private static async Task SeedTokenAsync(WebApplication app, string rootKey)
    {
        var factory = app.Services.GetRequiredService<
            IDbContextFactory<ReferencePostgreSqlDbContext>>();
        var store = new EfCoreServiceSettingStore<ReferencePostgreSqlDbContext>(factory);
        var ciphertext = new SensitiveValueProtector(
            ServiceId.Parse(ServiceIdValue),
            ReferenceSettingDefinitions.IntegrationTokenKey)
            .Protect(TokenPlaintext, rootKey, Token);
        var result = await store.UpdateAsync(
            ServiceId.Parse(ServiceIdValue),
            new ServiceSettingStoreUpdate(
                0,
                new Dictionary<string, string?> { [ReferenceSettingDefinitions.IntegrationTokenKey] = ciphertext },
                "test-seed",
                restartRequired: false),
            Token);
        Assert.True(result.Succeeded);
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string secret)
    {
        using var login = await client.SendAsync(LoginRequest(username, secret), Token);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';', 2)[0];
    }

    private static HttpRequestMessage LoginRequest(string username, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(
                $$"""{"username":"{{username}}","secret":"{{secret}}"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        return request;
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

    private static void AssertNoSecret(CapturingLoggerProvider recorder, string body)
    {
        Assert.DoesNotContain(RootKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenPlaintext, body, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherRootKey, body, StringComparison.Ordinal);
        foreach (var message in recorder.Messages)
        {
            Assert.DoesNotContain(RootKey, message, StringComparison.Ordinal);
            Assert.DoesNotContain(OtherRootKey, message, StringComparison.Ordinal);
            Assert.DoesNotContain(TokenPlaintext, message, StringComparison.Ordinal);
            Assert.DoesNotContain(AdminCredential, message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Wraps the real store so the authorization matrix can prove zero loads and the cancellation
    /// test can observe the token the store received.
    /// </summary>
    private sealed class StoreProbe
    {
        private int loads;

        internal int Loads => Volatile.Read(ref loads);

        internal volatile bool Park;

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<string> Outcome { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Install(IServiceCollection services) =>
            services.AddSingleton<IServiceSettingStore>(provider => new Wrapper(
                new EfCoreServiceSettingStore<ReferencePostgreSqlDbContext>(
                    provider.GetRequiredService<IDbContextFactory<ReferencePostgreSqlDbContext>>()),
                this));

        private sealed class Wrapper(IServiceSettingStore inner, StoreProbe probe) : IServiceSettingStore
        {
            public async ValueTask<ServiceSettingStoreSnapshot> LoadAsync(
                ServiceId serviceId,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref probe.loads);
                if (probe.Park)
                {
                    probe.Entered.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        probe.Outcome.TrySetResult(
                            cancellationToken.IsCancellationRequested ? "canceled" : "other");
                        throw;
                    }
                }

                return await inner.LoadAsync(serviceId, cancellationToken);
            }

            public ValueTask<ServiceSettingStoreUpdateResult> UpdateAsync(
                ServiceId serviceId,
                ServiceSettingStoreUpdate update,
                CancellationToken cancellationToken = default) =>
                inner.UpdateAsync(serviceId, update, cancellationToken);
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

    private async Task<string> CreateReadyTargetAsync(string name)
    {
        RequireDatabase();
        var database = DatabaseName(name);
        await ExecuteAsync(maintenanceConnectionString!, $"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        await ExecuteAsync(maintenanceConnectionString!, $"""CREATE DATABASE "{database}" """);
        return database;
    }

    private Task CompleteInstallationAsync(string target) =>
        ExecuteAsync(target, $"""
            UPDATE public."{InstallationTable}"
            SET status = 1, completed_at_utc = now(), version = version + 1
            WHERE service_id = '{ServiceIdValue}'
            """);

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

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

    private static async Task<List<string>> ReadStringsAsync(string connectionString, string query)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static string DatabaseName(string name) => $"reference_settings_{name}";

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";
}
