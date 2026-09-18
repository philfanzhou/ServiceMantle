using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Npgsql;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Discovery;
using ServiceMantle.Testing;
using Testcontainers.Consul;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Drives two real reference-service processes against one real Consul agent over one PostgreSQL
/// target: while the discovery settings are still at their disabled default nothing reaches the
/// agent, an enabled but not-ready instance registers nothing, an agent outage during readiness is
/// retried by both running hosts and recovers to exactly one registration per instance, both
/// instances carry their own registration identity with an agent health check that passes against
/// their real readiness endpoint, losing readiness and stopping deregister reliably, and the ACL
/// token never reaches a console.
/// </summary>
/// <remarks>
/// <para>
/// The hosts are real operating-system processes and the agent is a real single-node dev Consul,
/// so every registration fact is asserted against the agent's own HTTP API. The internal
/// guarantees the process-own suites already prove - zero clients while disabled, non-overlapping
/// retry, the combination validation, the restart-only settings refresh, the token boundary of
/// logs, exceptions, and responses - are deliberately not repeated here; this file owns only the
/// cross-instance observations a real agent can confirm, and the contract's non-guarantees
/// (propagation-delay bounds, clusters, multi-datacenter, ACL topologies, failover) stay intact.
/// </para>
/// <para>
/// Topology: each instance binds a test-chosen port on every interface and advertises
/// <c>host.docker.internal</c> to the agent; the agent container resolves that name to the host
/// through an extra host-gateway mapping, so its health checks reach each process's real
/// <c>/health/ready</c>. The shared real-database policy applies:
/// <c>RUN_SERVICEMANTLE_POSTGRES_TESTS=true</c> and a running Docker daemon.
/// </para>
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceConsulCrossInstanceTests : IAsyncLifetime
{
    private const string SettingsPath = "/management/v1/settings";
    private const string LoginPath = "/management/v1/session/login";
    private const string UnsafeRequestHeader = "X-ServiceMantle-Request";
    private const string ReadyPath = "/health/ready";
    private const string InstallationTable = "service_installations";
    private const string WorkspacesTable = "reference_workspaces";

    private const string ServiceName = "reference-service";
    private const string InstanceA = "reference-a";
    private const string InstanceB = "reference-b";
    private const string AdvertisedHost = "host.docker.internal";

    // Synthetic fixture secrets, asserted to stay out of every response and captured output.
    private const string SyntheticUser = "reference_consulx_owner";
    private const string SyntheticPassword = "synthetic-reference-consulx-secret";
    private const string RootKey = "synthetic-reference-consulx-root-key";
    private const string OperatorId = "ops-admin";
    private const string OperatorCredential = "synthetic-consulx-operator-secret";

    // The ACL token the registration presents as discovery.credential: the dev agent accepts it,
    // and no console of any process generation may ever show it.
    private const string AclToken = "synthetic-consulx-acl-token";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? postgres;
    private ConsulContainer? consul;
    private string? maintenanceConnectionString;
    private int agentPort;

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        postgres = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("reference_consulx_maintenance")
            .WithUsername(SyntheticUser)
            .WithPassword(SyntheticPassword)
            .Build();
        await postgres.StartAsync(Token);
        maintenanceConnectionString = postgres.GetConnectionString();

        // The agent must answer on one fixed host port across the outage restart of its scenario:
        // an engine-assigned random port is re-assigned by a container restart, which would break
        // both this test's queries and the endpoint the instances captured in their settings
        // snapshot before the outage. A test-chosen free port carries the same reservation posture
        // as the ports the two processes bind.
        agentPort = ReserveFreePort();
        consul = new ConsulBuilder(GetConsulImage())
            .WithPortBinding(agentPort, ConsulBuilder.ConsulHttpPort)
            // The agent's health checks must reach the host's processes back, which a Linux engine
            // only allows through the host-gateway mapping of host.docker.internal. Testcontainers
            // always creates the host config a modifier receives.
            .WithCreateParameterModifier(parameters => parameters.HostConfig!.ExtraHosts =
                new[] { "host.docker.internal:host-gateway" })
            .Build();
        await consul.StartAsync(Token);
        using (var agent = new HttpClient { BaseAddress = AgentBaseAddress, Timeout = ReferenceServiceBudgets.Request })
        {
            await WaitUntilLeaderAsync(agent);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (postgres is not null)
        {
            await postgres.StopAsync(Token);
            await postgres.DisposeAsync();
        }

        if (consul is not null)
        {
            await consul.StopAsync(Token);
            await consul.DisposeAsync();
        }
    }

    [Fact]
    public async Task Two_instances_register_and_deregister_only_through_readiness_against_one_real_agent()
    {
        RequireDatabase();
        var target = await CreateTargetAsync();
        using var working = TemporaryDirectory.Create();
        using var agent = new HttpClient { BaseAddress = AgentBaseAddress, Timeout = ReferenceServiceBudgets.Request };
        var portA = ReserveFreePort();
        var portB = ReserveFreePort();

        // --- G-A: the disabled path keeps a running agent's catalog empty ----------------------------
        //
        // The snapshot still carries the disabled default of discovery.enabled, so neither instance
        // has anything to register; more than three readiness sampling intervals pass with a live
        // agent observing nothing. The zero-client internals of this path stay with the in-process
        // suites; here only the agent-observable absence is claimed.
        await using var firstInitial = await StartInstanceAsync(working.Path, target, InstanceA, portA);
        await using var secondInitial = await StartInstanceAsync(working.Path, target, InstanceB, portB);
        Assert.Empty(await ReadCatalogAsync(agent));
        await Task.Delay(TimeSpan.FromSeconds(3.5), Token);
        Assert.Empty(await ReadCatalogAsync(agent));

        // The configuration change of this suite: the installation completes, the full discovery
        // combination is written through instance A's management endpoint, and both instances
        // restart - the moment discovery.* takes effect. The service-level address and port below
        // are the shared fallback both instances must override with their own advertisement.
        await ExecuteAsync(target, CompleteInstallationSql());
        var cookie = await LoginAsync(portA);
        using var written = await PostSettingsAsync(portA, cookie, DiscoveryCombination(AgentBaseAddress));
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        await firstInitial.ShutDownAsync(Token);
        await secondInitial.ShutDownAsync(Token);

        // --- G-B: enabled and not ready registers nothing --------------------------------------------
        //
        // Both instances restarted onto the enabled snapshot, so the client exists and readiness is
        // the only gate left: the workspace row is missing, both readiness endpoints answer the
        // workspace-missing refusal, and more than three sampling intervals leave the catalog empty.
        await using var first = await StartInstanceAsync(working.Path, target, InstanceA, portA);
        await using var second = await StartInstanceAsync(working.Path, target, InstanceB, portB);
        await AssertReadyAsync(portA, HttpStatusCode.ServiceUnavailable, "reference.workspace_missing");
        await AssertReadyAsync(portB, HttpStatusCode.ServiceUnavailable, "reference.workspace_missing");
        Assert.Empty(await ReadCatalogAsync(agent));
        await Task.Delay(TimeSpan.FromSeconds(3.5), Token);
        Assert.Empty(await ReadCatalogAsync(agent));

        // --- G-C: an agent outage during readiness is retried and recovers ---------------------------
        //
        // The agent goes away first, so the registrations the workspace row now triggers cannot
        // succeed: both hosts keep running through the refused connections and their contract
        // backoff (the retry internals stay with the in-process suite), and the recovered agent
        // ends up with exactly one registration per instance.
        await consul!.StopAsync(Token);
        await ExecuteAsync(target, InsertWorkspaceSql());
        await Task.Delay(TimeSpan.FromSeconds(3.5), Token);
        Assert.False(first.HasExited, "instance A must survive the agent outage");
        Assert.False(second.HasExited, "instance B must survive the agent outage");
        await Assert.ThrowsAsync<HttpRequestException>(
            () => agent.GetAsync("v1/catalog/service/" + ServiceName, Token));
        await consul.StartAsync(Token);
        await WaitUntilLeaderAsync(agent);
        await WaitCatalogAsync(
            agent,
            entries => HasExactlyTheseInstances(entries, InstanceA, InstanceB),
            "exactly one registration per instance after the agent recovered");

        // --- G-D: both instances carry their own identity, and the agent's checks observe the
        // real readiness of each process ----------------------------------------------------------------
        await AssertReadyAsync(portA, HttpStatusCode.OK, "\"phase\":\"completed\"");
        await AssertReadyAsync(portB, HttpStatusCode.OK, "\"phase\":\"completed\"");
        var registered = await WaitCatalogAsync(
            agent,
            entries => HasExactlyTheseInstances(entries, InstanceA, InstanceB),
            "both instance registrations");
        Assert.Equal(2, registered.Count);
        foreach (var entry in registered)
        {
            Assert.Equal(ServiceName, entry.ServiceName);
            Assert.Equal(AdvertisedHost, entry.ServiceAddress);
            Assert.Equal(entry.ServiceId == RegistrationId(InstanceA) ? portA : portB, entry.ServicePort);
        }

        var passing = await WaitPassingInstanceIdsAsync(agent);
        Assert.Equal(
            new HashSet<string> { RegistrationId(InstanceA), RegistrationId(InstanceB) },
            passing.ToHashSet());

        // --- G-E: losing readiness deregisters; the input returning registers the same ids again ----
        await ExecuteAsync(target, $"DELETE FROM {WorkspacesTable}");
        await WaitCatalogAsync(agent, entries => entries.Count == 0, "an empty catalog after readiness is lost");
        await ExecuteAsync(target, InsertWorkspaceSql());
        await WaitCatalogAsync(
            agent,
            entries => HasExactlyTheseInstances(entries, InstanceA, InstanceB),
            "the same two registrations after readiness returns");

        // --- G-F: a graceful stop deregisters exactly the stopping instance ---------------------------
        if (ReferenceServiceProcess.SupportsGracefulShutdownSignal)
        {
            Assert.Equal(0, await first.ShutDownAsync(Token));
            await WaitCatalogAsync(
                agent,
                entries => HasExactlyTheseInstances(entries, InstanceB),
                "only instance B after instance A stops");
            Assert.Equal(0, await second.ShutDownAsync(Token));
            await WaitCatalogAsync(agent, entries => entries.Count == 0, "an empty catalog after both stop");
        }
        else
        {
            // No graceful shutdown signal exists on this platform, so the deregistration-on-stop
            // evidence comes from the gated Linux CI path; both processes are still reclaimed here
            // so the token evidence below observes their complete output.
            await first.ShutDownAsync(Token);
            await second.ShutDownAsync(Token);
        }

        // --- G-G: the ACL token never reached a console of any process generation --------------------
        //
        // The captured output is the log face of these hosts. The metrics face does not exist in
        // this deployment form - neither the Prometheus endpoint nor the phase metrics are enabled
        // - so there is no metrics surface to assert; that is recorded here rather than being
        // replaced by an empty assertion.
        AssertNoSecrets(
            target,
            firstInitial.Output,
            secondInitial.Output,
            first.Output,
            second.Output);
    }

    // --- helpers --------------------------------------------------------------------------------

    private static string RegistrationId(string instanceId) => ServiceName + ":" + instanceId;

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private Uri AgentBaseAddress => new($"http://127.0.0.1:{agentPort.ToString(CultureInfo.InvariantCulture)}/");

    private async Task<string> CreateTargetAsync()
    {
        await ExecuteAsync(
            maintenanceConnectionString!, $"""DROP DATABASE IF EXISTS "reference_consulx_lifecycle" WITH (FORCE)""");
        await ExecuteAsync(maintenanceConnectionString!, $"""CREATE DATABASE "reference_consulx_lifecycle" """);
        return new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = "reference_consulx_lifecycle",
            IncludeErrorDetail = false,
        }.ConnectionString;
    }

    /// <summary>
    /// Starts one real process bound on every interface of a test-chosen port and waits until it
    /// listens, so the containerized agent can reach the process back through the advertised host.
    /// </summary>
    private static async Task<ReferenceServiceProcess> StartInstanceAsync(
        string workingDirectory,
        string target,
        string instanceId,
        int port)
    {
        var process = ReferenceServiceProcess.Start(
            workingDirectory,
            new Uri($"http://0.0.0.0:{port.ToString(CultureInfo.InvariantCulture)}"),
            ProcessArguments(target, instanceId, port));
        await process.WaitUntilListeningAsync(Token);
        return process;
    }

    private static string[] ProcessArguments(string target, string instanceId, int port) =>
    [
        "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
        "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, target,
        "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
        "--ReferenceService:Management:RootKey", RootKey,
        "--ReferenceService:Management:Operators:0:Id", OperatorId,
        "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
        "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
        "--ReferenceService:Management:Operators:0:Credential", OperatorCredential,
        "--" + ReferenceConsulDefaults.EnabledKey, "true",
        "--" + ReferenceConsulDefaults.InstanceIdKey, instanceId,
        "--" + ReferenceConsulDefaults.AdvertisedAddressKey, AdvertisedHost,
        "--" + ReferenceConsulDefaults.AdvertisedPortKey, port.ToString(CultureInfo.InvariantCulture),
    ];

    private static string DiscoveryCombination(Uri endpoint) => """
        {"expectedVersion":0,"changes":[
            {"key":"discovery.enabled","value":"true"},
            {"key":"discovery.endpoint","value":"ENDPOINT"},
            {"key":"discovery.credential","value":"TOKEN"},
            {"key":"discovery.service-name","value":"reference-service"},
            {"key":"discovery.address","value":"10.0.0.5"},
            {"key":"discovery.port","value":"8080"}]}
        """
        .Replace("ENDPOINT", endpoint.ToString())
        .Replace("TOKEN", AclToken);

    private static string CompleteInstallationSql() =>
        $"""
        UPDATE public."{InstallationTable}" SET status = 1, completed_at_utc = now()
        WHERE service_id = 'reference-service'
        """;

    private static string InsertWorkspaceSql() =>
        $"""INSERT INTO {WorkspacesTable} ("Id", "DisplayName") VALUES (gen_random_uuid(), 'discovery')""";

    private static async Task AssertReadyAsync(int port, HttpStatusCode expected, string marker)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
            Timeout = ReferenceServiceBudgets.Request,
        };
        using var response = await client.GetAsync(ReadyPath, Token);
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.Contains(marker, body, StringComparison.Ordinal);
    }

    private static async Task<string> LoginAsync(int port)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
            Timeout = ReferenceServiceBudgets.Request,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(
                $$"""{"username":"{{OperatorId}}","secret":"{{OperatorCredential}}"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        using var login = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
    }

    private static async Task<HttpResponseMessage> PostSettingsAsync(int port, string cookie, string json)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
            Timeout = ReferenceServiceBudgets.Request,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, SettingsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return await client.SendAsync(request, Token);
    }

    private static bool HasExactlyTheseInstances(List<CatalogEntry> entries, params string[] instanceIds) =>
        entries
            .Select(entry => entry.ServiceId)
            .ToHashSet()
            .SetEquals(instanceIds.Select(RegistrationId));

    private static async Task<List<CatalogEntry>> ReadCatalogAsync(HttpClient agent)
    {
        using var response = await agent.GetAsync("v1/catalog/service/" + ServiceName, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ParseCatalog(await response.Content.ReadAsStringAsync(Token));
    }

    private static List<CatalogEntry> ParseCatalog(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
        {
            // Consul answers JSON null where no instance of the service is registered.
            return [];
        }

        return document.RootElement.EnumerateArray()
            .Select(entry => new CatalogEntry(
                entry.GetProperty("ServiceID").GetString()!,
                entry.GetProperty("ServiceName").GetString()!,
                entry.GetProperty("ServiceAddress").GetString()!,
                entry.GetProperty("ServicePort").GetInt32()))
            .ToList();
    }

    private static async Task<List<CatalogEntry>> WaitCatalogAsync(
        HttpClient agent,
        Func<List<CatalogEntry>, bool> condition,
        string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var entries = await TryReadCatalogAsync(agent);
            if (entries is not null && condition(entries))
            {
                return entries;
            }

            Assert.True(
                DateTime.UtcNow < deadline,
                $"the Consul catalog did not reach {what} in time; last observed: " +
                Describe(entries ?? []));
            await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        }
    }

    /// <summary>
    /// Reads the catalog for the polls, where a momentarily refused connection - a just-restarted
    /// agent's published port settles only briefly after the engine reports it up - counts as
    /// "not yet" rather than as the wait's outcome.
    /// </summary>
    private static async Task<List<CatalogEntry>?> TryReadCatalogAsync(HttpClient agent)
    {
        try
        {
            return await ReadCatalogAsync(agent);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<List<string>> WaitPassingInstanceIdsAsync(HttpClient agent)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            List<string>? ids = null;
            try
            {
                using var response = await agent.GetAsync(
                    "v1/health/service/" + ServiceName + "?passing=true", Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(Token);
                using var document = JsonDocument.Parse(body);
                ids = document.RootElement.ValueKind is JsonValueKind.Array
                    ? document.RootElement.EnumerateArray()
                        .Select(entry => entry.GetProperty("Service").GetProperty("ID").GetString()!)
                        .ToList()
                    : [];
            }
            catch (HttpRequestException)
            {
                // The same transient a restarted agent's port settles out of.
            }

            if (ids is { Count: 2 })
            {
                return ids;
            }

            Assert.True(
                DateTime.UtcNow < deadline,
                "the agent's health checks did not pass for both instances in time; last observed: " +
                string.Join(", ", ids ?? []));
            await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        }
    }

    private static async Task WaitUntilLeaderAsync(HttpClient agent)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                var leader = (await agent.GetStringAsync("v1/status/leader", Token)).Trim('"');
                if (leader.Length > 0)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The same transient a restarted agent's port settles out of.
            }

            Assert.True(DateTime.UtcNow < deadline, "the Consul agent did not elect a leader in time");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        }
    }

    private static int ReserveFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static string Describe(IEnumerable<CatalogEntry> entries) => string.Join(
        ", ",
        entries.Select(entry => $"{entry.ServiceId}@{entry.ServiceAddress}:{entry.ServicePort.ToString(CultureInfo.InvariantCulture)}"));

    private static void AssertNoSecrets(string target, params string[] outputs)
    {
        foreach (var output in outputs)
        {
            Assert.DoesNotContain(AclToken, output, StringComparison.Ordinal);
            Assert.DoesNotContain(RootKey, output, StringComparison.Ordinal);
            Assert.DoesNotContain(OperatorCredential, output, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, output, StringComparison.Ordinal);
            Assert.DoesNotContain(target, output, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", output, StringComparison.OrdinalIgnoreCase);
        }
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

    private static string GetConsulImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_CONSUL_IMAGE") ?? "hashicorp/consul:1.20";

    private sealed record CatalogEntry(string ServiceId, string ServiceName, string ServiceAddress, int ServicePort);
}
