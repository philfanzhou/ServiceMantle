using System.Net;
using Npgsql;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Observes the health surface of two real reference-service processes over one PostgreSQL
/// target: both instances report the same Live/Ready result at every explicit stable database
/// state, a business-readiness probe blocked by a held table lock ends as a caller cancellation
/// without a leak, and neither responses nor captured output carry a secret.
/// </summary>
/// <remarks>
/// <para>
/// The hosts are real operating-system processes, the readiness input is authoritative database
/// fact (the installation row and the workspace table) changed with test SQL between observation
/// points, and no process restarts along the way. Cross-instance agreement at an observation
/// point is this suite's acceptance object, not a runtime guarantee the sample declares.
/// </para>
/// <para>
/// The shared real-database policy applies: <c>RUN_SERVICEMANTLE_POSTGRES_TESTS=true</c> and a
/// running Docker daemon.
/// </para>
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceHealthCrossInstanceTests : IAsyncLifetime
{
    private const string ReadyPath = "/health/ready";
    private const string CompatPath = "/health";
    private const string LivePath = "/health/live";
    private const string InstallationTable = "service_installations";
    private const string WorkspacesTable = "reference_workspaces";

    // Synthetic fixture secrets, asserted to stay out of every health response and output.
    private const string SyntheticUser = "reference_healthx_owner";
    private const string SyntheticPassword = "synthetic-reference-healthx-secret";
    private const string RootKey = "synthetic-reference-healthx-root-key";
    private const string OperatorCredential = "synthetic-healthx-operator-secret";

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
            .WithDatabase("reference_healthx_maintenance")
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

    [Fact]
    public async Task Both_instances_report_the_same_live_and_ready_at_every_stable_state()
    {
        var (target, working, first, second) = await StartTwoAsync("phases");
        using var workingScope = working;
        await using var firstScope = first;
        await using var secondScope = second;

        // PendingSetup: live is unconditionally alive, readiness is closed on both instances.
        await AssertStablePhaseAsync(
            (first, second),
            HttpStatusCode.ServiceUnavailable,
            "pendingSetup");

        // The installation row flips to completed and one workspace appears: both instances turn
        // ready on their next request, with no restart anywhere.
        await CompleteInstallationAsync(target);
        await InsertWorkspaceAsync(target);
        await AssertStablePhaseAsync((first, second), HttpStatusCode.OK, "\"phase\":\"completed\"");

        // The business contributor loses its input: both instances close readiness again.
        await ExecuteAsync(target, $"DELETE FROM {WorkspacesTable}");
        await AssertStablePhaseAsync(
            (first, second),
            HttpStatusCode.ServiceUnavailable,
            "reference.workspace_missing");

        // The input returns: both instances recover.
        await InsertWorkspaceAsync(target);
        await AssertStablePhaseAsync((first, second), HttpStatusCode.OK, "\"phase\":\"completed\"");

        AssertNoSecrets(target, first.Output, second.Output);
    }

    [Fact]
    public async Task A_blocked_probe_ends_as_a_caller_cancellation_and_both_instances_recover()
    {
        var (target, working, first, second) = await StartTwoAsync("blocked-probe");
        using var workingScope = working;
        await using var firstScope = first;
        await using var secondScope = second;
        await CompleteInstallationAsync(target);
        await InsertWorkspaceAsync(target);
        await AssertStablePhaseAsync((first, second), HttpStatusCode.OK, "\"phase\":\"completed\"");

        // A test session holds the workspace table exclusively, so the business contributor's
        // existence probe parks inside the database on both instances.
        await using var blocker = new NpgsqlConnection(target);
        await blocker.OpenAsync(Token);
        await ExecuteOnAsync(blocker, $"BEGIN; LOCK TABLE {WorkspacesTable} IN ACCESS EXCLUSIVE MODE;");

        // The caller cancels its readiness request: the request ends in that cancellation, not in
        // a server error, and nothing of the blocked state is echoed anywhere.
        using var client = new HttpClient
        {
            BaseAddress = ParseListeningAddress(first.Output),
            Timeout = ReferenceServiceBudgets.Request,
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(400));
        Exception? ended = null;
        try
        {
            using var response = await client.GetAsync(ReadyPath, cancellation.Token);
        }
        catch (Exception exception)
        {
            ended = exception;
        }

        Assert.True(ended is OperationCanceledException or TaskCanceledException, $"unexpected end: {ended}");

        // The lock goes away; both instances answer ready again on their next request.
        await ExecuteOnAsync(blocker, "ROLLBACK;");
        await AssertStablePhaseAsync((first, second), HttpStatusCode.OK, "\"phase\":\"completed\"");
        AssertNoSecrets(target, first.Output, second.Output);
    }

    // --- helpers --------------------------------------------------------------------------------

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private async Task<(string Target, TemporaryDirectory Working, ReferenceServiceProcess First, ReferenceServiceProcess Second)>
        StartTwoAsync(string name)
    {
        RequireDatabase();
        await ExecuteAsync(maintenanceConnectionString!, $"""DROP DATABASE IF EXISTS "{DatabaseName(name)}" WITH (FORCE)""");
        await ExecuteAsync(maintenanceConnectionString!, $"""CREATE DATABASE "{DatabaseName(name)}" """);
        var target = Target(name);
        var working = TemporaryDirectory.Create();
        var arguments = ProcessArguments(target);

        var first = ReferenceServiceProcess.Start(working.Path, arguments);
        var second = ReferenceServiceProcess.Start(working.Path, arguments);
        await first.WaitUntilListeningAsync(Token);
        await second.WaitUntilListeningAsync(Token);
        return (target, working, first, second);
    }

    private static Uri ParseListeningAddress(string output)
    {
        var line = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Single(candidate => candidate.StartsWith("Now listening on: ", StringComparison.Ordinal));
        return new Uri(line["Now listening on: ".Length..], UriKind.Absolute);
    }

    private static string DatabaseName(string name) => $"reference_healthx_{name}";

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
        "--ReferenceService:Management:Operators:0:Id", "ops-admin",
        "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
        "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
        "--ReferenceService:Management:Operators:0:Credential", OperatorCredential,
    ];

    /// <summary>
    /// One explicit stable observation point: both instances answer live 200, and ready and the
    /// compatibility endpoint agree with the expected result and marker on both instances.
    /// </summary>
    private static async Task AssertStablePhaseAsync(
        (ReferenceServiceProcess First, ReferenceServiceProcess Second) processes,
        HttpStatusCode expectedReady,
        string marker)
    {
        foreach (var process in new[] { processes.First, processes.Second })
        {
            var address = ParseListeningAddress(process.Output);
            using var client = new HttpClient { BaseAddress = address, Timeout = ReferenceServiceBudgets.Request };

            using var live = await client.GetAsync(LivePath, Token);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);

            using var ready = await client.GetAsync(ReadyPath, Token);
            Assert.Equal(expectedReady, ready.StatusCode);
            var readyBody = await ready.Content.ReadAsStringAsync(Token);
            Assert.Contains(marker, readyBody, StringComparison.Ordinal);

            // The compatibility endpoint is the same handler: same status, same body.
            using var compat = await client.GetAsync(CompatPath, Token);
            Assert.Equal(expectedReady, compat.StatusCode);
            Assert.Equal(readyBody, await compat.Content.ReadAsStringAsync(Token));
        }
    }

    private Task CompleteInstallationAsync(string target) =>
        ExecuteAsync(target, $"""
            UPDATE public."{InstallationTable}" SET status = 1, completed_at_utc = now() WHERE service_id = 'reference-service'
            """);

    private Task InsertWorkspaceAsync(string target) =>
        ExecuteAsync(
            target,
            $"""INSERT INTO {WorkspacesTable} ("Id", "DisplayName") VALUES (gen_random_uuid(), 'health')""");

    private void AssertNoSecrets(string target, params string[] outputs)
    {
        foreach (var output in outputs)
        {
            Assert.DoesNotContain(SyntheticPassword, output, StringComparison.Ordinal);
            Assert.DoesNotContain(RootKey, output, StringComparison.Ordinal);
            Assert.DoesNotContain(OperatorCredential, output, StringComparison.Ordinal);
            Assert.DoesNotContain(target, output, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", output, StringComparison.OrdinalIgnoreCase);
        }
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
