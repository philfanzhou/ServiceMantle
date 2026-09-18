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
    private const string KeyRingTable = "service_data_protection_keys";
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

    // --- 1: a second root key closes login and never accepts the first instance's cookies ------

    [Fact]
    public async Task A_second_root_key_fails_login_closed_and_never_accepts_the_first_cookies()
    {
        RequireDatabase();
        var target = await CreateTargetAsync();
        var responses = new CapturedResponses();

        await using var first = ReferenceServiceProcess.Start(workingDirectory, ProcessArguments(target));
        var firstAddress = await first.WaitUntilListeningAsync(Token);
        await CompleteInstallationAsync(target);
        var cookie = await LoginAsync(firstAddress, responses);
        var ringBefore = await ReadKeyRingAsync(target);

        // Same database, same service identity, a second valid root key of the same length: the
        // ring row was protected under the first key, so the second process must fail closed
        // instead of issuing cookies over material it cannot read.
        var secondKey = "synthetic-reference-management-r00t-key";
        Assert.Equal(RootKey.Length, secondKey.Length);
        await using var second = ReferenceServiceProcess.Start(
            workingDirectory, ProcessArguments(target, rootKey: secondKey));
        var secondAddress = await second.WaitUntilListeningAsync(Token);

        var secondLogin = await TryLoginAsync(secondAddress, responses);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, secondLogin.Status);
        Assert.Contains("management.session.unavailable", secondLogin.Body, StringComparison.Ordinal);

        // The first instance's cookie establishes no authenticated session on the second process.
        var secondSession = await ReadSessionAsync(secondAddress, cookie, responses);
        AssertNotAuthenticated(secondSession);

        // The closed second process changed nothing: the same row with the same ciphertext, and
        // the first instance keeps accepting the session it issued.
        Assert.Equal(ringBefore, await ReadKeyRingAsync(target));
        var firstSession = await ReadSessionAsync(firstAddress, cookie, responses);
        Assert.Equal(HttpStatusCode.OK, firstSession.Status);
        Assert.True(firstSession.Body.GetProperty("authenticated").GetBoolean());

        AssertBoundaries(responses, first.Output, second.Output, cookies: []);
    }

    // --- 2: a corrupted key ring row fails both instances closed --------------------------------

    [Fact]
    public async Task A_corrupted_key_ring_row_closes_every_fresh_reader_without_leaking_material()
    {
        RequireDatabase();
        var target = await CreateTargetAsync();
        var responses = new CapturedResponses();

        await using var issuer = ReferenceServiceProcess.Start(workingDirectory, ProcessArguments(target));
        var issuerAddress = await issuer.WaitUntilListeningAsync(Token);
        await CompleteInstallationAsync(target);
        var cookie = await LoginAsync(issuerAddress, responses);

        // The ring holds one ciphertext; the test corrupts exactly that stored material. A
        // process already running holds its in-process copy of the ring, so the closed boundary
        // is every reader that must fetch the row again - a process started after the corruption.
        var ringRow = Assert.Single(await ReadKeyRingAsync(target));
        const string corrupted = "bm90LXZhbGlkLWNpcGhlcnRleHQ=";
        await ExecuteAsync(target, $"""
            UPDATE public."{KeyRingTable}"
            SET encrypted_xml = '{corrupted}'
            WHERE service_id = '{ringRow.ServiceId}' AND key_id = '{ringRow.KeyId}'
            """);

        await using var fresh = ReferenceServiceProcess.Start(workingDirectory, ProcessArguments(target));
        var freshAddress = await fresh.WaitUntilListeningAsync(Token);

        var freshLogin = await TryLoginAsync(freshAddress, responses);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, freshLogin.Status);
        Assert.Contains("management.session.unavailable", freshLogin.Body, StringComparison.Ordinal);
        var freshSession = await ReadSessionAsync(freshAddress, cookie, responses);
        AssertNotAuthenticated(freshSession);

        // The corrupted row is neither repaired nor amplified, and the original material never
        // reached a body, a header, or a captured console.
        var after = Assert.Single(await ReadKeyRingAsync(target));
        Assert.Equal(corrupted, after.Ciphertext);
        responses.AssertDoesNotContain(ringRow.Ciphertext);
        AssertBoundaries(responses, issuer.Output, fresh.Output, cookies: []);
    }

    // --- 3: two processes over one ring, racing their first logins -----------------------------

    [Fact]
    public async Task Two_processes_over_one_ring_race_their_first_logins_and_share_the_result()
    {
        RequireDatabase();
        var target = await CreateTargetAsync();
        var responses = new CapturedResponses();

        await using var first = ReferenceServiceProcess.Start(workingDirectory, ProcessArguments(target));
        var firstAddress = await first.WaitUntilListeningAsync(Token);
        await CompleteInstallationAsync(target);

        // The ring row is a startup fact of the first process; the second process adopts it
        // instead of adding its own, so the two share one key before any login happens.
        var ringRow = Assert.Single(await ReadKeyRingAsync(target));
        await using var second = ReferenceServiceProcess.Start(workingDirectory, ProcessArguments(target));
        var secondAddress = await second.WaitUntilListeningAsync(Token);
        Assert.Equal([ringRow], await ReadKeyRingAsync(target));

        // Both processes race their first login with the same operator credential: both succeed
        // over the shared row, and each issued cookie is a session on both instances.
        var raced = await Task.WhenAll(
            TryLoginAsync(firstAddress, responses),
            TryLoginAsync(secondAddress, responses));
        Assert.All(raced, result => Assert.Equal(HttpStatusCode.NoContent, result.Status));

        foreach (var address in new[] { firstAddress, secondAddress })
        {
            foreach (var result in raced)
            {
                var session = await ReadSessionAsync(address, result.Cookie, responses);
                Assert.Equal(HttpStatusCode.OK, session.Status);
                Assert.True(session.Body.GetProperty("authenticated").GetBoolean());
            }
        }

        // The ring still holds exactly the one row the first process created at startup.
        Assert.Equal([ringRow], await ReadKeyRingAsync(target));
        AssertBoundaries(
            responses,
            first.Output,
            second.Output,
            cookies: raced.Select(result => result.Cookie).ToList());
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

    private static string[] ProcessArguments(string target, string? rootKey = null) =>
    [
        "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
        "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, target,
        "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
        "--" + ReferenceManagementOptions.RootKeySetting, rootKey ?? RootKey,
        "--ReferenceService:Management:Operators:0:Id", OperatorId,
        "--ReferenceService:Management:Operators:0:DisplayName", "Reference Operator",
        "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
        "--ReferenceService:Management:Operators:0:Credential", OperatorCredential,
    ];

    private Task CompleteInstallationAsync(string target) =>
        ExecuteAsync(target, $"""
            UPDATE public."{InstallationTable}" SET status = 1, completed_at_utc = now() WHERE service_id = '{ServiceIdValue}'
            """);

    private async Task<List<(string ServiceId, string KeyId, string Ciphertext)>> ReadKeyRingAsync(string target)
    {
        await using var connection = new NpgsqlConnection(target);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT service_id, key_id, encrypted_xml FROM public."{KeyRingTable}"
            """;
        var rows = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static void AssertNotAuthenticated((HttpStatusCode Status, System.Text.Json.JsonElement Body) session)
    {
        Assert.False(
            session.Status == HttpStatusCode.OK && session.Body.GetProperty("authenticated").GetBoolean(),
            "no closed or foreign-key path may report an authenticated session");
    }

    private static void AssertBoundaries(
        CapturedResponses responses,
        string firstOutput,
        string secondOutput,
        IReadOnlyList<string> cookies)
    {
        responses.AssertNoSecrets();
        foreach (var output in new[] { firstOutput, secondOutput })
        {
            Assert.DoesNotContain(OperatorCredential, output, StringComparison.Ordinal);
            Assert.DoesNotContain(RootKey, output, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, output, StringComparison.Ordinal);
        }

        // A cookie value may appear in its own Set-Cookie header - that is the transport
        // contract itself - but never in a body, in another header, or in a captured console.
        foreach (var cookie in cookies)
        {
            responses.AssertCookieStaysTransportOnly(cookie);
            Assert.DoesNotContain(cookie, firstOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(cookie, secondOutput, StringComparison.Ordinal);
        }
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

    private static async Task<string> LoginAsync(Uri address, CapturedResponses responses)
    {
        var result = await TryLoginAsync(address, responses);
        Assert.Equal(HttpStatusCode.NoContent, result.Status);
        Assert.StartsWith(
            ManagementSessionDefaults.CookieName + "=",
            result.Cookie,
            StringComparison.Ordinal);
        return result.Cookie!;
    }

    private static async Task<(HttpStatusCode Status, string Body, string Cookie)> TryLoginAsync(
        Uri address,
        CapturedResponses responses)
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
        var body = await response.Content.ReadAsStringAsync(Token);
        // A closed login answers with neither a cookie nor any other credential material.
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? Assert.Single(setCookies).Split(';', 2)[0]
            : string.Empty;
        responses.Add(response, body);
        return (response.StatusCode, body, cookie);
    }

    private static async Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)> ReadSessionAsync(
        Uri address,
        string cookie,
        CapturedResponses? responses = null)
    {
        using var client = new HttpClient { BaseAddress = address };
        using var request = new HttpRequestMessage(HttpMethod.Get, SessionPath);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        responses?.Add(response, text);
        var body = System.Text.Json.JsonDocument.Parse(
            string.IsNullOrEmpty(text) ? "{}" : text).RootElement.Clone();
        return (response.StatusCode, body);
    }

    /// <summary>
    /// Captures every response the cross-instance scenarios exchange, so the negative boundary
    /// covers the whole HTTP face - bodies and headers alike - not only the console output.
    /// </summary>
    private sealed class CapturedResponses
    {
        private readonly List<string> bodies = [];
        private readonly List<string> headers = [];

        internal void Add(HttpResponseMessage response, string body)
        {
            bodies.Add(body);
            headers.AddRange(response.Headers.SelectMany(
                header => header.Value,
                (header, value) => header.Key + ": " + value));
        }

        internal void AssertDoesNotContain(string value)
        {
            foreach (var text in bodies.Concat(headers))
            {
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            }
        }

        internal void AssertNoSecrets()
        {
            foreach (var text in bodies.Concat(headers))
            {
                Assert.DoesNotContain(OperatorCredential, text, StringComparison.Ordinal);
                Assert.DoesNotContain(RootKey, text, StringComparison.Ordinal);
                Assert.DoesNotContain(SyntheticPassword, text, StringComparison.Ordinal);
            }
        }

        internal void AssertCookieStaysTransportOnly(string cookie)
        {
            foreach (var body in bodies)
            {
                Assert.DoesNotContain(cookie, body, StringComparison.Ordinal);
            }

            foreach (var header in headers.Where(
                header => !header.StartsWith("Set-Cookie:", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain(cookie, header, StringComparison.Ordinal);
            }
        }

    }

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";
}
