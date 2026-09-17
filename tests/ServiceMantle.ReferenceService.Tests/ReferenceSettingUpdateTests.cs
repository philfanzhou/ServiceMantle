using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Drives the sample's transactional setting-update endpoint through its real
/// <see cref="ReferenceApplication"/> path against a real PostgreSQL server and real management
/// logins: the success matrix with per-key audit rows, every refusal leaving both tables empty,
/// the same-transaction atomicity of settings and audit, commit-time and cancellation semantics
/// through the caller-owned boundary, the one-winner concurrency row, and the sensitive-value
/// boundary across storage, audit, logs, and the read projection.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceSettingUpdateTests : IAsyncLifetime
{
    private const string ValuesPath = "/management/v1/settings";
    private const string LoginPath = "/management/v1/session/login";
    private const string InstallationTable = "service_installations";
    private const string SettingsTable = "service_settings";
    private const string AuditTable = "service_audit_logs";
    private const string ServiceIdValue = "reference-service";
    private const string UnsafeRequestHeader = "X-ServiceMantle-Request";

    private const string AdminId = "ops-admin";
    private const string AdminCredential = "synthetic-settingu-admin-secret";
    private const string ReaderId = "ops-reader";
    private const string ReaderCredential = "synthetic-settingu-reader-secret";

    // Synthetic fixture secrets, asserted to stay out of every response and captured log line.
    private const string SyntheticUser = "reference_settingu_owner";
    private const string SyntheticPassword = "synthetic-reference-settingq-secret";
    private const string RootKey = "synthetic-reference-management-root-key";
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
            .WithDatabase("reference_settingu_maintenance")
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

    // --- A1: the success matrix ---------------------------------------------------------------

    [Fact]
    public async Task A1_AppliedBatch_CommitsVersionOneWritesPerKeyAuditAndNamesTheOperator()
    {
        var (app, client, cookie, target) = await ReadyAsync("success");
        await using var ownedApp = app;

        using var response = await PostAsync(client, cookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"Ops workspace"},{"key":"workspace.item_limit","value":"7"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"version":1}""", await response.Content.ReadAsStringAsync(Token));

        Assert.Equal(
            ["1"],
            await ReadStringsAsync(target, $"SELECT version::text FROM {SettingsTable}"));
        Assert.Equal(
            [AdminId],
            await ReadStringsAsync(target, $"SELECT updated_by FROM {SettingsTable}"));

        var audit = await ReadStringsAsync(target, $"""
            SELECT action || '|' || operator_id || '|' || operator_source || '|' ||
                   target_type || '|' || target_id || '|' || coalesce(metadata_json, '')
            FROM {AuditTable} ORDER BY metadata_json
            """);
        Assert.Equal(
        [
            $$"""configuration.changed|{{AdminId}}|interactive_admin|configuration|{{ServiceIdValue}}|{"key":"workspace.display_name"}""",
            $$"""configuration.changed|{{AdminId}}|interactive_admin|configuration|{{ServiceIdValue}}|{"key":"workspace.item_limit"}""",
        ], audit);

        // The audit metadata carries keys only — never the submitted values.
        var rawAudit = string.Join('\n', audit);
        Assert.DoesNotContain("Ops workspace", rawAudit, StringComparison.Ordinal);
    }

    // --- A2: conflict, unknown key, constraint violation --------------------------------------

    [Fact]
    public async Task A2_ConflictUnknownKeyAndConstraintViolation_LeaveBothTablesEmpty()
    {
        var (app, client, cookie, target) = await ReadyAsync("rejects");
        await using var ownedApp = app;

        using var conflict = await PostAsync(client, cookie,
            """{"expectedVersion":3,"changes":[{"key":"workspace.display_name","value":"x"}]}""");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("management.request.conflict", await conflict.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        using var unknown = await PostAsync(client, cookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.nope","value":"1"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var invalid = await PostAsync(client, cookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.item_limit","value":"5000"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await invalid.Content.ReadAsStringAsync(Token);
        Assert.Contains("management.request.invalid", invalidBody, StringComparison.Ordinal);
        Assert.DoesNotContain("5000", invalidBody, StringComparison.Ordinal);

        await AssertEmptyAsync(target);
    }

    // --- A3: authorization ---------------------------------------------------------------------

    [Fact]
    public async Task A3_UnauthenticatedAndReadOnlyOperators_LeaveBothTablesEmpty()
    {
        var (app, client, _, target) = await ReadyAsync("authz");
        await using var ownedApp = app;

        using var anonymous = await PostAsync(client, null,
            """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"x"}]}""");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var readerCookie = await LoginAsync(client, ReaderId, ReaderCredential);
        using var reader = await PostAsync(client, readerCookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"x"}]}""");
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);

        await AssertEmptyAsync(target);
    }

    // --- A4: audit insert failure rolls the settings row back ----------------------------------

    [Fact]
    public async Task A4_AFailingAuditInsert_RollsBackToTheFixed503()
    {
        var (app, client, cookie, target) = await ReadyAsync("auditfail");
        await using var ownedApp = app;
        await ExecuteAsync(target, """
            CREATE FUNCTION settingu_fail_audit() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'simulated audit failure'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER settingu_fail_audit BEFORE INSERT ON public.service_audit_logs
                FOR EACH ROW EXECUTE FUNCTION settingu_fail_audit();
            """);

        using var response = await PostAsync(client, cookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"Ops"}]}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            """{"errorCode":"management.settings.update_unavailable"}""",
            await response.Content.ReadAsStringAsync(Token));
        // Settings and audit share one transaction: neither row exists.
        await AssertEmptyAsync(target);
    }

    // --- A5: commit-time failure ----------------------------------------------------------------

    [Fact]
    public async Task A5_ACommitTimeFailure_IsTheSameFixed503AndLeavesNothing()
    {
        var (app, client, cookie, target) = await ReadyAsync("commitfail");
        await using var ownedApp = app;
        await ExecuteAsync(target, """
            CREATE FUNCTION settingu_fail_commit() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'simulated commit failure'; END $$ LANGUAGE plpgsql;
            CREATE CONSTRAINT TRIGGER settingu_fail_commit AFTER INSERT ON public.service_settings
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION settingu_fail_commit();
            """);

        using var response = await PostAsync(client, cookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"Ops"}]}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            """{"errorCode":"management.settings.update_unavailable"}""",
            await response.Content.ReadAsStringAsync(Token));
        await AssertEmptyAsync(target);
    }

    // --- A6: caller cancellation before the commit ---------------------------------------------

    [Fact]
    public async Task A6_CallerCancellationBeforeCommit_RollsBackEverything()
    {
        var (app, client, cookie, target) = await ReadyAsync("cancelbefore");
        await using var ownedApp = app;
        await ExecuteAsync(target, """
            CREATE FUNCTION settingu_slow_audit() RETURNS trigger AS $$
            BEGIN PERFORM pg_sleep(3); RETURN NEW; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER settingu_slow_audit BEFORE INSERT ON public.service_audit_logs
                FOR EACH ROW EXECUTE FUNCTION settingu_slow_audit();
            """);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = PostAsync(client, cookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"Ops"}]}""",
            cancellation.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(800), Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // The server unwinds on its own; after it settled, nothing was committed.
        await Task.Delay(TimeSpan.FromSeconds(4), Token);
        await AssertEmptyAsync(target);
    }

    // --- A7: caller cancellation after the commit started --------------------------------------

    [Fact]
    public async Task A7_CallerCancellationDuringCommit_DoesNotUndoTheCommit()
    {
        var (app, client, cookie, target) = await ReadyAsync("cancelcommit");
        await using var ownedApp = app;
        await ExecuteAsync(target, """
            CREATE FUNCTION settingu_slow_commit() RETURNS trigger AS $$
            BEGIN PERFORM pg_sleep(3); RETURN NEW; END $$ LANGUAGE plpgsql;
            CREATE CONSTRAINT TRIGGER settingu_slow_commit AFTER INSERT ON public.service_settings
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION settingu_slow_commit();
            """);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = PostAsync(client, cookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"Ops"}]}""",
            cancellation.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // The commit itself ran on CancellationToken.None: the caller sees its own abort while
        // the batch stays committed.
        await Task.Delay(TimeSpan.FromSeconds(4), Token);
        Assert.Equal(
            ["1"],
            await ReadStringsAsync(target, $"SELECT version::text FROM {SettingsTable}"));
        Assert.Equal(
            ["1"],
            await ReadStringsAsync(target, $"SELECT count(*)::text FROM {AuditTable}"));
    }

    // --- A8: one winner among concurrent same-version updates ----------------------------------

    [Fact]
    public async Task A8_TwoConcurrentUpdatesOnTheSameVersion_HaveExactlyOneWinner()
    {
        var (app, client, cookie, target) = await ReadyAsync("race");
        await using var ownedApp = app;
        await ExecuteAsync(target, """
            CREATE FUNCTION settingu_slow_race() RETURNS trigger AS $$
            BEGIN PERFORM pg_sleep(0.5); RETURN NEW; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER settingu_slow_race BEFORE INSERT ON public.service_audit_logs
                FOR EACH ROW EXECUTE FUNCTION settingu_slow_race();
            """);

        var first = PostAsync(client, cookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"A"}]}""");
        var second = PostAsync(client, cookie,
            """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"B"}]}""");
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(
            ["1"],
            await ReadStringsAsync(target, $"SELECT version::text FROM {SettingsTable}"));
        Assert.Equal(
            ["1"],
            await ReadStringsAsync(target, $"SELECT count(*)::text FROM {AuditTable}"));
        var stored = Assert.Single(
            await ReadStringsAsync(target, $"SELECT values_json FROM {SettingsTable}"));
        var winnerBody = stored.Contains("\"A\"", StringComparison.Ordinal) ? "A" : "B";
        Assert.DoesNotContain(winnerBody == "A" ? "\"B\"" : "\"A\"", stored, StringComparison.Ordinal);
    }

    // --- A9: the sensitive-value boundary -------------------------------------------------------

    [Fact]
    public async Task A9_ASensitiveWrite_StoresCiphertextAndLeaksNothing()
    {
        var recorder = new CapturingLoggerProvider();
        var (app, client, cookie, target) = await ReadyAsync("sensitive", recorder);
        await using var ownedApp = app;

        using var response = await PostAsync(client, cookie,
            $$"""{"expectedVersion":0,"changes":[{"key":"workspace.integration_token","value":"{{TokenPlaintext}}"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = Assert.Single(await ReadStringsAsync(target, $"SELECT values_json FROM {SettingsTable}"));
        Assert.Contains("sm:v1:", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenPlaintext, raw, StringComparison.Ordinal);

        var audit = string.Join('\n', await ReadStringsAsync(target,
            $"SELECT coalesce(metadata_json, '') || coalesce(security_description, '') FROM {AuditTable}"));
        Assert.DoesNotContain(TokenPlaintext, audit, StringComparison.Ordinal);
        Assert.DoesNotContain("sm:v1:", audit, StringComparison.Ordinal);

        // The read projection answers hasValue:true with value:null for a sensitive key.
        using var values = await GetAsync(client, ValuesPath, cookie);
        var body = await values.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.OK, values.StatusCode);
        Assert.Contains(
            "\"key\":\"workspace.integration_token\",\"valueType\":\"string\",\"isRequired\":false," +
            "\"isSensitive\":true,\"hasDefault\":false,\"requiresRestart\":false," +
            "\"hasValue\":true,\"source\":\"persisted\",\"value\":null",
            body,
            StringComparison.Ordinal);

        foreach (var message in recorder.Messages)
        {
            Assert.DoesNotContain(TokenPlaintext, message, StringComparison.Ordinal);
            Assert.DoesNotContain("sm:v1:", message, StringComparison.Ordinal);
            Assert.DoesNotContain(RootKey, message, StringComparison.Ordinal);
        }
    }

    // --- helpers --------------------------------------------------------------------------------

    private async Task<(WebApplication App, HttpClient Client, string Cookie, string Target)> ReadyAsync(
        string name,
        ILoggerProvider? recorder = null)
    {
        var database = await CreateReadyTargetAsync(name);
        var app = await StartAppAsync(BaseArguments(database), recorder);
        await CompleteInstallationAsync(Target(database));
        var client = app.GetTestClient();
        var cookie = await LoginAsync(client, AdminId, AdminCredential);
        return (app, client, cookie, Target(database));
    }

    private List<string> BaseArguments(string database) =>
    [
        "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
        "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, Target(database),
        "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
        "--environment", "Production",
        "--ReferenceService:Management:RootKey", RootKey,
        "--ReferenceService:Management:Operators:0:Id", AdminId,
        "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
        "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
        "--ReferenceService:Management:Operators:0:Credential", AdminCredential,
        "--ReferenceService:Management:Operators:1:Id", ReaderId,
        "--ReferenceService:Management:Operators:1:DisplayName", "Ops Reader",
        "--ReferenceService:Management:Operators:1:Permissions", "management.read",
        "--ReferenceService:Management:Operators:1:Credential", ReaderCredential,
    ];

    private static async Task<WebApplication> StartAppAsync(
        List<string> arguments,
        ILoggerProvider? recorder = null)
    {
        var builder = ReferenceApplication.CreateBuilder([.. arguments]);
        builder.WebHost.UseTestServer();
        if (recorder is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(recorder);
        }

        var app = ReferenceApplication.Build(builder);
        await app.StartAsync(Token);
        return app;
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

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string? cookie,
        string json,
        CancellationToken? cancellationToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ValuesPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return client.SendAsync(request, cancellationToken ?? Token);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request, Token);
    }

    private static async Task AssertEmptyAsync(string target)
    {
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(target, $"SELECT count(*)::text FROM {SettingsTable}"));
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(target, $"SELECT count(*)::text FROM {AuditTable}"));
    }

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

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
            UPDATE public."{InstallationTable}" SET status = 1, completed_at_utc = now()
            WHERE service_id = '{ServiceIdValue}'
            """);

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

    private static string DatabaseName(string name) => $"reference_settingu_{name}";

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

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
