using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Drives the sample's setting updates and audits through two real operating-system processes
/// over one PostgreSQL target: one instance's committed update is the other's observed snapshot,
/// a cross-instance race on one expected version has exactly one winner, a failed update leaves
/// no partial state on either instance, and a sensitive value written by one instance projects
/// as null on the other.
/// </summary>
/// <remarks>
/// The hosts are real operating-system processes started from the sample's own build output, so
/// the evidence is real HTTP with independently authenticated sessions, real transactions, and
/// the database facts both processes share. The single-instance matrices stay where they are;
/// this file owns only the cross-instance observations. The shared real-database policy applies:
/// <c>RUN_SERVICEMANTLE_POSTGRES_TESTS=true</c> and a running Docker daemon.
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceSettingCrossInstanceTests : IAsyncLifetime
{
    private const string ValuesPath = "/management/v1/settings";
    private const string LoginPath = "/management/v1/session/login";
    private const string UnsafeRequestHeader = "X-ServiceMantle-Request";
    private const string InstallationTable = "service_installations";
    private const string SettingsTable = "service_settings";
    private const string AuditTable = "service_audit_logs";
    private const string TokenPlaintext = "synthetic-cross-instance-token-secret";

    // Synthetic fixture secrets, asserted to stay out of every response and captured output.
    private const string SyntheticUser = "reference_settingx_owner";
    private const string SyntheticPassword = "synthetic-reference-settingx-secret";
    private const string RootKey = "synthetic-reference-settingx-root-key";
    private const string OperatorId = "ops-admin";
    private const string OperatorCredential = "synthetic-settingx-operator-secret";

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
            .WithDatabase("reference_settingx_maintenance")
            .WithUsername(SyntheticUser)
            .WithPassword(SyntheticPassword)
            .Build();
        await container.StartAsync(Token);
        maintenanceConnectionString = container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(Token);
            await container.DisposeAsync();
        }
    }

    // --- 1: one instance's committed update is the other's observed snapshot --------------------

    [Fact]
    public async Task An_update_on_one_instance_is_observed_identically_on_the_other()
    {
        var two = await StartTwoAsync("observe");
        using var workingScope = two.Working;
        await using var firstScope = two.First;
        await using var secondScope = two.Second;

        var target = two.Target;
        var updated = await PostSettingsAsync(two.FirstAddress, two.FirstCookie, """
            {"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"cross-instance-name"}]}
            """);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        // The other instance observes the same version and the same complete snapshot - compared
        // byte for byte against the writer's own read, sensitive projection included.
        var writerView = await GetSettingsAsync(two.FirstAddress, two.FirstCookie);
        var readerView = await GetSettingsAsync(two.SecondAddress, two.SecondCookie);
        Assert.Equal(1, writerView.Version);
        Assert.Equal(writerView.Body, readerView.Body);
        Assert.Contains("cross-instance-name", readerView.Body, StringComparison.Ordinal);

        Assert.Equal(["1"], await ReadAsync(target, $"SELECT version::text FROM {SettingsTable}"));
        Assert.Equal(["1"], await ReadAsync(target, $"SELECT count(*)::text FROM {AuditTable}"));
        AssertNoSecrets(writerView.Body, readerView.Body);
    }

    // --- 2: a cross-instance race on one expected version ----------------------------------------

    [Fact]
    public async Task Cross_instance_concurrent_updates_on_the_same_version_have_exactly_one_winner()
    {
        var two = await StartTwoAsync("race");
        using var workingScope = two.Working;
        await using var firstScope = two.First;
        await using var secondScope = two.Second;

        var target = two.Target;
        // The audit insert sleeps, so both processes' transactions genuinely overlap inside the
        // shared budget; the version concurrency token arbitrates across the process boundary.
        await ExecuteAsync(target, """
            CREATE FUNCTION settingx_slow_race() RETURNS trigger AS $$
            BEGIN PERFORM pg_sleep(0.5); RETURN NEW; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER settingx_slow_race BEFORE INSERT ON public.service_audit_logs
                FOR EACH ROW EXECUTE FUNCTION settingx_slow_race();
            """);

        var raced = await Task.WhenAll(
            PostSettingsAsync(two.FirstAddress, two.FirstCookie,
                """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"winner-A"}]}"""),
            PostSettingsAsync(two.SecondAddress, two.SecondCookie,
                """{"expectedVersion":0,"changes":[{"key":"workspace.display_name","value":"winner-B"}]}"""));

        Assert.Single(raced, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(raced, result => result.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(["1"], await ReadAsync(target, $"SELECT version::text FROM {SettingsTable}"));
        Assert.Equal(["1"], await ReadAsync(target, $"SELECT count(*)::text FROM {AuditTable}"));
        var stored = Assert.Single(await ReadAsync(target, $"SELECT values_json FROM {SettingsTable}"));
        Assert.Contains("winner-", stored, StringComparison.Ordinal);
        Assert.True(
            stored.Contains("winner-A", StringComparison.Ordinal) ^
            stored.Contains("winner-B", StringComparison.Ordinal),
            "exactly one writer's value is stored");

        // The 409 side re-reads the same version and snapshot the winner left behind.
        var winnerIsFirst = raced[0].StatusCode == HttpStatusCode.OK;
        var winnerRead = await GetSettingsAsync(
            winnerIsFirst ? two.FirstAddress : two.SecondAddress,
            winnerIsFirst ? two.FirstCookie : two.SecondCookie);
        var loserRead = await GetSettingsAsync(
            winnerIsFirst ? two.SecondAddress : two.FirstAddress,
            winnerIsFirst ? two.SecondCookie : two.FirstCookie);
        Assert.Equal(winnerRead.Body, loserRead.Body);
        Assert.Equal(1, loserRead.Version);
        AssertNoSecrets([.. raced.Select(result => result.Body), winnerRead.Body, loserRead.Body]);
    }

    // --- 3: a failed update leaves no partial state on either instance --------------------------

    [Fact]
    public async Task An_audit_failure_on_one_instance_leaves_no_partial_state_on_the_other()
    {
        var two = await StartTwoAsync("failure");
        using var workingScope = two.Working;
        await using var firstScope = two.First;
        await using var secondScope = two.Second;
        var target = two.Target;

        await ExecuteAsync(target, """
            CREATE FUNCTION settingx_fail_audit() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'synthetic audit failure'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER settingx_audit_failure BEFORE INSERT ON public.service_audit_logs
                FOR EACH ROW EXECUTE FUNCTION settingx_fail_audit();
            """);

        var failed = await PostSettingsAsync(two.FirstAddress, two.FirstCookie, """
            {"expectedVersion":0,"changes":[{"key":"workspace.item_limit","value":"50"}]}
            """);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);

        // The other instance observes no partial state: the same version-zero snapshot, and the
        // database holds no audit row and no stored value.
        var readerView = await GetSettingsAsync(two.SecondAddress, two.SecondCookie);
        Assert.Equal(0, readerView.Version);
        Assert.Contains(
            "\"key\":\"workspace.item_limit\",\"valueType\":\"number\"",
            readerView.Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"50\"", readerView.Body, StringComparison.Ordinal);
        // The version-zero state has no stored row at all; unchanged means still none.
        Assert.Empty(await ReadAsync(target, $"SELECT version::text FROM {SettingsTable}"));
        Assert.Equal(["0"], await ReadAsync(target, $"SELECT count(*)::text FROM {AuditTable}"));

        // With the trigger gone, the retry succeeds - from the other instance.
        await ExecuteAsync(target, "DROP TRIGGER settingx_audit_failure ON public.service_audit_logs");
        var retried = await PostSettingsAsync(two.SecondAddress, two.SecondCookie, """
            {"expectedVersion":0,"changes":[{"key":"workspace.item_limit","value":"50"}]}
            """);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(["1"], await ReadAsync(target, $"SELECT version::text FROM {SettingsTable}"));
        AssertNoSecrets(failed.Body, retried.Body, readerView.Body);
    }

    // --- 4: a sensitive value crosses instances as a null projection ----------------------------

    [Fact]
    public async Task A_sensitive_write_on_one_instance_projects_null_on_the_other()
    {
        var two = await StartTwoAsync("sensitive");
        using var workingScope = two.Working;
        await using var firstScope = two.First;
        await using var secondScope = two.Second;
        var target = two.Target;

        var written = await PostSettingsAsync(two.FirstAddress, two.FirstCookie, $$"""
            {"expectedVersion":0,"changes":[{"key":"workspace.integration_token","value":"{{TokenPlaintext}}"}]}
            """);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        // The database holds ciphertext only.
        var raw = Assert.Single(await ReadAsync(target, $"SELECT values_json FROM {SettingsTable}"));
        Assert.Contains("sm:v1:", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenPlaintext, raw, StringComparison.Ordinal);

        // The other instance projects the sensitive value as null with the sensitive flags set.
        var readerView = await GetSettingsAsync(two.SecondAddress, two.SecondCookie);
        Assert.Contains(
            "\"key\":\"workspace.integration_token\",\"valueType\":\"string\",\"isRequired\":false," +
                "\"isSensitive\":true,\"hasDefault\":false,\"requiresRestart\":false," +
                "\"hasValue\":true,\"source\":\"persisted\",\"value\":null",
            readerView.Body,
            StringComparison.Ordinal);

        // Neither the plaintext, nor the ciphertext prefix, nor the root key reaches a response
        // of either instance, a captured console, or an audit row.
        var writerView = await GetSettingsAsync(two.FirstAddress, two.FirstCookie);
        var audit = string.Join('\n', await ReadAsync(
            target,
            $"SELECT coalesce(metadata_json, '') || coalesce(security_description, '') FROM {AuditTable}"));
        foreach (var text in new[]
                 {
                     written.Body,
                     writerView.Body,
                     readerView.Body,
                    audit,
                    two.First.Output,
                    two.Second.Output,
                 })
        {
            Assert.DoesNotContain(TokenPlaintext, text, StringComparison.Ordinal);
            Assert.DoesNotContain("sm:v1:", text, StringComparison.Ordinal);
            Assert.DoesNotContain(RootKey, text, StringComparison.Ordinal);
        }
    }

    // --- helpers --------------------------------------------------------------------------------

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    /// <summary>
    /// Starts two independently logged-in processes over one completed target and returns their
    /// addresses, sessions, and the maintenance facts the assertions need.
    /// </summary>
    private async Task<TwoInstances> StartTwoAsync(string name)
    {
        RequireDatabase();
        await ExecuteAsync(maintenanceConnectionString!, $"""DROP DATABASE IF EXISTS "{DatabaseName(name)}" WITH (FORCE)""");
        await ExecuteAsync(maintenanceConnectionString!, $"""CREATE DATABASE "{DatabaseName(name)}" """);
        var target = Target(name);
        var working = TemporaryDirectory.Create();
        var arguments = ProcessArguments(target);

        var first = ReferenceServiceProcess.Start(working.Path, arguments);
        var firstAddress = await first.WaitUntilListeningAsync(Token);
        await ExecuteAsync(target, $"""
            UPDATE public."{InstallationTable}" SET status = 1, completed_at_utc = now() WHERE service_id = 'reference-service'
            """);
        var second = ReferenceServiceProcess.Start(working.Path, arguments);
        var secondAddress = await second.WaitUntilListeningAsync(Token);
        return new TwoInstances(
            target,
            first,
            second,
            firstAddress,
            secondAddress,
            await LoginAsync(firstAddress),
            await LoginAsync(secondAddress),
            working);
    }

    private sealed record TwoInstances(
        string Target,
        ReferenceServiceProcess First,
        ReferenceServiceProcess Second,
        Uri FirstAddress,
        Uri SecondAddress,
        string FirstCookie,
        string SecondCookie,
        TemporaryDirectory Working);

    private static string DatabaseName(string name) => $"reference_settingx_{name}";

    private string Target(string name) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = DatabaseName(name),
            IncludeErrorDetail = false,
        }.ConnectionString;

    private static string[] ProcessArguments(string target) =>
    [
        "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
        "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, target,
        "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
        "--ReferenceService:Management:RootKey", RootKey,
        "--ReferenceService:Management:Operators:0:Id", OperatorId,
        "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
        "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
        "--ReferenceService:Management:Operators:0:Credential", OperatorCredential,
    ];

    private static async Task<string> LoginAsync(Uri address)
    {
        using var client = new HttpClient { BaseAddress = address, Timeout = ReferenceServiceBudgets.Request };
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(
                $$"""{"username":"{{OperatorId}}","secret":"{{OperatorCredential}}"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> PostSettingsAsync(
        Uri address,
        string cookie,
        string json)
    {
        using var client = new HttpClient { BaseAddress = address, Timeout = ReferenceServiceBudgets.Request };
        using var request = new HttpRequestMessage(HttpMethod.Post, ValuesPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request, Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    private static async Task<(int Version, string Body)> GetSettingsAsync(Uri address, string cookie)
    {
        using var client = new HttpClient { BaseAddress = address, Timeout = ReferenceServiceBudgets.Request };
        using var request = new HttpRequestMessage(HttpMethod.Get, ValuesPath);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        using var document = JsonDocument.Parse(body);
        return (document.RootElement.GetProperty("version").GetInt32(), body);
    }

    private static void AssertNoSecrets(params string[] texts)
    {
        foreach (var text in texts)
        {
            Assert.DoesNotContain(RootKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain(OperatorCredential, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, text, StringComparison.Ordinal);
        }
    }

    private static async Task<List<string>> ReadAsync(string connectionString, string query)
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

    private static async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";
}
