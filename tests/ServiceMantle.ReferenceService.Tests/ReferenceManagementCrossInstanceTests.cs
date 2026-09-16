using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Management;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Proves the management cookie key ring is genuinely shared: two independently started reference
/// service processes pointing at one PostgreSQL target accept the same cookie, the issuing process
/// can restart and still accept it, and neither process ever echoes the credential, the root key,
/// or the cookie value in its console output.
/// </summary>
/// <remarks>
/// The hosts are real operating-system processes started from the sample's own build output, so the
/// evidence is the real startup gate, the real Kestrel, the real key ring persisted through the
/// gate's context factory, and real HTTP. The shared real-database policy applies:
/// <c>RUN_SERVICEMANTLE_POSTGRES_TESTS=true</c> and a running Docker daemon.
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceManagementCrossInstanceTests : IAsyncLifetime
{
    private const string InstallationTable = "service_installations";
    private const string ServiceIdValue = "reference-service";
    private const string LoginPath = "/management/v1/session/login";
    private const string SessionPath = "/management/v1/session";
    private const string UnsafeRequestHeader = "X-ServiceMantle-Request";

    // Synthetic fixture secrets, asserted to stay out of every process's captured output.
    private const string SyntheticUser = "reference_cross_owner";
    private const string SyntheticPassword = "synthetic-reference-cross-secret";
    private const string OperatorId = "ops-admin";
    private const string OperatorCredential = "synthetic-operator-credential-secret";
    private const string RootKey = "synthetic-reference-management-root-key";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? container;
    private string? maintenanceConnectionString;
    private string workingDirectory = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("reference_cross_maintenance")
            .WithUsername(SyntheticUser)
            .WithPassword(SyntheticPassword)
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        maintenanceConnectionString = container.GetConnectionString();
        workingDirectory = Path.Combine(Path.GetTempPath(), $"sm-reference-cross-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(TestContext.Current.CancellationToken);
            await container.DisposeAsync();
        }

        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Two_instances_share_one_cookie_and_a_restart_keeps_accepting_it()
    {
        RequireDatabase();
        var target = await CreateTargetAsync();
        var arguments = ProcessArguments(target);

        await using var first = ReferenceServiceProcess.Start(workingDirectory, arguments);
        var firstAddress = await first.WaitUntilListeningAsync(Token);
        // The first process's own startup gate migrated the target and published its result; the
        // installation row is a database fact written with test SQL, never through a setup flow.
        await CompleteInstallationAsync(target);
        var cookie = await LoginAsync(firstAddress);

        // A second, independently started instance accepts the same cookie through the shared key
        // ring: same database, same root key, same application name.
        await using var second = ReferenceServiceProcess.Start(workingDirectory, arguments);
        var secondAddress = await second.WaitUntilListeningAsync(Token);

        var (firstStatus, firstBody) = await ReadSessionAsync(firstAddress, cookie);
        var (secondStatus, secondBody) = await ReadSessionAsync(secondAddress, cookie);
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.Equal(HttpStatusCode.OK, secondStatus);
        Assert.True(firstBody.GetProperty("authenticated").GetBoolean());
        Assert.True(secondBody.GetProperty("authenticated").GetBoolean());

        // Two instances can use the same cookie concurrently: only the key ring is shared, and it
        // carries no per-instance session state.
        var concurrentFirst = ReadSessionAsync(firstAddress, cookie);
        var concurrentSecond = ReadSessionAsync(secondAddress, cookie);
        Assert.All(await Task.WhenAll(concurrentFirst, concurrentSecond), result =>
            Assert.Equal(HttpStatusCode.OK, result.Status));

        // The issuing process restarts and still accepts the cookie: the key ring is persisted in
        // the database, not in process memory.
        var exit = await first.ShutDownAsync(Token);
        if (ReferenceServiceProcess.SupportsGracefulShutdownSignal)
        {
            Assert.Equal(0, exit);
        }

        await using var restarted = ReferenceServiceProcess.Start(workingDirectory, arguments);
        var restartedAddress = await restarted.WaitUntilListeningAsync(Token);
        var (restartedStatus, restartedBody) = await ReadSessionAsync(restartedAddress, cookie);
        Assert.Equal(HttpStatusCode.OK, restartedStatus);
        Assert.True(restartedBody.GetProperty("authenticated").GetBoolean());

        // No process echoed a credential, the root key, or the cookie value.
        foreach (var output in new[] { first.Output, second.Output, restarted.Output })
        {
            Assert.DoesNotContain(OperatorCredential, output, StringComparison.Ordinal);
            Assert.DoesNotContain(RootKey, output, StringComparison.Ordinal);
            Assert.DoesNotContain(cookie, output, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, output, StringComparison.Ordinal);
        }
    }

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private async Task<string> CreateTargetAsync()
    {
        RequireDatabase();
        await ExecuteAsync(maintenanceConnectionString!, """DROP DATABASE IF EXISTS "reference_cross_target" WITH (FORCE)""");
        await ExecuteAsync(maintenanceConnectionString!, """CREATE DATABASE "reference_cross_target" """);
        return Target();
    }

    private string Target() =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = "reference_cross_target",
            IncludeErrorDetail = false,
        }.ConnectionString;

    private static string[] ProcessArguments(string target) =>
    [
        "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
        "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, target,
        "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
        "--" + ReferenceManagementOptions.RootKeySetting, RootKey,
        "--ReferenceService:Management:Operators:0:Id", OperatorId,
        "--ReferenceService:Management:Operators:0:DisplayName", "Reference Operator",
        "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
        "--ReferenceService:Management:Operators:0:Credential", OperatorCredential,
    ];

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

    private static async Task<string> LoginAsync(Uri address)
    {
        using var client = new HttpClient { BaseAddress = address };
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
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(ManagementSessionDefaults.CookieName + "=", setCookie, StringComparison.Ordinal);
        return setCookie.Split(';', 2)[0];
    }

    private static async Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)> ReadSessionAsync(
        Uri address,
        string cookie)
    {
        using var client = new HttpClient { BaseAddress = address };
        using var request = new HttpRequestMessage(HttpMethod.Get, SessionPath);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        var body = System.Text.Json.JsonDocument.Parse(
            string.IsNullOrEmpty(text) ? "{}" : text).RootElement.Clone();
        return (response.StatusCode, body);
    }

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";
}
