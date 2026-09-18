using System.Globalization;
using System.Net;
using System.Text;
using Npgsql;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Installation.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Drives the sample's migration and one-shot Setup through two real operating-system processes
/// over one PostgreSQL target: the advisory-lock serialized cold start, the deterministic gate
/// race over one setup code, the failed completion that leaves nothing behind, and the death of
/// a process whose completion was mid-flight.
/// </summary>
/// <remarks>
/// <para>
/// Every case here starts the reference service's own build output as real processes, so the
/// evidence is the real advisory lock, the real migration history, the real one-shot executor
/// transactions, real HTTP, process exit, and the database facts left behind. The competition
/// barrier is a database sequence plus a BEFORE INSERT trigger, the same shape the single-process
/// concurrency row uses, so it crosses process boundaries by construction.
/// </para>
/// <para>
/// The shared real-database policy applies: <c>RUN_SERVICEMANTLE_POSTGRES_TESTS=true</c> and a
/// running Docker daemon.
/// </para>
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceSetupTwoProcessTests : IAsyncLifetime
{
    private const string SetupPath = "/management/v1/setup";
    private const string ReadyPath = "/health/ready";
    private const string UnsafeRequestHeader = "X-ServiceMantle-Request";
    private const string InstallationTable = "service_installations";
    private const string AuditTable = "service_audit_logs";
    private const string WorkspacesTable = "reference_workspaces";
    private const string HistoryTable = "__EFMigrationsHistory";
    private const string CodeBannerAnchor = "one-time setup code:";
    private const int DeathGateLock = 987654321;

    // Synthetic fixture secrets, asserted to stay out of every response and captured output.
    private const string SyntheticUser = "reference_setup2_owner";
    private const string SyntheticPassword = "synthetic-reference-setup2-secret";
    private const string RootKey = "synthetic-reference-setup2-root-key";
    private const string OperatorId = "ops-admin";
    private const string OperatorCredential = "synthetic-setup2-operator-secret";

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
            .WithDatabase("reference_setup2_maintenance")
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

    // --- 1: two processes cold-starting one empty target ----------------------------------------

    [Fact]
    public async Task Two_processes_cold_starting_one_empty_target_settle_on_one_pending_installation()
    {
        var (target, working) = await CreateEmptyTargetAsync("cold-start");
        using var workingScope = working;
        var arguments = ProcessArguments(target);

        // Both processes spawn before either is awaited, so their startup gates genuinely overlap
        // on the empty target; the advisory lock is the only serializer.
        await using var first = ReferenceServiceProcess.Start(working.Path, arguments);
        await using var second = ReferenceServiceProcess.Start(working.Path, arguments);
        var firstAddress = await first.WaitUntilListeningAsync(Token);
        var secondAddress = await second.WaitUntilListeningAsync(Token);

        // Exactly one process prints the plaintext; the other prints the fixed already-issued
        // hint. Which one wins is not asserted - it is a documented non-guarantee.
        await WaitUntilAsync(() =>
            CountOccurrences(first.Output, CodeBannerAnchor) + CountOccurrences(second.Output, CodeBannerAnchor) == 1);
        var issuer = CountOccurrences(first.Output, CodeBannerAnchor) == 1 ? first : second;
        var other = ReferenceEquals(issuer, first) ? second : first;
        await WaitUntilAsync(() => other.Output.Contains(
            ReferenceSetupCodeIssuer.AlreadyIssuedHint, StringComparison.Ordinal));
        var code = ExtractCode(issuer.Output);

        // One installation row, still pending, with one digest; the schema history carries the
        // full known migration set exactly once - the second process added no second history.
        Assert.Equal(["0"], await ReadAsync(target, $"SELECT status::text FROM {InstallationTable}"));
        Assert.Single(await ReadAsync(target, $"SELECT setup_code_digest FROM {InstallationTable}"));
        Assert.Equal(
            ["4"],
            await ReadAsync(target, $"""SELECT count(*)::text FROM public."{HistoryTable}" """));

        // Both processes serve the pending phase identically.
        var healthBodies = new List<string>();
        foreach (var address in new[] { firstAddress, secondAddress })
        {
            var (status, body) = await GetAsync(address, ReadyPath);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.Contains("pendingSetup", body, StringComparison.Ordinal);
            healthBodies.Add(body);
        }

        AssertOutputBoundaries(code, issuer, other);
        AssertResponseBoundaries(code, [.. healthBodies]);
    }

    // --- 2: the deterministic gate race over one code -------------------------------------------

    [Fact]
    public async Task The_deterministic_gate_race_completes_exactly_one_installation()
    {
        var (target, working) = await CreateEmptyTargetAsync("gate-race");
        using var workingScope = working;
        await using var first = ReferenceServiceProcess.Start(working.Path, ProcessArguments(target));
        var firstAddress = await first.WaitUntilListeningAsync(Token);
        var code = await WaitForCodeAsync(first);
        await using var second = ReferenceServiceProcess.Start(working.Path, ProcessArguments(target));
        var secondAddress = await second.WaitUntilListeningAsync(Token);
        await WaitUntilAsync(() => second.Output.Contains(
            ReferenceSetupCodeIssuer.AlreadyIssuedHint, StringComparison.Ordinal));
        var versionBefore = Assert.Single(await ReadAsync(target, $"SELECT version::text FROM {InstallationTable}"));

        // The arrival marker is a sequence, so both sessions see each other's arrival even before
        // either commits: the first to arrive at the workspace insert waits for the second before
        // either saves on.
        await ExecuteAsync(target, """
            CREATE SEQUENCE IF NOT EXISTS setup2_arrivals;
            CREATE OR REPLACE FUNCTION setup2_sync_gate() RETURNS trigger AS $$
            DECLARE n bigint;
            BEGIN
                n := nextval('setup2_arrivals');
                WHILE (SELECT last_value FROM setup2_arrivals) < 2 LOOP
                    PERFORM pg_sleep(0.05);
                END LOOP;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER setup2_sync BEFORE INSERT ON reference_workspaces
                FOR EACH ROW EXECUTE FUNCTION setup2_sync_gate();
            """);

        var raced = await Task.WhenAll(
            PostSetupAsync(firstAddress, code),
            PostSetupAsync(secondAddress, code));

        Assert.Single(raced, result => result.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(raced, result => result.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(["1"], await ReadAsync(target, $"SELECT count(*)::text FROM {WorkspacesTable}"));
        Assert.Equal(
            ["1"],
            await ReadAsync(target, $"SELECT count(*)::text FROM {AuditTable} WHERE action = 'installation.completed'"));
        var versionAfter = Assert.Single(await ReadAsync(target, $"SELECT version::text FROM {InstallationTable}"));
        Assert.Equal(
            int.Parse(versionBefore, CultureInfo.InvariantCulture) + 1,
            int.Parse(versionAfter, CultureInfo.InvariantCulture));

        // Both processes observe the same completed installation through the read endpoint.
        var readBodies = new List<string>();
        foreach (var address in new[] { firstAddress, secondAddress })
        {
            var (status, body) = await GetAsync(address, SetupPath);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"status\":\"completed\"", body, StringComparison.Ordinal);
            readBodies.Add(body);
        }

        AssertOutputBoundaries(code, first, second);
        AssertResponseBoundaries(code, [.. raced.Select(result => result.Body).Concat(readBodies)]);
    }

    // --- 3: a failed completion leaves nothing and the same code still completes ------------------

    [Fact]
    public async Task A_failed_completion_leaves_nothing_and_the_same_code_still_completes()
    {
        var (target, working) = await CreateEmptyTargetAsync("audit-failure");
        using var workingScope = working;
        await using var first = ReferenceServiceProcess.Start(working.Path, ProcessArguments(target));
        var firstAddress = await first.WaitUntilListeningAsync(Token);
        var code = await WaitForCodeAsync(first);
        await using var second = ReferenceServiceProcess.Start(working.Path, ProcessArguments(target));
        var secondAddress = await second.WaitUntilListeningAsync(Token);
        await WaitUntilAsync(() => second.Output.Contains(
            ReferenceSetupCodeIssuer.AlreadyIssuedHint, StringComparison.Ordinal));
        var digestBefore = Assert.Single(await ReadAsync(target, $"SELECT setup_code_digest FROM {InstallationTable}"));
        var versionBefore = Assert.Single(await ReadAsync(target, $"SELECT version::text FROM {InstallationTable}"));

        // The audit insert fails inside the first process's completion transaction.
        await ExecuteAsync(target, """
            CREATE OR REPLACE FUNCTION setup2_fail_audit() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'synthetic audit failure';
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER setup2_audit_failure BEFORE INSERT ON service_audit_logs
                FOR EACH ROW EXECUTE FUNCTION setup2_fail_audit();
            """);

        var failed = await PostSetupAsync(firstAddress, code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);

        // Zero residue: no workspace, no audit row, the installation still pending with its
        // version and code material untouched.
        Assert.Equal(["0"], await ReadAsync(target, $"SELECT count(*)::text FROM {WorkspacesTable}"));
        Assert.Equal(["0"], await ReadAsync(target, $"SELECT count(*)::text FROM {AuditTable}"));
        Assert.Equal(["0"], await ReadAsync(target, $"SELECT status::text FROM {InstallationTable}"));
        Assert.Equal(
            [digestBefore],
            await ReadAsync(target, $"SELECT setup_code_digest FROM {InstallationTable}"));
        Assert.Equal(
            [versionBefore],
            await ReadAsync(target, $"SELECT version::text FROM {InstallationTable}"));

        // The other process completes with the very same code.
        await ExecuteAsync(target, "DROP TRIGGER setup2_audit_failure ON service_audit_logs");
        var completed = await PostSetupAsync(secondAddress, code);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var versionAfter = Assert.Single(await ReadAsync(target, $"SELECT version::text FROM {InstallationTable}"));
        Assert.Equal(
            int.Parse(versionBefore, CultureInfo.InvariantCulture) + 1,
            int.Parse(versionAfter, CultureInfo.InvariantCulture));

        // The failed process observes the same completed installation the winner produced.
        var (status, body) = await GetAsync(firstAddress, SetupPath);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"status\":\"completed\"", body, StringComparison.Ordinal);

        AssertOutputBoundaries(code, first, second);
        AssertResponseBoundaries(code, [failed.Body, completed.Body, body]);
    }

    // --- 4: a process dying mid-completion leaves no half installation --------------------------

    [Fact]
    public async Task A_process_dying_mid_completion_leaves_no_half_installation()
    {
        var (target, working) = await CreateEmptyTargetAsync("process-death");
        using var workingScope = working;
        await using var first = ReferenceServiceProcess.Start(working.Path, ProcessArguments(target));
        var firstAddress = await first.WaitUntilListeningAsync(Token);
        var code = await WaitForCodeAsync(first);
        await using var second = ReferenceServiceProcess.Start(working.Path, ProcessArguments(target));
        var secondAddress = await second.WaitUntilListeningAsync(Token);
        await WaitUntilAsync(() => second.Output.Contains(
            ReferenceSetupCodeIssuer.AlreadyIssuedHint, StringComparison.Ordinal));
        var versionBefore = Assert.Single(await ReadAsync(target, $"SELECT version::text FROM {InstallationTable}"));

        // The workspace insert first marks its arrival on a sequence, then blocks on an advisory
        // lock this test holds, so the completion transaction is observably mid-flight.
        await ExecuteAsync(target, $"""
            CREATE SEQUENCE IF NOT EXISTS setup2_death_arrivals;
            CREATE OR REPLACE FUNCTION setup2_death_gate() RETURNS trigger AS $$
            DECLARE n bigint;
            BEGIN
                n := nextval('setup2_death_arrivals');
                PERFORM pg_advisory_lock({DeathGateLock});
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER setup2_death BEFORE INSERT ON reference_workspaces
                FOR EACH ROW EXECUTE FUNCTION setup2_death_gate();
            """);
        await using var gate = new NpgsqlConnection(target);
        await gate.OpenAsync(Token);
        await ExecuteOnAsync(gate, $"SELECT pg_advisory_lock({DeathGateLock})");

        var client = new HttpClient { BaseAddress = firstAddress, Timeout = ReferenceServiceBudgets.Request };
        var inFlight = PostSetupAsync(client, code);
        await WaitUntilAsync(async () =>
            (await ReadAsync(target, "SELECT last_value::text FROM setup2_death_arrivals"))[0] != "0");

        // The process tree dies while its completion is parked in the trigger. The in-flight
        // request ends in transport failure, and the aborted connection rolls the transaction
        // back with it.
        await first.DisposeAsync();
        Exception? ended = null;
        try
        {
            _ = await inFlight;
        }
        catch (Exception exception)
        {
            ended = exception;
        }

        Assert.NotNull(ended);
        Assert.Equal(["0"], await ReadAsync(target, $"SELECT count(*)::text FROM {WorkspacesTable}"));
        Assert.Equal(["0"], await ReadAsync(target, $"SELECT count(*)::text FROM {AuditTable}"));
        Assert.Equal(["0"], await ReadAsync(target, $"SELECT status::text FROM {InstallationTable}"));
        Assert.Equal(
            [versionBefore],
            await ReadAsync(target, $"SELECT version::text FROM {InstallationTable}"));

        // Once the gate opens, the surviving process completes with the same code.
        await ExecuteOnAsync(gate, $"SELECT pg_advisory_unlock({DeathGateLock})");
        client.Dispose();
        var completed = await PostSetupAsync(secondAddress, code);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var versionAfter = Assert.Single(await ReadAsync(target, $"SELECT version::text FROM {InstallationTable}"));
        Assert.Equal(
            int.Parse(versionBefore, CultureInfo.InvariantCulture) + 1,
            int.Parse(versionAfter, CultureInfo.InvariantCulture));
        Assert.Equal(
            ["1"],
            await ReadAsync(target, $"SELECT count(*)::text FROM {AuditTable} WHERE action = 'installation.completed'"));

        AssertOutputBoundaries(code, first, second);
        AssertResponseBoundaries(code, [completed.Body]);
    }

    // --- helpers --------------------------------------------------------------------------------

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private async Task<(string Target, TemporaryDirectory Working)> CreateEmptyTargetAsync(string name)
    {
        RequireDatabase();
        await ExecuteAsync(maintenanceConnectionString!, $"""DROP DATABASE IF EXISTS "{DatabaseName(name)}" WITH (FORCE)""");
        await ExecuteAsync(maintenanceConnectionString!, $"""CREATE DATABASE "{DatabaseName(name)}" """);
        return (
            new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
            {
                Database = DatabaseName(name),
                IncludeErrorDetail = false,
            }.ConnectionString,
            TemporaryDirectory.Create());
    }

    private static string DatabaseName(string name) => $"reference_setup2_{name}";

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

    private static async Task<(HttpStatusCode StatusCode, string Body)> PostSetupAsync(Uri address, string code)
    {
        using var client = new HttpClient { BaseAddress = address, Timeout = ReferenceServiceBudgets.Request };
        return await PostSetupAsync(client, code);
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> PostSetupAsync(HttpClient client, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SetupPath)
        {
            Content = new StringContent("{\"code\":\"" + code + "\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        using var response = await client.SendAsync(request, Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> GetAsync(Uri address, string path)
    {
        using var client = new HttpClient { BaseAddress = address, Timeout = ReferenceServiceBudgets.Request };
        using var response = await client.GetAsync(path, Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    private static async Task<string> WaitForCodeAsync(ReferenceServiceProcess service)
    {
        var deadline = DateTime.UtcNow + ReferenceServiceBudgets.Start;
        while (DateTime.UtcNow < deadline)
        {
            if (CountOccurrences(service.Output, CodeBannerAnchor) == 1)
            {
                return ExtractCode(service.Output);
            }

            Assert.False(
                service.HasExited,
                "the host exited before printing the setup code:" + Environment.NewLine + service.Output);
            await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
        }

        throw new InvalidOperationException("the setup code banner did not appear in time");
    }

    private static string ExtractCode(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(lines, CodeBannerAnchor);
        Assert.True(index >= 0, "no setup code banner was printed: " + output);
        return lines[index + 1];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + ReferenceServiceBudgets.Start;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the expected state did not arrive in time");
            await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + ReferenceServiceBudgets.Start;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the expected state did not arrive in time");
            await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
        }
    }

    /// <summary>
    /// The plaintext code belongs to exactly one console banner - the issuing process - and to no
    /// other captured output; the deployment secrets belong to none of them.
    /// </summary>
    private static void AssertOutputBoundaries(
        string code,
        ReferenceServiceProcess issuer,
        params ReferenceServiceProcess[] others)
    {
        Assert.Equal(1, CountOccurrences(issuer.Output, code));
        foreach (var process in others)
        {
            Assert.DoesNotContain(code, process.Output, StringComparison.Ordinal);
        }

        foreach (var process in new[] { issuer }.Concat(others))
        {
            Assert.DoesNotContain(RootKey, process.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(OperatorCredential, process.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, process.Output, StringComparison.Ordinal);
        }
    }

    private static void AssertResponseBoundaries(string code, params string[] bodies)
    {
        foreach (var text in bodies)
        {
            Assert.DoesNotContain(code, text, StringComparison.Ordinal);
            Assert.DoesNotContain(RootKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain(OperatorCredential, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, text, StringComparison.Ordinal);
        }
    }

    private static async Task<List<string>> ReadAsync(string connectionString, string query)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        return await ReadOnAsync(connection, query);
    }

    private static async Task<List<string>> ReadOnAsync(NpgsqlConnection connection, string query)
    {
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
        await ExecuteOnAsync(connection, statement);
    }

    private static async Task ExecuteOnAsync(NpgsqlConnection connection, string statement)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";
}
