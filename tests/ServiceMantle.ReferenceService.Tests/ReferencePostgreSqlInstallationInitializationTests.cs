using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Installation;
using ServiceMantle.Migration;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the atomic initialization boundary of the sample's composite PostgreSQL executor on a
/// real server: an empty target's schema, history, and initial pending installation row commit
/// together or not at all, an old workspace-only target migrates without a backfilled row, and
/// concurrent orchestrations under the real advisory lock initialise exactly one row.
/// </summary>
/// <remarks>
/// Nothing here wires the sample's host, issues Setup Codes, saves consumer business data, or
/// claims a completed schema is a completed installation. The no-database qualification and
/// delegation boundaries are owned by
/// <see cref="ReferencePostgreSqlInstallationInitializationCompletionTests"/>.
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferencePostgreSqlInstallationInitializationTests : IAsyncLifetime
{
    private const string WorkspaceMigration = "20260910000000_InitialReferencePostgreSqlWorkspace";
    private const string InstallationMigration = "20260912000000_AddReferencePostgreSqlInstallation";
    private const string DataProtectionMigration = "20260916000000_AddReferencePostgreSqlDataProtectionKeys";
    private const string HistoryTable = "__EFMigrationsHistory";
    private const string InstallationTable = "service_installations";
    private const string InitializationFailureMessage =
        "The reference service PostgreSQL installation initialization did not complete.";

    // Synthetic fixture secrets. They exist only inside this container and are asserted to stay out
    // of the executor's own diagnostics.
    private const string SyntheticUser = "reference_pg_owner";
    private const string SyntheticPassword = "synthetic-reference-owner-secret";
    private const string SyntheticStoreSecret = "synthetic-reference-store-secret";

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

    [Fact]
    public async Task An_empty_target_is_initialised_with_schema_history_and_exactly_one_pending_row()
    {
        var target = await CreateTargetAsync("init_success");
        var serviceId = ServiceId.Parse("reference-init");
        await using var context = CreateContext(target);
        var executor = CreateExecutor(context, target, serviceId);

        var initial = await executor.InspectAsync(Token);
        await executor.ExecuteAsync(Token);
        var final = await executor.InspectAsync(Token);

        Assert.Equal(MigrationObservationState.Empty, initial);
        // The schema the initialization committed is judged compatible by the schema executor the
        // composite delegates to, not by the composite's own word.
        Assert.Equal(MigrationObservationState.CurrentVersionCompatible, final);
        Assert.Equal([WorkspaceMigration, InstallationMigration, DataProtectionMigration], await ReadHistoryAsync(target));
        await using (var verify = CreateContext(target))
        {
            Assert.Empty(await verify.Database.GetPendingMigrationsAsync(Token));
        }

        var row = Assert.Single(await ReadInstallationRowsAsync(target));
        Assert.Equal(serviceId.Value, row.ServiceId);
        Assert.Equal((int)InstallationStatus.PendingSetup, row.Status);
        Assert.Null(row.SetupCodeDigest);
        Assert.Null(row.SetupCodeIssuedAtUtc);
        Assert.Null(row.SetupCodeExpiresAtUtc);
    }

    [Fact]
    public async Task A_store_failure_after_the_script_rolls_the_whole_initialization_back()
    {
        var target = await CreateTargetAsync("store_failure");
        var serviceId = ServiceId.Parse("reference-init");
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlInstallationInitializationExecutor(
            context,
            new ReferencePostgreSqlMigrationExecutor(context, target),
            new ThrowingStore(new InvalidOperationException(SyntheticStoreSecret)),
            serviceId);
        await executor.InspectAsync(Token);

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlInstallationInitializationFailedException>(
            async () => await executor.ExecuteAsync(Token));

        Assert.Equal(InitializationFailureMessage, failure.Message);
        Assert.Null(failure.InnerException);
        var text = failure.ToString();
        Assert.DoesNotContain(SyntheticStoreSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticUser, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticPassword, text, StringComparison.Ordinal);
        // Neither the script's tables and history nor the installation row survived: the target is
        // still exactly an empty schema.
        Assert.Empty(await ReadRelationNamesAsync(target));
    }

    [Fact]
    public async Task A_caller_cancellation_before_the_commit_keeps_the_caller_s_token_and_rolls_back()
    {
        var target = await CreateTargetAsync("caller_cancelled");
        var serviceId = ServiceId.Parse("reference-init");
        using var abort = new CancellationTokenSource();
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlInstallationInitializationExecutor(
            context,
            new ReferencePostgreSqlMigrationExecutor(context, target),
            new CancellingStore(abort),
            serviceId);
        await executor.InspectAsync(Token);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.IsNotType<ReferencePostgreSqlInstallationInitializationFailedException>(failure);
        Assert.Empty(await ReadRelationNamesAsync(target));
    }

    [Fact]
    public async Task An_internal_cancellation_is_a_fixed_failure_and_rolls_back()
    {
        var target = await CreateTargetAsync("internal_cancelled");
        var serviceId = ServiceId.Parse("reference-init");
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlInstallationInitializationExecutor(
            context,
            new ReferencePostgreSqlMigrationExecutor(context, target),
            new InternallyCancellingStore(unrelated),
            serviceId);
        await executor.InspectAsync(Token);

        // The caller never cancelled, so somebody else's cancellation is an ordinary failure.
        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlInstallationInitializationFailedException>(
            async () => await executor.ExecuteAsync(Token));

        Assert.Equal(InitializationFailureMessage, failure.Message);
        Assert.Empty(await ReadRelationNamesAsync(target));
    }

    [Fact]
    public async Task A_server_side_termination_before_the_commit_is_a_fixed_failure_and_rolls_back()
    {
        var target = await CreateTargetAsync("terminated");
        var serviceId = ServiceId.Parse("reference-init");
        var backend = new BackendPidInterceptor();
        await using var context = CreateContext(target, backend);
        var executor = new ReferencePostgreSqlInstallationInitializationExecutor(
            context,
            new ReferencePostgreSqlMigrationExecutor(context, target),
            new TerminatingStore(() => maintenanceConnectionString!, () => backend.BackendPid!.Value),
            serviceId);
        await executor.InspectAsync(Token);

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlInstallationInitializationFailedException>(
            async () => await executor.ExecuteAsync(Token));

        Assert.Equal(InitializationFailureMessage, failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Empty(await ReadRelationNamesAsync(target));
    }

    [Fact]
    public async Task A_cancellation_after_the_commit_is_reported_but_does_not_roll_back()
    {
        var target = await CreateTargetAsync("commit_then_cancel");
        var serviceId = ServiceId.Parse("reference-init");
        using var abort = new CancellationTokenSource();
        await using var context = CreateContext(target, new CancelOnCommitInterceptor(abort));
        var executor = CreateExecutor(context, target, serviceId);
        await executor.InspectAsync(Token);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        // A cancelled initialization is not a rolled-back one: the persisted result is exactly the
        // successful initialization's.
        Assert.Equal([WorkspaceMigration, InstallationMigration, DataProtectionMigration], await ReadHistoryAsync(target));
        var row = Assert.Single(await ReadInstallationRowsAsync(target));
        Assert.Equal(serviceId.Value, row.ServiceId);
        Assert.Equal((int)InstallationStatus.PendingSetup, row.Status);
        await using (var verify = CreateContext(target))
        {
            Assert.Equal(
                MigrationObservationState.CurrentVersionCompatible,
                await CreateExecutor(verify, target, serviceId).InspectAsync(Token));
        }
    }

    [Fact]
    public async Task A_workspace_only_target_migrates_without_writing_an_installation_row()
    {
        var target = await CreateTargetAsync("workspace_only");
        await MigrateAsync(target);
        // Roll the schema back to exactly what an older workspace-only build left behind.
        await ExecuteAsync(target, $"""DROP TABLE public."{InstallationTable}" """);
        await ExecuteAsync(target, $"""
            DELETE FROM public."{HistoryTable}" WHERE "MigrationId" = '{InstallationMigration}'
            """);
        await ExecuteAsync(target, $"""DROP TABLE public."service_data_protection_keys" """);
        await ExecuteAsync(target, $"""
            DELETE FROM public."{HistoryTable}" WHERE "MigrationId" = '{DataProtectionMigration}'
            """);
        var serviceId = ServiceId.Parse("reference-init");

        var result = await OrchestrateAsync(target, serviceId);

        Assert.True(result.Succeeded);
        Assert.True(result.ExecutorWasCalled);
        Assert.Equal([WorkspaceMigration, InstallationMigration, DataProtectionMigration], await ReadHistoryAsync(target));
        Assert.Empty(await ReadInstallationRowsAsync(target));
        await using (var verify = CreateContext(target))
        {
            Assert.Equal(
                MigrationObservationState.CurrentVersionCompatible,
                await CreateExecutor(verify, target, serviceId).InspectAsync(Token));
        }
    }

    [Fact]
    public async Task Concurrent_orchestrations_initialize_exactly_one_pending_row()
    {
        var target = await CreateTargetAsync("concurrent");
        var serviceId = ServiceId.Parse("reference-init");
        var lockProvider = new PostgreSqlMigrationLockProvider();
        var bootstrap = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "15",
            target);

        var orchestrations = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var context = CreateContext(target);
            var executor = CreateExecutor(context, target, serviceId);
            var registry = new DatabaseMigrationLockProviderRegistry(
                [lockProvider],
                DatabaseProviderIdResolver.Empty);
            var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);
            return await orchestrator.OrchestrateMigrationAsync(
                serviceId,
                bootstrap,
                TimeSpan.FromSeconds(60),
                Token);
        }, Token)).ToArray();

        var results = await Task.WhenAll(orchestrations).WaitAsync(TimeSpan.FromSeconds(120), Token);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, results.Count(result => result.ExecutorWasCalled));
        var row = Assert.Single(await ReadInstallationRowsAsync(target));
        Assert.Equal(serviceId.Value, row.ServiceId);
        Assert.Equal((int)InstallationStatus.PendingSetup, row.Status);
        Assert.Equal([WorkspaceMigration, InstallationMigration, DataProtectionMigration], await ReadHistoryAsync(target));
    }

    [Fact]
    public async Task Every_known_migration_generates_commands_without_transaction_suppression()
    {
        var target = await CreateTargetAsync("census");
        await using var context = CreateContext(target);
        var assembly = context.GetService<IMigrationsAssembly>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var inspected = 0;

        foreach (var (_, type) in assembly.Migrations)
        {
            var migration = (Microsoft.EntityFrameworkCore.Migrations.Migration)
                Activator.CreateInstance(type.AsType())!;
            var commands = generator.Generate(migration.UpOperations, migration.TargetModel);
            inspected++;
            Assert.NotEmpty(commands);
            // A future migration that requires a suppressed transaction would silently break the
            // single-transaction initialization; the census refuses it here instead.
            Assert.All(commands, command => Assert.False(command.TransactionSuppressed));
        }

        Assert.Equal(3, inspected);
    }

    private static ReferencePostgreSqlInstallationInitializationExecutor CreateExecutor(
        ReferencePostgreSqlDbContext context,
        string target,
        ServiceId serviceId) =>
        new(
            context,
            new ReferencePostgreSqlMigrationExecutor(context, target),
            new EfCoreServiceInstallationStore<ReferencePostgreSqlDbContext>(context),
            serviceId);

    private async Task<MigrationExecutionResult> OrchestrateAsync(string target, ServiceId serviceId)
    {
        await using var context = CreateContext(target);
        var executor = CreateExecutor(context, target, serviceId);
        var registry = new DatabaseMigrationLockProviderRegistry(
            [new PostgreSqlMigrationLockProvider()],
            DatabaseProviderIdResolver.Empty);
        var orchestrator = new DatabaseMigrationOrchestrator(executor, registry);
        return await orchestrator.OrchestrateMigrationAsync(
            serviceId,
            new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.PostgreSql, "15", target),
            TimeSpan.FromSeconds(60),
            Token);
    }

    private ReferencePostgreSqlDbContext CreateContext(
        string connectionString,
        IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
            .UseNpgsql(connectionString);
        if (interceptor is not null)
        {
            builder = builder.AddInterceptors(interceptor);
        }

        return new ReferencePostgreSqlDbContext(builder.Options);
    }

    private async Task MigrateAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync(Token);
    }

    private async Task<string> CreateTargetAsync(string name)
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql,
            maintenanceConnectionString is not null);
        var database = DatabaseName(name);
        await ExecuteOnMaintenanceAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        await ExecuteOnMaintenanceAsync($"""CREATE DATABASE "{database}" """);
        return Target(database);
    }

    private string Target(string database) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
            // The finite result must not depend on the provider volunteering extra error text.
            IncludeErrorDetail = false,
        }.ConnectionString;

    private static string DatabaseName(string name) => $"reference_initialization_{name}";

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

    private static async Task<List<InstallationRow>> ReadInstallationRowsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT service_id, status, setup_code_digest, setup_code_issued_at_utc, setup_code_expires_at_utc
            FROM public."{InstallationTable}"
            ORDER BY service_id
            """;
        var rows = new List<InstallationRow>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(new InstallationRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
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

    private sealed record InstallationRow(
        string ServiceId,
        int Status,
        string? SetupCodeDigest,
        DateTime? SetupCodeIssuedAtUtc,
        DateTime? SetupCodeExpiresAtUtc);

    /// <summary>Fails the installation-row write after the migration script has run.</summary>
    private sealed class ThrowingStore(Exception exception) : IServiceInstallationStore
    {
        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never finds installation state.");

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw exception;

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never completes an installation.");
    }

    /// <summary>Cancels the caller's own source, then reports that cancellation.</summary>
    private sealed class CancellingStore(CancellationTokenSource source) : IServiceInstallationStore
    {
        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never finds installation state.");

        public async ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            await source.CancelAsync();
            throw new OperationCanceledException(source.Token);
        }

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never completes an installation.");
    }

    /// <summary>Reports a cancellation the caller never asked for.</summary>
    private sealed class InternallyCancellingStore(CancellationTokenSource source)
        : IServiceInstallationStore
    {
        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never finds installation state.");

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(source.Token);

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never completes an installation.");
    }

    /// <summary>
    /// Terminates the executor's own server session from the maintenance connection, after the
    /// migration script has run.
    /// </summary>
    private sealed class TerminatingStore(
        Func<string> maintenanceConnectionString,
        Func<int> backendPid) : IServiceInstallationStore
    {
        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never finds installation state.");

        public async ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = new NpgsqlConnection(maintenanceConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_terminate_backend($1)";
            command.Parameters.AddWithValue(backendPid());
            await command.ExecuteScalarAsync(cancellationToken);
            return ServiceInstallationState.CreatePending(serviceId);
        }

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never completes an installation.");
    }

    /// <summary>Records the backend pid of the connection the executor works through.</summary>
    private sealed class BackendPidInterceptor : DbConnectionInterceptor
    {
        public int? BackendPid { get; private set; }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_backend_pid()";
            BackendPid = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
    }

    /// <summary>
    /// Cancels the caller's own source once the transaction's commit has completed, so the
    /// cancellation is observed at the executor's completion checkpoint.
    /// </summary>
    private sealed class CancelOnCommitInterceptor(CancellationTokenSource source)
        : DbTransactionInterceptor
    {
        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await source.CancelAsync();
        }
    }
}
