using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Installation.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Drives the sample's one-shot Setup installation against a real PostgreSQL server: startup
/// issuance and its console boundary, restart and rotation behaviour, the full HTTP completion
/// with its single-transaction guarantees, refusal and failure matrices, cancellation before and
/// after the commit, the one-winner concurrency row, the sensitive-value boundary, and the
/// gate-off path with neither routes nor issuer.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferenceSetupTests : IAsyncLifetime
{
    private const string SetupPath = "/management/v1/setup";
    private const string InstallationTable = "service_installations";
    private const string SettingsTable = "service_settings";
    private const string AuditTable = "service_audit_logs";
    private const string WorkspacesTable = "reference_workspaces";
    private const string UnsafeRequestHeader = "X-ServiceMantle-Request";

    private const string AdminId = "ops-admin";
    private const string AdminCredential = "synthetic-setup-admin-secret";

    // Synthetic fixture secrets, asserted to stay out of every response, log line, and audit row.
    private const string SyntheticUser = "reference_setup_owner";
    private const string SyntheticPassword = "synthetic-reference-setupq-secret";
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
            .WithDatabase("reference_setup_maintenance")
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

    // --- C1 + C2: issuance, its boundary, and the restart that does not rotate ----------------

    [Fact]
    public async Task C1_AFirstStartIssuesExactlyOneBannerAndStoresOnlyADigest()
    {
        var database = await CreateTargetAsync("first-issue");
        var output = new RecordingOutput();
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAppAsync(database, output, logs);

        var code = ExtractCode(output.OutLines);
        Assert.Equal(32, code.Length);
        Assert.Contains(
            output.OutLines,
            line => line.StartsWith("expires at ", StringComparison.Ordinal));
        Assert.Contains(ReferenceSetupCodeIssuer.RotationHint, output.OutLines);
        Assert.Single(output.OutLines, line => line == "one-time setup code:");

        var digest = Assert.Single(await ReadStringsAsync(
            Target(database),
            $"SELECT setup_code_digest FROM {InstallationTable}"));
        Assert.NotEqual(code, digest);
        Assert.DoesNotContain(code, string.Join('\n', logs.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task C2_ARestartWhilePendingPrintsTheFixedHintAndKeepsTheFirstCode()
    {
        var database = await CreateTargetAsync("restart");
        var first = new RecordingOutput();
        await using var app = await StartAppAsync(database, first);
        var code = ExtractCode(first.Text);

        var second = new RecordingOutput();
        await using var restarted = await StartAppAsync(database, second);

        Assert.Contains(ReferenceSetupCodeIssuer.AlreadyIssuedHint, second.OutLines);
        Assert.DoesNotContain("one-time setup code:", string.Join('\n', second.OutLines), StringComparison.Ordinal);
        Assert.DoesNotContain(code, string.Join('\n', second.OutLines), StringComparison.Ordinal);

        using var completion = await PostSetupAsync(restarted.GetTestClient(), code);
        Assert.Equal(HttpStatusCode.NoContent, completion.StatusCode);
    }

    // --- C4: the success path ----------------------------------------------------------------

    [Fact]
    public async Task C4_ACorrectCodeCommitsEverythingOnceAndReadyTurnsHealthyWithoutRestart()
    {
        var database = await CreateTargetAsync("success");
        var output = new RecordingOutput();
        await using var app = await StartAppAsync(database, output);
        var client = app.GetTestClient();
        var code = ExtractCode(output.Text);
        var issuedGeneration = Assert.Single(await ReadStringsAsync(
            Target(database),
            $"SELECT version::text FROM {InstallationTable}"));

        using var pending = await client.GetAsync(SetupPath, Token);
        Assert.Equal("""{"status":"pending"}""", await pending.Content.ReadAsStringAsync(Token));
        using var notReady = await client.GetAsync("/health/ready", Token);
        Assert.NotEqual(HttpStatusCode.OK, notReady.StatusCode);

        using var completion = await PostSetupAsync(client, code);
        Assert.Equal(HttpStatusCode.NoContent, completion.StatusCode);
        Assert.Equal(string.Empty, await completion.Content.ReadAsStringAsync(Token));

        var installation = Assert.Single(await ReadStringsAsync(Target(database), $"""
            SELECT status::text || '|' || coalesce(completed_at_utc::text, '') || '|' ||
                   coalesce(setup_code_digest, '') || '|' || coalesce(setup_code_issued_at_utc::text, '') || '|' ||
                   coalesce(setup_code_expires_at_utc::text, '') || '|' || version::text
            FROM {InstallationTable}
            """));
        var parts = installation.Split('|');
        Assert.Equal("1", parts[0]);
        Assert.NotEqual("", parts[1]);
        Assert.Equal("", parts[2]);
        Assert.Equal("", parts[3]);
        Assert.Equal("", parts[4]);
        Assert.Equal(int.Parse(issuedGeneration, CultureInfo.InvariantCulture) + 1, int.Parse(parts[5], CultureInfo.InvariantCulture));

        Assert.Equal(
            ["1"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {WorkspacesTable}"));
        var audit = Assert.Single(await ReadStringsAsync(Target(database), $"""
            SELECT action || '|' || coalesce(operator_id, '') || '|' || operator_source || '|' ||
                   target_type || '|' || target_id || '|' || outcome::text || '|' ||
                   coalesce(client_ip, '') || '|' || coalesce(security_description, '') || '|' ||
                   coalesce(metadata_json, '')
            FROM {AuditTable}
            """));
        Assert.Equal("installation.completed||system|service|reference-service|1|||", audit);
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {SettingsTable}"));

        using var ready = await client.GetAsync("/health/ready", Token);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var completed = await client.GetAsync(SetupPath, Token);
        Assert.Equal("""{"status":"completed"}""", await completed.Content.ReadAsStringAsync(Token));

        using var replay = await PostSetupRawAsync(client, "not json at all");
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        // The configured operator can log in once the installation is completed.
        using var login = await client.SendAsync(LoginRequest(), Token);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    // --- C5: refused codes -------------------------------------------------------------------

    [Fact]
    public async Task C5_WrongExpiredAndRotatedAwayCodesAnswer401AndChangeNothing()
    {
        var database = await CreateTargetAsync("refusals");
        var output = new RecordingOutput();
        await using var app = await StartAppAsync(database, output);
        var client = app.GetTestClient();
        var code = ExtractCode(output.Text);

        using var wrong = await PostSetupAsync(client, "ThisIsNotTheIssuedSetupCode00000");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(
            """{"errorCode":"management.setup.credential_invalid"}""",
            await wrong.Content.ReadAsStringAsync(Token));

        // The expiry is placed a fixed, short distance after the issuance timestamp - the same
        // clock that wrote it - so the material reads as expired, never corrupt, once the wait
        // below has passed.
        await ExecuteAsync(Target(database), $"""
            UPDATE {InstallationTable}
            SET setup_code_expires_at_utc = setup_code_issued_at_utc + interval '100 milliseconds'
            WHERE service_id = 'reference-service'
            """);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Token);
        using var expired = await PostSetupAsync(client, code);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        // Rotating the still-valid code away: the rotation command prints the new code, the old
        // one is refused, and nothing else changes.
        var rotated = await RotateInProcessAsync(database);
        var newCode = ExtractCode(rotated);
        Assert.NotEqual(code, newCode);
        using var rotatedAway = await PostSetupAsync(client, code);
        Assert.Equal(HttpStatusCode.Unauthorized, rotatedAway.StatusCode);

        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {WorkspacesTable}"));
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {AuditTable}"));
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT status::text FROM {InstallationTable}"));

        using var fresh = await PostSetupAsync(client, newCode);
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
    }

    // --- C6: mid-transaction failures roll everything back -----------------------------------

    [Theory]
    [InlineData("audit")]
    [InlineData("workspace")]
    [InlineData("commit")]
    public async Task C6_AnAuditWorkspaceOrCommitFailureLeavesNoPartialStateAndKeepsTheCode(string failure)
    {
        var database = await CreateTargetAsync("failure-" + failure);
        var output = new RecordingOutput();
        await using var app = await StartAppAsync(database, output);
        var client = app.GetTestClient();
        var code = ExtractCode(output.Text);
        await CreateFailureTriggerAsync(Target(database), failure);

        using var response = await PostSetupAsync(client, code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {WorkspacesTable}"));
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {AuditTable}"));
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT status::text FROM {InstallationTable}"));

        await DropFailureTriggerAsync(Target(database), failure);
        using var recovery = await PostSetupAsync(client, code);
        Assert.Equal(HttpStatusCode.NoContent, recovery.StatusCode);
        Assert.Equal(
            ["1"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {WorkspacesTable}"));
    }

    // --- C7: caller cancellation before and after the commit ---------------------------------

    [Fact]
    public async Task C7a_CancellationBeforeTheCommitLeavesNothingBehind()
    {
        var database = await CreateTargetAsync("cancel-before");
        var output = new RecordingOutput();
        await using var app = await StartAppAsync(database, output);
        var client = app.GetTestClient();
        var code = ExtractCode(output.Text);
        await ExecuteAsync(Target(database), $"""
            CREATE OR REPLACE FUNCTION setup_tests_sleep_row() RETURNS trigger AS $$
            BEGIN
                PERFORM pg_sleep(20);
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER setup_tests_cancel BEFORE INSERT ON {WorkspacesTable}
                FOR EACH ROW EXECUTE FUNCTION setup_tests_sleep_row();
            """);

        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PostSetupAsync(client, code, abort.Token));

        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {WorkspacesTable}"));
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {AuditTable}"));
        Assert.Equal(
            ["0"],
            await ReadStringsAsync(Target(database), $"SELECT status::text FROM {InstallationTable}"));
        await ExecuteAsync(Target(database), "DROP TRIGGER setup_tests_cancel ON " + WorkspacesTable);
    }

    [Fact]
    public async Task C7b_CancellationAfterTheCommitStartedStillCommitsTheInstallation()
    {
        var database = await CreateTargetAsync("cancel-after");
        var output = new RecordingOutput();
        await using var app = await StartAppAsync(database, output);
        var client = app.GetTestClient();
        var code = ExtractCode(output.Text);
        await ExecuteAsync(Target(database), $"""
            CREATE OR REPLACE FUNCTION setup_tests_sleep_commit() RETURNS trigger AS $$
            BEGIN
                PERFORM pg_sleep(8);
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE CONSTRAINT TRIGGER setup_tests_commit AFTER INSERT ON {WorkspacesTable}
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION setup_tests_sleep_commit();
            """);

        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PostSetupAsync(client, code, abort.Token));

        // The commit itself runs on CancellationToken.None, so the data settles even though the
        // caller is long gone; the status read is the authority.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string status;
        do
        {
            status = Assert.Single(await ReadStringsAsync(
                Target(database),
                $"SELECT status::text FROM {InstallationTable}"));
            if (status == "1")
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "the deferred commit did not settle in time");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        }
        while (true);

        Assert.Equal(
            ["1"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {WorkspacesTable}"));
        Assert.Equal(
            ["1"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {AuditTable}"));
        using var completed = await client.GetAsync(SetupPath, Token);
        Assert.Equal("""{"status":"completed"}""", await completed.Content.ReadAsStringAsync(Token));
    }

    // --- C8: two concurrent completions produce one winner -----------------------------------

    [Fact]
    public async Task C8_TwoOverlappingCorrectCodesCommitExactlyOneInstallation()
    {
        var database = await CreateTargetAsync("race");
        var output = new RecordingOutput();
        await using var app = await StartAppAsync(database, output);
        var client = app.GetTestClient();
        var code = ExtractCode(output.Text);
        // The insert marker is a sequence, so both sessions see each other's arrival even before
        // either commits: the first to arrive waits for the second before it saves on.
        await ExecuteAsync(Target(database), $"""
            CREATE SEQUENCE IF NOT EXISTS setup_tests_arrivals;
            CREATE OR REPLACE FUNCTION setup_tests_sync_gate() RETURNS trigger AS $$
            DECLARE n bigint;
            BEGIN
                n := nextval('setup_tests_arrivals');
                WHILE (SELECT last_value FROM setup_tests_arrivals) < 2 LOOP
                    PERFORM pg_sleep(0.05);
                END LOOP;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER setup_tests_sync BEFORE INSERT ON {WorkspacesTable}
                FOR EACH ROW EXECUTE FUNCTION setup_tests_sync_gate();
            """);

        var first = PostSetupAsync(client, code);
        var second = PostSetupAsync(client, code);
        using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(60), Token);
        using var secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(60), Token);
        var statuses = new[] { firstResponse.StatusCode, secondResponse.StatusCode };

        Assert.Single(statuses, status => status == HttpStatusCode.NoContent);
        Assert.Single(statuses, status => status == HttpStatusCode.Conflict);
        Assert.Equal(
            ["1"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {WorkspacesTable}"));
        Assert.Equal(
            ["1"],
            await ReadStringsAsync(Target(database), $"SELECT count(*)::text FROM {AuditTable}"));
        Assert.Equal(
            ["1"],
            await ReadStringsAsync(Target(database), $"SELECT status::text FROM {InstallationTable}"));
    }

    // --- C9: the sensitive-value boundary -----------------------------------------------------

    [Fact]
    public async Task C9_SecretsReachNeitherResponsesLogsNorAuditRows()
    {
        var database = await CreateTargetAsync("secrets");
        var output = new RecordingOutput();
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAppAsync(database, output, logs);
        var client = app.GetTestClient();
        var code = ExtractCode(output.Text);
        var responses = new List<string>();

        using var wrong = await PostSetupAsync(client, "ThisIsNotTheIssuedSetupCode00000");
        responses.Add(await wrong.Content.ReadAsStringAsync(Token));
        using var completion = await PostSetupAsync(client, code);
        responses.Add(await completion.Content.ReadAsStringAsync(Token));
        using var replay = await PostSetupAsync(client, code);
        responses.Add(await replay.Content.ReadAsStringAsync(Token));
        using var login = await client.SendAsync(LoginRequest(), Token);
        responses.Add(await login.Content.ReadAsStringAsync(Token));

        var audit = string.Join('\n', await ReadStringsAsync(
            Target(database),
            $"SELECT * FROM {AuditTable}"));
        foreach (var secret in new[] { code, RootKey, AdminCredential, SyntheticPassword })
        {
            foreach (var response in responses)
            {
                Assert.DoesNotContain(secret, response, StringComparison.Ordinal);
            }

            foreach (var message in logs.Messages)
            {
                Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        }
    }

    // --- C10: the gate-off path has no Setup surface at all -----------------------------------

    [Fact]
    public async Task C10_WithoutTheGateThereAreNoSetupRoutesAndNoIssuer()
    {
        var builder = ReferenceApplication.CreateBuilder(["--environment", "Production"]);
        builder.WebHost.UseTestServer();
        var app = ReferenceApplication.Build(builder);
        await using (app)
        {
            await app.StartAsync(Token);

            using var response = await app.GetTestClient().GetAsync(SetupPath, Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Null(app.Services.GetService<ReferenceSetupCodeIssuer>());
            Assert.Null(app.Services.GetService<ReferenceSetupCodeOutput>());
        }
    }

    // --- C3: the rotation command, process level ----------------------------------------------

    [Fact]
    public async Task C3_TheRotationCommandRotatesRefusesAndFailsClosedByExitCode()
    {
        RequireDatabase();
        var database = await CreateTargetAsync("rotation");
        var target = Target(database);
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"reference-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var arguments = BaseArguments(target);

            string firstCode;
            await using (var running = ReferenceServiceProcess.Start(workingDirectory, [.. arguments]))
            {
                await running.WaitUntilListeningAsync(Token);
                firstCode = await WaitForCodeAsync(running);
                await running.ShutDownAsync(Token);
            }

            // Pending: the command prints a new code and exits zero.
            var rotation = ReferenceServiceProcess.Start(
                workingDirectory, [.. arguments, "--rotate-setup-code"]);
            var exitCode = await rotation.WaitForExitAsync(Token);
            Assert.Equal(0, exitCode);
            var rotatedCode = ExtractCode(rotation.Output);
            Assert.NotEqual(firstCode, rotatedCode);

            // The old code is refused, the rotated one completes the installation.
            await using (var running = ReferenceServiceProcess.Start(workingDirectory, [.. arguments]))
            {
                var address = await running.WaitUntilListeningAsync(Token);
                using var client = new HttpClient { BaseAddress = address };
                using var refused = await PostSetupAsync(client, firstCode);
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
                using var accepted = await PostSetupAsync(client, rotatedCode);
                Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
                await running.ShutDownAsync(Token);
            }

            // Completed: the command exits one and prints no plaintext banner.
            rotation = ReferenceServiceProcess.Start(
                workingDirectory, [.. arguments, "--rotate-setup-code"]);
            var completedExit = await rotation.WaitForExitAsync(Token);
            Assert.Equal(1, completedExit);
            Assert.DoesNotContain("one-time setup code:", rotation.Output, StringComparison.Ordinal);

            // Without the gate the command exits two: there is no installation to rotate.
            rotation = ReferenceServiceProcess.Start(
                workingDirectory, [.. BaseArguments(target, gate: false), "--rotate-setup-code"]);
            var gateOffExit = await rotation.WaitForExitAsync(Token);
            Assert.Equal(2, gateOffExit);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    // --- helpers --------------------------------------------------------------------------------

    private static string ExtractCode(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return ExtractCode(lines);
    }

    private static string ExtractCode(IReadOnlyList<string> lines)
    {
        var index = Array.IndexOf([.. lines], "one-time setup code:");
        Assert.True(index >= 0, "no setup code banner was printed: " + string.Join('\n', lines));
        return lines[index + 1];
    }

    private static async Task<string> WaitForCodeAsync(ReferenceServiceProcess process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var lines = process.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Contains("one-time setup code:"))
            {
                return ExtractCode(lines);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
        }

        throw new InvalidOperationException("the process did not print a setup code in time");
    }

    private async Task<WebApplication> StartAppAsync(
        string database,
        RecordingOutput? output = null,
        ILoggerProvider? recorder = null)
    {
        var builder = ReferenceApplication.CreateBuilder([.. BaseArguments(Target(database))]);
        builder.WebHost.UseTestServer();
        if (output is not null)
        {
            builder.Services.AddSingleton<ReferenceSetupCodeOutput>(output.Output);
        }

        if (recorder is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(recorder);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
        }

        var app = ReferenceApplication.Build(builder);
        await app.StartAsync(Token);
        return app;
    }

    private static List<string> BaseArguments(string target, bool gate = true)
    {
        var arguments = new List<string>();
        if (gate)
        {
            arguments.Add("--" + ReferencePostgreSqlStartupOptions.EnabledKey);
            arguments.Add("true");
        }

        arguments.AddRange(
        [
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, target,
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
            "--environment", "Production",
            "--ReferenceService:Management:RootKey", RootKey,
            "--ReferenceService:Management:Operators:0:Id", AdminId,
            "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
            "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
            "--ReferenceService:Management:Operators:0:Credential", AdminCredential,
        ]);
        return arguments;
    }

    /// <summary>Runs the rotation command in-process over the same configuration shape.</summary>
    private async Task<string> RotateInProcessAsync(string database)
    {
        var target = Target(database);
        var configuration = new ConfigurationBuilder()
            .AddCommandLine([.. BaseArguments(target)])
            .Build();
        var output = new RecordingOutput();
        var exitCode = await ReferenceSetupCodeRotation.RunAsync(configuration, output.Output);
        Assert.Equal(0, exitCode);
        return output.Text;
    }

    private static Task<HttpResponseMessage> PostSetupAsync(
        HttpClient client,
        string code,
        CancellationToken? cancellationToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SetupPath)
        {
            Content = new StringContent(
                "{\"code\":\"" + code + "\"}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        return client.SendAsync(request, cancellationToken ?? Token);
    }

    private static Task<HttpResponseMessage> PostSetupRawAsync(HttpClient client, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SetupPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        return client.SendAsync(request, Token);
    }

    private static HttpRequestMessage LoginRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/management/v1/session/login")
        {
            Content = new StringContent(
                $$"""{"username":"{{AdminId}}","secret":"{{AdminCredential}}"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(UnsafeRequestHeader, "1");
        return request;
    }

    private static async Task CreateFailureTriggerAsync(string target, string failure) =>
        await ExecuteAsync(target, failure switch
        {
            "audit" => $"""
                CREATE OR REPLACE FUNCTION setup_tests_fail() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'synthetic audit failure';
                END $$ LANGUAGE plpgsql;
                CREATE TRIGGER setup_tests_failure BEFORE INSERT ON {AuditTable}
                    FOR EACH ROW EXECUTE FUNCTION setup_tests_fail();
                """,
            "workspace" => $"""
                CREATE OR REPLACE FUNCTION setup_tests_fail() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'synthetic workspace failure';
                END $$ LANGUAGE plpgsql;
                CREATE TRIGGER setup_tests_failure BEFORE INSERT ON {WorkspacesTable}
                    FOR EACH ROW EXECUTE FUNCTION setup_tests_fail();
                """,
            _ => $"""
                CREATE OR REPLACE FUNCTION setup_tests_fail_commit() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'synthetic deferred commit failure';
                END $$ LANGUAGE plpgsql;
                CREATE CONSTRAINT TRIGGER setup_tests_failure AFTER INSERT ON {WorkspacesTable}
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION setup_tests_fail_commit();
                """,
        });

    private static Task DropFailureTriggerAsync(string target, string failure) =>
        ExecuteAsync(target, failure switch
        {
            "audit" => $"DROP TRIGGER setup_tests_failure ON {AuditTable}",
            "workspace" => $"DROP TRIGGER setup_tests_failure ON {WorkspacesTable}",
            _ => $"DROP TRIGGER setup_tests_failure ON {WorkspacesTable}",
        });

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

    private static string DatabaseName(string name) => $"reference_setup_{name}";

    private string Target(string database) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
        }.ConnectionString;

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

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    /// <summary>The two console streams the issuer writes to, captured separately.</summary>
    private sealed class RecordingOutput
    {
        private readonly List<string> @out = [];
        private readonly List<string> error = [];

        internal RecordingOutput()
        {
            Output = new ReferenceSetupCodeOutput(new Recorder(@out), new Recorder(error));
        }

        internal ReferenceSetupCodeOutput Output { get; }

        internal IReadOnlyList<string> OutLines
        {
            get
            {
                lock (@out)
                {
                    return [.. @out];
                }
            }
        }

        internal string Text
        {
            get
            {
                lock (@out)
                {
                    return string.Join('\n', @out);
                }
            }
        }

        private sealed class Recorder(List<string> lines) : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;

            public override void WriteLine(string? value)
            {
                lock (lines)
                {
                    lines.Add(value ?? string.Empty);
                }
            }
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
