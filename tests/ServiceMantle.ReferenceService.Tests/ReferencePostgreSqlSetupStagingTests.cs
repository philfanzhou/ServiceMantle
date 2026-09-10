using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ServiceMantle.Installation;
using ServiceMantle.ReferenceService.Configuration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Installation.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the sample's PostgreSQL setup staging adapters on a real server: a read-only validation, a
/// single staged workspace, and the cleanup boundary the real <see cref="ServiceSetupOrchestrator"/>
/// drives around them.
/// </summary>
/// <remarks>
/// <para>
/// The schema is prepared by calling EF's own <c>MigrateAsync</c> on the already-merged context, so
/// nothing here depends on the sample's migration executor. The caller owns every save and every
/// transaction: each test does its own <c>SaveChangesAsync</c>, commit, or rollback explicitly.
/// </para>
/// <para>
/// Nothing here installs the service. Staging a workspace is not an installation that is
/// <c>Completed</c>, there is no Setup Code, no installation state, no configuration, and no audit.
/// Two explicit registrations stage two rows: this is a staging example, not a one-time install, and
/// nothing here promises idempotence, duplicate-install protection, or a single winner across
/// instances.
/// </para>
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferencePostgreSqlSetupStagingTests : IAsyncLifetime
{
    private const string WorkspaceTable = "reference_workspaces";

    // Synthetic fixture secrets. They exist only inside this container.
    private const string SyntheticUser = "reference_setup_owner";
    private const string SyntheticPassword = "synthetic-reference-setup-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? container;
    private string? maintenanceConnectionString;

    public static TheoryData<string, string> LaterContributorFailures() => new()
    {
        { "rejection", "reference.declined" },
        { "exception", WellKnownServiceSetupErrorCodes.ContributorFailed },
        { "internal-cancellation", WellKnownServiceSetupErrorCodes.ContributorFailed },
    };

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("servicemantle_reference_setup_maintenance")
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
    public async Task Validation_reads_nothing_and_registration_stages_exactly_one_workspace()
    {
        var target = await CreateMigratedTargetAsync("staging");
        var commands = new CommandCounter();
        await using var context = CreateContext(target, commands);
        var contributor = new ReferencePostgreSqlSetupContributor(context);
        var scope = new ReferencePostgreSqlSetupStagingScope(context);
        var commandsBeforeValidation = commands.Count;

        var validation = await contributor.ValidateAsync(Token);

        Assert.True(validation.Succeeded);
        Assert.False(scope.HasPendingChanges);
        Assert.Empty(context.ChangeTracker.Entries());
        // A read-only validation issues no statement of its own.
        Assert.Equal(commandsBeforeValidation, commands.Count);
        Assert.Empty(await ReadWorkspaceNamesAsync(target));

        var registration = await contributor.RegisterAsync(Token);

        Assert.True(registration.Succeeded);
        Assert.True(scope.HasPendingChanges);
        var staged = Assert.Single(context.ChangeTracker.Entries<ReferenceWorkspace>());
        Assert.Equal(EntityState.Added, staged.State);
        Assert.Equal(ReferenceSettingDefinitions.DefaultDisplayName, staged.Entity.DisplayName);
        Assert.NotEqual(Guid.Empty, staged.Entity.Id);
        // Staging runs no statement, and an independent observer still sees nothing.
        Assert.Equal(commandsBeforeValidation, commands.Count);
        Assert.Empty(await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task Only_the_caller_s_own_commit_publishes_the_staged_workspace()
    {
        var target = await CreateMigratedTargetAsync("commit");
        await using var context = CreateContext(target);
        var contributor = new ReferencePostgreSqlSetupContributor(context);

        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        await contributor.RegisterAsync(Token);
        await context.SaveChangesAsync(Token);

        // Saved but not committed: the caller's own transaction still hides it.
        Assert.Empty(await ReadWorkspaceNamesAsync(target));

        await transaction.CommitAsync(Token);

        Assert.Equal(
            [ReferenceSettingDefinitions.DefaultDisplayName],
            await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task A_caller_rollback_leaves_no_trace_and_the_scope_takes_over_no_transaction()
    {
        var target = await CreateMigratedTargetAsync("rollback");
        await using var context = CreateContext(target);
        var contributor = new ReferencePostgreSqlSetupContributor(context);
        var scope = new ReferencePostgreSqlSetupStagingScope(context);

        await using (var transaction = await context.Database.BeginTransactionAsync(Token))
        {
            await contributor.RegisterAsync(Token);
            await context.SaveChangesAsync(Token);
            // The adapter neither owns nor replaces the caller's transaction.
            Assert.Same(transaction, context.Database.CurrentTransaction);
            await scope.DiscardPendingChangesAsync(Token);
            Assert.Same(transaction, context.Database.CurrentTransaction);
            await transaction.RollbackAsync(Token);
        }

        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task An_orchestrated_setup_stages_nothing_when_a_validation_is_rejected()
    {
        var target = await CreateMigratedTargetAsync("validation_rejected");
        await using var context = CreateContext(target);
        var scope = new ReferencePostgreSqlSetupStagingScope(context);
        var orchestrator = new ServiceSetupOrchestrator(
            [
                new ReferencePostgreSqlSetupContributor(context),
                new RejectingValidationContributor(order: 200, "reference.not_allowed")
            ],
            scope);

        var result = await orchestrator.OrchestrateAsync(Token);

        Assert.False(result.Succeeded);
        Assert.Equal("reference.not_allowed", result.ErrorCode);
        // Every validation runs before any registration, so nothing was staged at all.
        Assert.False(scope.HasPendingChanges);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Empty(await ReadWorkspaceNamesAsync(target));
    }

    [Theory]
    [MemberData(nameof(LaterContributorFailures))]
    public async Task A_later_registration_failure_clears_the_uncommitted_staging(
        string behaviour,
        string expectedErrorCode)
    {
        var target = await CreateMigratedTargetAsync($"registration_{behaviour}");
        await using var context = CreateContext(target);
        var scope = new ReferencePostgreSqlSetupStagingScope(context);
        var orchestrator = new ServiceSetupOrchestrator(
            [
                new ReferencePostgreSqlSetupContributor(context),
                new FailingRegistrationContributor(order: 200, behaviour)
            ],
            scope);

        var result = await orchestrator.OrchestrateAsync(Token);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.False(scope.HasPendingChanges);
        Assert.Empty(await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task An_already_cancelled_setup_keeps_the_original_token_and_changes_nothing()
    {
        var target = await CreateMigratedTargetAsync("pre_cancelled");
        await using var context = CreateContext(target);
        var contributor = new ReferencePostgreSqlSetupContributor(context);
        var scope = new ReferencePostgreSqlSetupStagingScope(context);
        var orchestrator = new ServiceSetupOrchestrator([contributor], scope);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var orchestration = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await orchestrator.OrchestrateAsync(abort.Token));
        var validation = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await contributor.ValidateAsync(abort.Token));
        var registration = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await contributor.RegisterAsync(abort.Token));

        Assert.Equal(abort.Token, orchestration.CancellationToken);
        Assert.Equal(abort.Token, validation.CancellationToken);
        Assert.Equal(abort.Token, registration.CancellationToken);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Empty(await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task A_cancellation_during_orchestration_keeps_the_original_token_and_clears_staging()
    {
        var target = await CreateMigratedTargetAsync("cancelled_orchestration");
        await using var context = CreateContext(target);
        var scope = new ReferencePostgreSqlSetupStagingScope(context);
        using var abort = new CancellationTokenSource();
        var orchestrator = new ServiceSetupOrchestrator(
            [
                new ReferencePostgreSqlSetupContributor(context),
                new CancellingRegistrationContributor(order: 200, abort)
            ],
            scope);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await orchestrator.OrchestrateAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.False(scope.HasPendingChanges);
        Assert.Empty(await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task A_dirty_context_is_refused_and_its_existing_staged_work_is_kept()
    {
        var target = await CreateMigratedTargetAsync("dirty");
        await using var context = CreateContext(target);
        var caller = new ReferenceWorkspace { Id = Guid.NewGuid(), DisplayName = "caller staged" };
        context.Workspaces.Add(caller);
        var scope = new ReferencePostgreSqlSetupStagingScope(context);
        var orchestrator = new ServiceSetupOrchestrator(
            [new ReferencePostgreSqlSetupContributor(context)],
            scope);

        var result = await orchestrator.OrchestrateAsync(Token);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownSetupCodeErrorCodes.DirtyContext, result.ErrorCode);
        // The caller's own staged work is refused entry, never cleaned up on its behalf.
        var entry = Assert.Single(context.ChangeTracker.Entries<ReferenceWorkspace>());
        Assert.Equal(EntityState.Added, entry.State);
        Assert.Same(caller, entry.Entity);
        Assert.Empty(await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task Two_independent_contexts_do_not_affect_each_other()
    {
        var target = await CreateMigratedTargetAsync("independent");
        await using var committing = CreateContext(target);
        await using var discarding = CreateContext(target);
        var committingContributor = new ReferencePostgreSqlSetupContributor(committing);
        var discardingContributor = new ReferencePostgreSqlSetupContributor(discarding);
        var discardingScope = new ReferencePostgreSqlSetupStagingScope(discarding);

        await discardingContributor.RegisterAsync(Token);
        await using (var transaction = await committing.Database.BeginTransactionAsync(Token))
        {
            await committingContributor.RegisterAsync(Token);
            await committing.SaveChangesAsync(Token);
            await transaction.CommitAsync(Token);
        }

        await discardingScope.DiscardPendingChangesAsync(Token);

        // One context committed one row; the other discarded its own staging and added nothing.
        Assert.False(discardingScope.HasPendingChanges);
        Assert.Single(await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task Repeated_explicit_registrations_stage_two_different_workspaces()
    {
        var target = await CreateMigratedTargetAsync("repeated");
        await using var context = CreateContext(target);
        var contributor = new ReferencePostgreSqlSetupContributor(context);

        await contributor.RegisterAsync(Token);
        await contributor.RegisterAsync(Token);

        // This is a staging example, not a one-time installation: nothing here is idempotent.
        var staged = context.ChangeTracker.Entries<ReferenceWorkspace>().ToArray();
        Assert.Equal(2, staged.Length);
        Assert.All(staged, entry => Assert.Equal(EntityState.Added, entry.State));
        Assert.Equal(2, staged.Select(entry => entry.Entity.Id).Distinct().Count());
        Assert.Empty(await ReadWorkspaceNamesAsync(target));
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

    /// <summary>
    /// Creates an empty target and prepares its schema with EF's own migration, so this file never
    /// depends on the sample's migration executor.
    /// </summary>
    private async Task<string> CreateMigratedTargetAsync(string name)
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql,
            maintenanceConnectionString is not null);
        // Identifiers are unquoted here, so only letters, digits, and underscores may reach them.
        var databaseName = "reference_setup_" + new string([.. name.Select(character =>
            char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_')]);
        await ExecuteOnMaintenanceAsync($"DROP DATABASE IF EXISTS {databaseName}");
        await ExecuteOnMaintenanceAsync($"CREATE DATABASE {databaseName}");
        var connectionString = new NpgsqlConnectionStringBuilder(maintenanceConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync(Token);
        return connectionString;
    }

    private Task ExecuteOnMaintenanceAsync(string statement) =>
        ExecuteAsync(maintenanceConnectionString!, statement);

    private async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Reads the target with an independent connection this staging never touches.</summary>
    private async Task<List<string>> ReadWorkspaceNamesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""SELECT "DisplayName" FROM public."{WorkspaceTable}" ORDER BY "DisplayName" """;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    /// <summary>Counts the statements EF issues on this context.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int count;

        internal int Count => Volatile.Read(ref count);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result) => Record(result);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Record(result));

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result) => Record(result);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Record(result));

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result) => Record(result);

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Record(result));

        private T Record<T>(T result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
    }

    private sealed class RejectingValidationContributor(int order, string errorCode)
        : IServiceSetupContributor
    {
        public int Order => order;

        public ValueTask<ServiceSetupContributorResult> ValidateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ServiceSetupContributorResult.Rejected(errorCode));

        public ValueTask<ServiceSetupContributorResult> RegisterAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A rejected validation never reaches registration.");
    }

    private sealed class FailingRegistrationContributor(int order, string behaviour)
        : IServiceSetupContributor
    {
        public int Order => order;

        public ValueTask<ServiceSetupContributorResult> ValidateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ServiceSetupContributorResult.Success());

        public ValueTask<ServiceSetupContributorResult> RegisterAsync(
            CancellationToken cancellationToken = default) => behaviour switch
        {
            "rejection" => ValueTask.FromResult(
                ServiceSetupContributorResult.Rejected("reference.declined")),
            "internal-cancellation" => throw new OperationCanceledException(
                new CancellationToken(canceled: true)),
            _ => throw new InvalidOperationException("The later contributor failed."),
        };
    }

    private sealed class CancellingRegistrationContributor(
        int order,
        CancellationTokenSource abort) : IServiceSetupContributor
    {
        public int Order => order;

        public ValueTask<ServiceSetupContributorResult> ValidateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ServiceSetupContributorResult.Success());

        public async ValueTask<ServiceSetupContributorResult> RegisterAsync(
            CancellationToken cancellationToken = default)
        {
            await abort.CancelAsync();
            return ServiceSetupContributorResult.Success();
        }
    }
}
