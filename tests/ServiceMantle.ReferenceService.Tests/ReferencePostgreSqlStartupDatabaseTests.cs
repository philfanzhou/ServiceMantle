using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.Installation;
using ServiceMantle.ReferenceService;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Management;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the sample's opt-in PostgreSQL startup deployment gate against a real server: the
/// explicit preparation with distinct administrative and target accounts, the refusal of unusable
/// targets, the installation-state matrix after migration, concurrent hosts on one empty target,
/// and secret-free output on the real startup path.
/// </summary>
/// <remarks>
/// <para>
/// The gate is composed through the real <see cref="ReferenceApplication.CreateBuilder"/> seam on
/// a test server, exactly as the sample registers it. The deterministic completion, cancellation,
/// and lease-loss boundaries are owned by
/// <see cref="ReferencePostgreSqlStartupCompletionTests"/>; nothing here wires Setup Codes,
/// health snapshots, or management routes.
/// </para>
/// <para>
/// A startup that is not ready must fail <c>StartAsync</c> with the fixed message - the gate runs
/// in <c>StartingAsync</c> before any hosted service, so a failed startup never serves a request.
/// </para>
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferencePostgreSqlStartupDatabaseTests : IAsyncLifetime
{
    private const string WorkspaceMigration = "20260910000000_InitialReferencePostgreSqlWorkspace";
    private const string HistoryTable = "__EFMigrationsHistory";
    private const string InstallationTable = "service_installations";
    private const string StartupFailurePrefix =
        "The reference PostgreSQL startup deployment did not complete:";

    // Synthetic fixture secrets. They exist only inside this container and are asserted to stay
    // out of the gate's own output.
    private const string SyntheticPassword = "synthetic-reference-owner-secret";
    private const string RuntimeUser = "reference_runtime";
    private const string RuntimePassword = "synthetic-reference-runtime-secret";
    private const string AdminUser = "reference_gate_admin";
    private const string AdminPassword = "synthetic-reference-gate-admin-secret";
    private const string WrongPassword = "definitely-the-wrong-reference-secret";

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
            .WithDatabase("servicemantle_reference_maintenance")
            .WithUsername("reference_pg_owner")
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

    [Fact]
    public async Task A_missing_target_without_authorization_reports_TargetMissing_and_creates_nothing()
    {
        RequireDatabase();
        var database = DatabaseName("missing_unauthorized");
        await DropDatabaseAsync(database);

        var failure = await AssertStartupFailedAsync(
            Arguments(Target(database), prepareIfMissing: false),
            ReferencePostgreSqlStartupOutcome.TargetMissing);

        Assert.DoesNotContain(Target(database), failure.Message, StringComparison.Ordinal);
        Assert.False(await DatabaseExistsAsync(database));
    }

    [Fact]
    public async Task An_authorized_missing_target_is_created_and_reaches_ready_pending_setup()
    {
        RequireDatabase();
        var database = DatabaseName("authorized_creation");
        await DropDatabaseAsync(database);

        await using var app = await StartAppAsync(
            Arguments(Target(database), prepareIfMissing: true, administrative: Administrative()));

        var result = app.Services.GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!;

        Assert.True(await DatabaseExistsAsync(database));
        Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, result.Outcome);
        Assert.Equal(ServiceStartupPhase.PendingSetup, result.ServiceStartupPhase);
        Assert.True(result.ExecutorWasCalled);
        var row = Assert.Single(await ReadInstallationRowsAsync(Target(database)));
        Assert.Equal("reference-service", row.ServiceId);
        Assert.Equal((int)InstallationStatus.PendingSetup, row.Status);
    }

    [Fact]
    public async Task Every_runtime_connection_uses_the_target_account_not_the_administrative_one()
    {
        RequireDatabase();
        var database = DatabaseName("distinct_accounts");
        await DropDatabaseAsync(database);
        await CreateDistinctRolesAsync();

        // The administrative account is a separate, non-superuser role that can only create
        // databases owned by the target account. The target connection is the runtime account.
        var target = Target(database, RuntimeUser, RuntimePassword);
        await using (var app = await StartAppAsync(
            Arguments(target, prepareIfMissing: true, administrative: Administrative(AdminUser, AdminPassword))))
        {
            var result = app.Services.GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!;

            Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, result.Outcome);
            Assert.Equal(ServiceStartupPhase.PendingSetup, result.ServiceStartupPhase);
            // The preparation derived the owner from the target connection, not the administrative one.
            Assert.Equal(RuntimeUser, await ReadDatabaseOwnerAsync(database));
            Assert.Single(await ReadInstallationRowsAsync(target));
        }

        // Lock every non-owner out of the target: only the target account can still connect, so a
        // restart that reaches Ready proves the observation, the advisory lock, the inspection
        // connection, and the installation read all used the target account.
        await ExecuteOnMaintenanceAsync($"""REVOKE CONNECT ON DATABASE "{database}" FROM PUBLIC""");

        await using (var app = await StartAppAsync(Arguments(target, prepareIfMissing: false)))
        {
            var restart = app.Services.GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!;

            Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, restart.Outcome);
            Assert.Equal(ServiceStartupPhase.PendingSetup, restart.ServiceStartupPhase);
            Assert.False(restart.ExecutorWasCalled);
        }
    }

    [Fact]
    public async Task An_existing_target_with_a_wrong_password_is_TargetUnavailable_and_never_migrated()
    {
        RequireDatabase();
        var database = DatabaseName("wrong_password");
        await CreateTargetAsync(database);

        var failure = await AssertStartupFailedAsync(
            Arguments(Target(database, password: WrongPassword), prepareIfMissing: true, administrative: Administrative()),
            ReferencePostgreSqlStartupOutcome.TargetUnavailable);

        Assert.DoesNotContain(WrongPassword, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Target(database), failure.Message, StringComparison.Ordinal);
        // The target exists, was never adopted, and was never migrated.
        Assert.True(await DatabaseExistsAsync(database));
        Assert.Empty(await ReadRelationNamesAsync(Target(database)));
    }

    [Fact]
    public async Task An_unreachable_server_is_TargetUnavailable()
    {
        RequireDatabase();
        var unreachable = new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = DatabaseName("unreachable"),
            IncludeErrorDetail = false,
        }.ConnectionString;

        await AssertStartupFailedAsync(
            Arguments(unreachable, prepareIfMissing: false),
            ReferencePostgreSqlStartupOutcome.TargetUnavailable);
    }

    [Fact]
    public async Task An_empty_target_reaches_ready_pending_setup_with_exactly_one_row()
    {
        RequireDatabase();
        var database = DatabaseName("empty_current");
        await CreateTargetAsync(database);

        await using var app = await StartAppAsync(Arguments(Target(database), prepareIfMissing: false));

        var result = app.Services.GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!;

        Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, result.Outcome);
        Assert.Equal(ServiceStartupPhase.PendingSetup, result.ServiceStartupPhase);
        Assert.True(result.ExecutorWasCalled);
        var row = Assert.Single(await ReadInstallationRowsAsync(Target(database)));
        Assert.Equal((int)InstallationStatus.PendingSetup, row.Status);
    }

    [Fact]
    public async Task A_completed_installation_row_publishes_ready_completed_without_execution()
    {
        RequireDatabase();
        var database = DatabaseName("completed_row");
        await CreateTargetAsync(database);
        await using (var initialized = await StartAppAsync(
            Arguments(Target(database), prepareIfMissing: false)))
        {
            await initialized.StopAsync(Token);
        }

        await ExecuteAsync(
            Target(database),
            $"""
            UPDATE public."{InstallationTable}"
            SET status = {(int)InstallationStatus.Completed}, completed_at_utc = STATEMENT_TIMESTAMP()
            WHERE service_id = 'reference-service'
            """);

        await using var app = await StartAppAsync(Arguments(Target(database), prepareIfMissing: false));

        var result = app.Services.GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!;

        Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, result.Outcome);
        Assert.Equal(ServiceStartupPhase.Completed, result.ServiceStartupPhase);
        Assert.False(result.ExecutorWasCalled);
    }

    [Fact]
    public async Task A_current_target_without_an_installation_row_is_InstallationStateMissing()
    {
        RequireDatabase();
        var database = DatabaseName("missing_row");
        await CreateTargetAsync(database);
        await using (var initialized = await StartAppAsync(
            Arguments(Target(database), prepareIfMissing: false)))
        {
            await initialized.StopAsync(Token);
        }

        await ExecuteAsync(Target(database), $"""DELETE FROM public."{InstallationTable}" WHERE service_id = 'reference-service'""");

        await AssertStartupFailedAsync(
            Arguments(Target(database), prepareIfMissing: false),
            ReferencePostgreSqlStartupOutcome.InstallationStateMissing);
    }

    [Fact]
    public async Task A_workspace_only_target_migrates_then_reports_InstallationStateMissing()
    {
        RequireDatabase();
        var database = DatabaseName("workspace_only");
        await CreateTargetAsync(database);
        await MigrateWorkspaceOnlyAsync(Target(database));

        var failure = await AssertStartupFailedAsync(
            Arguments(Target(database), prepareIfMissing: false),
            ReferencePostgreSqlStartupOutcome.InstallationStateMissing);

        // The workspace migration itself did run; the old database was never given a row.
        Assert.Empty(await ReadInstallationRowsAsync(Target(database)));
        Assert.Contains(WorkspaceMigration, await ReadHistoryAsync(Target(database)));
    }

    [Fact]
    public async Task An_invalid_installation_status_is_InstallationStateInvalid()
    {
        RequireDatabase();
        var database = DatabaseName("invalid_status");
        await CreateTargetAsync(database);
        await using (var initialized = await StartAppAsync(
            Arguments(Target(database), prepareIfMissing: false)))
        {
            await initialized.StopAsync(Token);
        }

        await ExecuteAsync(
            Target(database),
            $"""UPDATE public."{InstallationTable}" SET status = 99 WHERE service_id = 'reference-service'""");

        await AssertStartupFailedAsync(
            Arguments(Target(database), prepareIfMissing: false),
            ReferencePostgreSqlStartupOutcome.InstallationStateInvalid);
    }

    [Fact]
    public async Task An_unknown_history_entry_is_VersionTooNew()
    {
        RequireDatabase();
        var database = DatabaseName("unknown_history");
        await CreateTargetAsync(database);
        await using (var initialized = await StartAppAsync(
            Arguments(Target(database), prepareIfMissing: false)))
        {
            await initialized.StopAsync(Token);
        }

        await ExecuteAsync(
            Target(database),
            $"""INSERT INTO public."{HistoryTable}" ("MigrationId", "ProductVersion") VALUES ('29990101000000_FromTheFuture', '10.0.11')""");

        await AssertStartupFailedAsync(
            Arguments(Target(database), prepareIfMissing: false),
            ReferencePostgreSqlStartupOutcome.VersionTooNew);
    }

    [Fact]
    public async Task A_restart_over_the_current_pending_database_is_ready_again_without_execution()
    {
        RequireDatabase();
        var database = DatabaseName("restart_pending");
        await CreateTargetAsync(database);
        await using (var initialized = await StartAppAsync(
            Arguments(Target(database), prepareIfMissing: false)))
        {
            Assert.True(initialized.Services
                .GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!.ExecutorWasCalled);
            await initialized.StopAsync(Token);
        }

        await using var restarted = await StartAppAsync(Arguments(Target(database), prepareIfMissing: false));

        var result = restarted.Services
            .GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!;

        Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, result.Outcome);
        Assert.Equal(ServiceStartupPhase.PendingSetup, result.ServiceStartupPhase);
        Assert.False(result.ExecutorWasCalled);
        // Still exactly one row for this service after two startups.
        Assert.Single(await ReadInstallationRowsAsync(Target(database)));
    }

    [Fact]
    public async Task Two_hosts_on_one_empty_target_initialize_exactly_one_row()
    {
        RequireDatabase();
        var database = DatabaseName("concurrent_hosts");
        await CreateTargetAsync(database);
        var arguments = Arguments(Target(database), prepareIfMissing: false);

        WebApplication? first = null;
        WebApplication? second = null;
        try
        {
            var startups = await Task.WhenAll(StartAppAsync(arguments), StartAppAsync(arguments));
            (first, second) = (startups[0], startups[1]);

            var firstResult = first.Services
                .GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!;
            var secondResult = second.Services
                .GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!;

            // The real advisory lock serialized them: one initialized, the other found it current.
            Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, firstResult.Outcome);
            Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, secondResult.Outcome);
            Assert.Equal(ServiceStartupPhase.PendingSetup, firstResult.ServiceStartupPhase);
            Assert.Equal(ServiceStartupPhase.PendingSetup, secondResult.ServiceStartupPhase);
            Assert.Equal(1, new[] { firstResult.ExecutorWasCalled, secondResult.ExecutorWasCalled }
                .Count(called => called));
            Assert.Single(await ReadInstallationRowsAsync(Target(database)));
        }
        finally
        {
            if (first is not null)
            {
                await first.DisposeAsync();
            }

            if (second is not null)
            {
                await second.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task The_startup_output_never_carries_a_connection_string_or_a_secret()
    {
        RequireDatabase();
        var database = DatabaseName("output_secrets");
        await DropDatabaseAsync(database);
        var recorder = new RecordingLoggerProvider();

        await using var app = await StartAppAsync(
            Arguments(Target(database), prepareIfMissing: true, administrative: Administrative()),
            recorder);

        var result = app.Services.GetRequiredService<ReferencePostgreSqlStartupHostedService>().Result!;

        Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, result.Outcome);
        Assert.NotEmpty(recorder.Lines);
        foreach (var line in recorder.Lines)
        {
            Assert.DoesNotContain(Target(database), line, StringComparison.Ordinal);
            Assert.DoesNotContain(Administrative(), line, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, line, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Username=", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Host=", line, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(Target(database), result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPassword, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_input_failure_never_creates_the_target()
    {
        RequireDatabase();
        var database = DatabaseName("input_failure");

        // The host is real, the inputs are not: the failure happens before any provider or
        // network call, so the target is never created.
        Assert.Throws<InvalidOperationException>(() => ReferenceApplication.CreateBuilder(
        [
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, Target(database),
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "perhaps",
        ]));

        Assert.False(await DatabaseExistsAsync(database));
    }

    private static async Task<InvalidOperationException> AssertStartupFailedAsync(
        string[] arguments,
        ReferencePostgreSqlStartupOutcome outcome,
        RecordingLoggerProvider? recorder = null)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAppAsync(arguments, recorder));

        // The fixed message carries the finite outcome; a host whose gate did not finish never
        // started serving requests.
        Assert.StartsWith(StartupFailurePrefix, failure.Message, StringComparison.Ordinal);
        Assert.Contains(outcome.ToString(), failure.Message, StringComparison.Ordinal);
        return failure;
    }

    private static async Task<WebApplication> StartAppAsync(
        string[] arguments,
        RecordingLoggerProvider? recorder = null)
    {
        var builder = ReferenceApplication.CreateBuilder(arguments);
        builder.WebHost.UseTestServer();
        if (recorder is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(recorder);
            builder.Logging.SetMinimumLevel(LogLevel.Information);
        }

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

    private static string[] Arguments(
        string target,
        bool prepareIfMissing,
        string? administrative = null)
    {
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, target,
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey,
            prepareIfMissing ? "true" : "false",
            // The management session rides the gate, so the root key is a required input here too.
            "--" + ReferenceManagementOptions.RootKeySetting, "synthetic-reference-management-root-key",
        };
        if (administrative is not null)
        {
            arguments.AddRange(
                ["--" + ReferencePostgreSqlStartupOptions.AdministrativeConnectionStringKey, administrative]);
        }

        return [.. arguments];
    }

    private string Target(
        string database,
        string? username = null,
        string? password = null)
    {
        // Assigning null to a builder property would remove the keyword the maintenance
        // connection already carries, so overrides are applied only when they have a value.
        var builder = new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
            IncludeErrorDetail = false,
        };
        if (username is not null)
        {
            builder.Username = username;
        }

        if (password is not null)
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private string Administrative(string username = "reference_pg_owner", string password = SyntheticPassword) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = "postgres",
            Username = username,
            Password = password,
            IncludeErrorDetail = false,
        }.ConnectionString;

    private static string DatabaseName(string name) => $"reference_gate_{name}";

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private async Task CreateTargetAsync(string database)
    {
        RequireDatabase();
        await DropDatabaseAsync(database);
        await ExecuteOnMaintenanceAsync($"""CREATE DATABASE "{database}" """);
    }

    private Task DropDatabaseAsync(string database) =>
        ExecuteOnMaintenanceAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");

    private async Task CreateDistinctRolesAsync()
    {
        // A previous interrupted run in this container may have left the roles behind; membership
        // has to be revoked before a role can be dropped.
        await ExecuteOnMaintenanceAsync("""
            DO $do$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'reference_gate_admin') THEN
                    EXECUTE 'REVOKE "reference_runtime" FROM "reference_gate_admin"';
                END IF;
            END
            $do$
            """);
        await ExecuteOnMaintenanceAsync(
            """
            DROP ROLE IF EXISTS "reference_gate_admin"
            """);
        await ExecuteOnMaintenanceAsync(
            """
            DROP ROLE IF EXISTS "reference_runtime"
            """);
        await ExecuteOnMaintenanceAsync(
            $"""CREATE ROLE "reference_runtime" LOGIN PASSWORD '{RuntimePassword}'""");
        // A non-superuser administrative role: it can create databases, but only ones owned by the
        // target account it is a member of - the least privilege the preparation needs.
        await ExecuteOnMaintenanceAsync(
            $"""CREATE ROLE "reference_gate_admin" LOGIN PASSWORD '{AdminPassword}' CREATEDB""");
        await ExecuteOnMaintenanceAsync(
            """
            GRANT "reference_runtime" TO "reference_gate_admin"
            """);
    }

    private async Task<bool> DatabaseExistsAsync(string database) =>
        await ExecuteScalarAsync(
            maintenanceConnectionString!,
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1)",
            database) is true;

    private async Task<string?> ReadDatabaseOwnerAsync(string database) =>
        (await ExecuteScalarAsync(
            maintenanceConnectionString!,
            "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = $1",
            database))?.ToString();

    private static async Task<object?> ExecuteScalarAsync(
        string connectionString,
        string statement,
        string parameter)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        command.Parameters.AddWithValue(parameter);
        return await command.ExecuteScalarAsync(Token);
    }

    private async Task MigrateWorkspaceOnlyAsync(string target)
    {
        var options = new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
            .UseNpgsql(target)
            .Options;
        await using var context = new ReferencePostgreSqlDbContext(options);
        await context.Database.MigrateAsync(WorkspaceMigration, Token);
    }

    private Task ExecuteOnMaintenanceAsync(string statement) =>
        ExecuteAsync(maintenanceConnectionString!, statement);

    private static async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<List<string>> ReadHistoryAsync(string connectionString) =>
        await ReadStringsAsync(
            connectionString,
            $"""SELECT "MigrationId" FROM public."{HistoryTable}" ORDER BY "MigrationId" """);

    private static async Task<List<string>> ReadRelationNamesAsync(string connectionString) =>
        await ReadStringsAsync(
            connectionString,
            """
            SELECT class.relname
            FROM pg_catalog.pg_class AS class
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = class.relnamespace
            WHERE namespace.nspname = 'public'
            ORDER BY class.relname
            """);

    private static async Task<List<(string ServiceId, int Status)>> ReadInstallationRowsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT service_id, status
            FROM public."{InstallationTable}"
            ORDER BY service_id
            """;
        var rows = new List<(string, int)>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return rows;
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

    /// <summary>Records every line the host writes during the startup under test.</summary>
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
                    lines.Add(formatter(state, exception) + " " + exception);
                }
            }
        }
    }
}
