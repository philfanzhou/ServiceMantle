using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.Management;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Management;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the reference service's management session wiring against a real PostgreSQL server and
/// the real <see cref="ReferenceApplication"/> composition: the login matrix over the
/// deployment-provided operator directory, the shared cookie key ring persisted through the gate's
/// context factory, the fail-closed root key, the cookie baseline, logout, the caller-cancellation
/// boundary, and the absence of any secret in a response or a captured log line.
/// </summary>
/// <remarks>
/// The external identity is a deployment fact: the operator directory is supplied through
/// configuration, the sample creates no local administrator, and with no directory configured the
/// provider keeps answering the fixed not-configured failure so login is impossible. The shared
/// real-database policy applies: <c>RUN_SERVICEMANTLE_POSTGRES_TESTS=true</c> and a running Docker
/// daemon; when the environment is explicitly required and unavailable the tests fail rather than
/// skip.
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceManagementSessionTests : IAsyncLifetime
{
    private const string InstallationTable = "service_installations";
    private const string DataProtectionTable = "service_data_protection_keys";
    private const string ServiceIdValue = "reference-service";
    private const string LoginPath = "/management/v1/session/login";
    private const string SessionPath = "/management/v1/session";
    private const string LogoutPath = "/management/v1/session/logout";
    private const string UnsafeRequestHeader = "X-ServiceMantle-Request";

    // Synthetic fixture secrets. They exist only inside this container and are asserted to stay out
    // of every management response body, cookie value, and captured log line.
    private const string SyntheticUser = "reference_session_owner";
    private const string SyntheticPassword = "synthetic-reference-session-secret";
    private const string OperatorId = "ops-admin";
    private const string OperatorCredential = "synthetic-operator-credential-secret";
    private const string RootKey = "synthetic-reference-management-root-key";

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
            .WithDatabase("reference_session_maintenance")
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

    // --- D1: directory login, no local administrator --------------------------------------

    [Fact]
    public async Task A_directory_operator_logs_in_without_any_local_administrator()
    {
        var database = await CreateReadyTargetAsync("directory_login");
        var recorder = new RecordingLoggerProvider();
        await using var app = await StartAppAsync(database, recorder: recorder);
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();

        using var login = await LoginAsync(client, OperatorId, OperatorCredential);
        var cookie = AssertCookieIssued(login);

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var session = await client.SendAsync(SessionRequest(client, SessionPath, cookie), Token);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var body = JsonDocument.Parse(await session.Content.ReadAsStringAsync(Token)).RootElement;
        Assert.True(body.GetProperty("authenticated").GetBoolean());
        Assert.Equal(["management.read", "management.admin"],
            body.GetProperty("permissions").EnumerateArray().Select(value => value.GetString()));

        // The external directory provider is the one identity registration; there is no account
        // store, no seeding, and no other provider to fall back to.
        await using var scope = app.Services.CreateAsyncScope();
        var providers = scope.ServiceProvider.GetServices<IManagementIdentityProvider>().ToArray();
        Assert.IsType<ReferenceExternalManagementIdentityProvider>(Assert.Single(providers));
        AssertNoSecret(recorder, await session.Content.ReadAsStringAsync(Token));
    }

    // --- D2: login failure matrix -----------------------------------------------------------

    [Fact]
    public async Task A_wrong_credential_and_an_unknown_operator_share_the_fixed_401()
    {
        var database = await CreateReadyTargetAsync("wrong_credential");
        await using var app = await StartAppAsync(database);
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();

        foreach (var username in new[] { OperatorId, "no-such-operator" })
        {
            using var response = await LoginAsync(client, username, "definitely-the-wrong-secret");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(
                "{\"errorCode\":\"management.session.unauthenticated\"}",
                await response.Content.ReadAsStringAsync(Token));
            AssertNoCookie(response);
        }
    }

    [Fact]
    public async Task An_unconfigured_directory_leaves_login_impossible()
    {
        var database = await CreateReadyTargetAsync("unconfigured_directory");
        await using var app = await StartAppAsync(database, operators: false);
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();
        await using var scope = app.Services.CreateAsyncScope();
        // The provider's own classification stays the fixed not-configured failure.
        scope.ServiceProvider.GetRequiredService<ReferenceOperatorCredentialAccessor>()
            .Set(OperatorId, OperatorCredential);
        var provider = Assert.IsType<ReferenceExternalManagementIdentityProvider>(
            Assert.Single(scope.ServiceProvider.GetServices<IManagementIdentityProvider>()));
        var identity = await ManagementIdentityProviderInvoker.InvokeAsync(provider, Token);
        Assert.Equal(ManagementIdentityStatus.Failed, identity.Status);
        Assert.Equal("reference.external_identity_not_configured", identity.ErrorCode);

        using var response = await LoginAsync(client, OperatorId, OperatorCredential);

        // A provider failure is the fixed unavailable result: the directory is a deployment fact,
        // not something the caller can probe.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"errorCode\":\"management.session.unavailable\"}",
            await response.Content.ReadAsStringAsync(Token));
        AssertNoCookie(response);
    }

    [Fact]
    public async Task A_malformed_envelope_is_not_a_login_attempt()
    {
        var database = await CreateReadyTargetAsync("malformed_envelope");
        var probe = new ProviderProbe();
        await using var app = await StartAppAsync(database, extraServices: services =>
            services.AddScoped<IManagementIdentityProvider>(sp => new ProbingIdentityProvider(
                sp.GetRequiredService<ReferenceManagementOptions>(),
                sp.GetRequiredService<ReferenceOperatorCredentialAccessor>(),
                probe)));
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();

        using var notJson = await LoginBodyAsync(client, "not json at all");
        using var wrongShape = await LoginBodyAsync(client, """{"username":"only-user"}""");
        using var unknownMember = await LoginBodyAsync(
            client,
            $$"""{"username":"{{OperatorId}}","secret":"{{OperatorCredential}}","extra":1}""");

        Assert.Equal(HttpStatusCode.Unauthorized, notJson.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongShape.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownMember.StatusCode);
        Assert.All(new[] { notJson, wrongShape, unknownMember }, AssertNoCookie);
        // None of the malformed requests reached the provider: nothing was compared against the
        // directory and no credential material was touched.
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task A_missing_unsafe_request_header_is_the_fixed_400_without_running_the_adapter()
    {
        var database = await CreateReadyTargetAsync("missing_header");
        var probe = new ProviderProbe();
        await using var app = await StartAppAsync(database, extraServices: services =>
            services.AddScoped<IManagementIdentityProvider>(sp => new ProbingIdentityProvider(
                sp.GetRequiredService<ReferenceManagementOptions>(),
                sp.GetRequiredService<ReferenceOperatorCredentialAccessor>(),
                probe)));
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = JsonContent(Envelope(OperatorId, OperatorCredential)),
        };
        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "management.request.invalid",
            await response.Content.ReadAsStringAsync(Token),
            StringComparison.Ordinal);
        AssertNoCookie(response);
        Assert.Equal(0, probe.Calls);
    }

    // --- D10: phase admission before the adapter --------------------------------------------

    [Fact]
    public async Task A_phase_that_is_not_complete_never_runs_the_adapter()
    {
        var database = await CreateReadyTargetAsync("pending_phase");
        var probe = new ProviderProbe();
        await using var app = await StartAppAsync(database, extraServices: services =>
            services.AddScoped<IManagementIdentityProvider>(sp => new ProbingIdentityProvider(
                sp.GetRequiredService<ReferenceManagementOptions>(),
                sp.GetRequiredService<ReferenceOperatorCredentialAccessor>(),
                probe)));
        using var client = app.GetTestClient();

        // The installation row is still pending, so the shared phase gate answers first.
        using var response = await LoginAsync(client, OperatorId, OperatorCredential);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"errorCode\":\"service.phase.unavailable\"}",
            await response.Content.ReadAsStringAsync(Token));
        AssertNoCookie(response);
        Assert.Equal(0, probe.Calls);
    }

    // --- D5: the shared key ring in the database --------------------------------------------

    [Fact]
    public async Task A_login_persists_an_encrypted_key_ring_row_for_the_service()
    {
        var database = await CreateReadyTargetAsync("key_ring");
        await using var app = await StartAppAsync(database);
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();

        using var login = await LoginAsync(client, OperatorId, OperatorCredential);
        AssertCookieIssued(login);

        var rows = await ReadStringsAsync(
            Target(database),
            $"""SELECT key_id || ':' || encrypted_xml FROM public."{DataProtectionTable}" WHERE service_id = '{ServiceIdValue}' """);
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.DoesNotContain("<?xml", row, StringComparison.Ordinal);
            Assert.DoesNotContain("<KeyInfo", row, StringComparison.Ordinal);
            Assert.DoesNotContain("<key", row, StringComparison.OrdinalIgnoreCase);
        });
    }

    // --- D7: the root key is fail-closed -----------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void A_missing_or_short_root_key_fails_the_composition(string? rootKey)
    {
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, "Host=127.0.0.1;Port=9;Database=ref;Username=u;Password=p",
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
        };
        if (rootKey is not null)
        {
            arguments.AddRange(["--" + ReferenceManagementOptions.RootKeySetting, rootKey]);
        }

        var failure = Assert.Throws<InvalidOperationException>(() => ReferenceApplication.CreateBuilder([.. arguments]));

        Assert.Contains(ReferenceManagementOptions.RootKeySetting, failure.Message, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(rootKey))
        {
            Assert.DoesNotContain(rootKey, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_malformed_operator_directory_fails_the_composition()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => ReferenceApplication.CreateBuilder(
        [
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, "Host=127.0.0.1;Port=9;Database=ref;Username=u;Password=p",
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
            "--" + ReferenceManagementOptions.RootKeySetting, RootKey,
            "--ReferenceService:Management:Operators:0:Id", "ops-admin",
            "--ReferenceService:Management:Operators:0:Permissions", "management.read",
            // A blank credential is an unusable deployment fact.
            "--ReferenceService:Management:Operators:0:Credential", "  ",
        ]));

        Assert.Contains(ReferenceManagementOptions.OperatorsSection, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OperatorId, failure.Message, StringComparison.Ordinal);
    }

    // --- D10: the cookie baseline ------------------------------------------------------------

    [Fact]
    public async Task The_management_cookie_keeps_the_secure_baseline()
    {
        var database = await CreateReadyTargetAsync("cookie_baseline");
        await using var app = await StartAppAsync(database);

        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ManagementSessionDefaults.AuthenticationScheme);

        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);
    }

    // --- D11: local logout --------------------------------------------------------------------

    [Fact]
    public async Task Logout_deletes_this_clients_cookie_and_the_next_read_is_unauthenticated()
    {
        var database = await CreateReadyTargetAsync("logout");
        await using var app = await StartAppAsync(database);
        await CompleteInstallationAsync(Target(database));
        using var client = app.GetTestClient();
        using var login = await LoginAsync(client, OperatorId, OperatorCredential);
        var cookie = AssertCookieIssued(login);

        using var logout = await client.SendAsync(SessionRequest(
            client,
            LogoutPath,
            cookie,
            method: HttpMethod.Post,
            unsafeHeader: true), Token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        // The deletion cookie was written before the 204 was answered.
        var deletion = Assert.Single(logout.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(ManagementSessionDefaults.CookieName + "=", deletion, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", deletion, StringComparison.OrdinalIgnoreCase);

        // This client honors the deletion, so its next read carries no cookie at all. A ticket
        // copied elsewhere stays valid until expiry: the shared contract's declared non-guarantee.
        using var after = await client.GetAsync(SessionPath, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // --- D9: caller cancellation during the adapter -------------------------------------------

    [Fact]
    public async Task A_caller_cancellation_during_the_adapter_wins_and_no_cookie_is_issued()
    {
        var database = await CreateReadyTargetAsync("cancelled_login");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A cooperative provider: it parks inside the call until either the test releases it or the
        // login token it received is cancelled.
        var probe = new ProviderProbe { Gate = token => gate.Task.WaitAsync(token) };
        await using var app = await StartAppAsync(database, extraServices: services =>
            services.AddScoped<IManagementIdentityProvider>(sp => new ProbingIdentityProvider(
                sp.GetRequiredService<ReferenceManagementOptions>(),
                sp.GetRequiredService<ReferenceOperatorCredentialAccessor>(),
                probe)));
        await CompleteInstallationAsync(Target(database));
        using var abort = new CancellationTokenSource();
        try
        {
            var request = app.GetTestServer().SendAsync(context =>
            {
                context.Request.Method = HttpMethods.Post;
                context.Request.Path = LoginPath;
                context.Request.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(Envelope(OperatorId, OperatorCredential));
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                context.Request.Headers[UnsafeRequestHeader] = "1";
                context.RequestAborted = abort.Token;
            }, Token);

            await probe.WaitForEntryAsync();
            abort.Cancel();

            // Reverse validation of the completion checkpoint: without it the adapter's late
            // authenticated result would be signed in and answered 204 instead of the caller's
            // own cancellation.
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(abort.Token, failure.CancellationToken);
            Assert.Equal(1, probe.Calls);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    private async Task<string> CreateReadyTargetAsync(string name)
    {
        RequireDatabase();
        var database = DatabaseName(name);
        await CreateDatabaseAsync(database);
        return database;
    }

    private async Task<WebApplication> StartAppAsync(
        string database,
        bool operators = true,
        RecordingLoggerProvider? recorder = null,
        Action<IServiceCollection>? extraServices = null)
    {
        RequireDatabase();
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, Target(database),
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
            "--environment", "Production",
            "--" + ReferenceManagementOptions.RootKeySetting, RootKey,
        };
        if (operators)
        {
            arguments.AddRange([
                "--ReferenceService:Management:Operators:0:Id", OperatorId,
                "--ReferenceService:Management:Operators:0:DisplayName", "Reference Operator",
                "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
                "--ReferenceService:Management:Operators:0:Credential", OperatorCredential,
            ]);
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
        await app.StartAsync(Token);
        return app;
    }

    private static HttpRequestMessage LoginRequest(string username, string secret, bool unsafeHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = JsonContent(Envelope(username, secret)),
        };
        if (unsafeHeader)
        {
            request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        }

        return request;
    }

    private static Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string username, string secret, bool unsafeHeader = true) =>
        client.SendAsync(LoginRequest(username, secret, unsafeHeader), Token);

    private static Task<HttpResponseMessage> LoginBodyAsync(HttpClient client, string body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = JsonContent(body),
            Headers = { { UnsafeRequestHeader, "1" } },
        }, Token);

    private static StringContent JsonContent(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static string Envelope(string username, string secret) =>
        $$"""{"username":"{{username}}","secret":"{{secret}}"}""";

    private static HttpRequestMessage SessionRequest(
        HttpClient client,
        string path,
        string cookie,
        HttpMethod? method = null,
        bool unsafeHeader = false)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (unsafeHeader)
        {
            request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        }

        return request;
    }

    private static string AssertCookieIssued(HttpResponseMessage response)
    {
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(ManagementSessionDefaults.CookieName + "=", setCookie, StringComparison.Ordinal);
        return setCookie.Split(';', 2)[0];
    }

    private static void AssertNoCookie(HttpResponseMessage response) =>
        Assert.False(response.Headers.Contains("Set-Cookie"));

    private static void AssertNoSecret(RecordingLoggerProvider recorder, string body)
    {
        Assert.DoesNotContain(OperatorCredential, body, StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, body, StringComparison.Ordinal);
        foreach (var line in recorder.Lines)
        {
            Assert.DoesNotContain(OperatorCredential, line, StringComparison.Ordinal);
            Assert.DoesNotContain(RootKey, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, line, StringComparison.Ordinal);
        }
    }

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private async Task CreateDatabaseAsync(string database)
    {
        RequireDatabase();
        await ExecuteOnMaintenanceAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        await ExecuteOnMaintenanceAsync($"""CREATE DATABASE "{database}" """);
    }

    private Task ExecuteOnMaintenanceAsync(string statement) =>
        ExecuteAsync(maintenanceConnectionString!, statement);

    private Task CompleteInstallationAsync(string target) =>
        ExecuteAsync(target, $"""
            UPDATE public."{InstallationTable}" SET status = 1, completed_at_utc = now() WHERE service_id = '{ServiceIdValue}'
            """);

    private static async Task ExecuteAsync(string connectionString, string statement)
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

    private string Target(string database) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
            IncludeErrorDetail = false,
        }.ConnectionString;

    private static string DatabaseName(string name) => $"reference_session_{name}";

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    /// <summary>Records every formatted line the host writes, for secret-output assertions.</summary>
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
                    lines.Add(formatter(state, exception));
                }
            }
        }
    }

    /// <summary>Observes the provider without changing what it answers.</summary>
    private sealed class ProviderProbe
    {        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Func<CancellationToken, Task>? Gate { get; set; }

        internal int Calls => Volatile.Read(ref calls);

        private int calls;

        internal Task WaitForEntryAsync() => entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        internal async ValueTask<ManagementIdentityResult> ObserveAsync(
            ReferenceExternalManagementIdentityProvider inner,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            if (Gate is { } gate)
            {
                await gate(cancellationToken);
            }

            return await inner.GetIdentityAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The last-registered provider: it wraps the directory provider behind a probe so a test can
    /// count entries and park inside the provider call without changing any answer.
    /// </summary>
    private sealed class ProbingIdentityProvider(
        ReferenceManagementOptions options,
        ReferenceOperatorCredentialAccessor accessor,
        ProviderProbe probe) : IManagementIdentityProvider
    {
        private readonly ReferenceExternalManagementIdentityProvider inner = new(options, accessor);

        public ValueTask<ManagementIdentityResult> GetIdentityAsync(CancellationToken cancellationToken = default) =>
            probe.ObserveAsync(inner, cancellationToken);
    }
}

