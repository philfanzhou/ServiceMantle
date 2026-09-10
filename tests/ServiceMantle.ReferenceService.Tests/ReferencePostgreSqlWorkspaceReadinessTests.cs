using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Health.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the sample's read-only PostgreSQL workspace readiness contributor against a real server:
/// the base matrix that decides before any read, the finite business rejections, the cancellation
/// boundary around the context each evaluation owns, and its behaviour under the real
/// <see cref="ServiceReadinessContributorCombiner"/>.
/// </summary>
/// <remarks>
/// <para>
/// The schema is prepared with EF's own <c>MigrateAsync</c> on the already-merged context, so
/// nothing here depends on the sample's migration executor, and no workspace is seeded through any
/// other adapter. The snapshots handed to the contributor are this component's input matrix, not
/// evidence that the running sample reports them: the sample's health source and endpoint wiring are
/// separate, unfinished work.
/// </para>
/// <para>
/// Ready proves only that a base-ready snapshot was supplied and that a workspace was observed at
/// that moment. Nothing here promises the row survives the observation, bounds propagation delay, or
/// recovers from arbitrary network failure.
/// </para>
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferencePostgreSqlWorkspaceReadinessTests : IAsyncLifetime
{
    private const string WorkspaceTable = "reference_workspaces";
    private const string HistoryTable = "__EFMigrationsHistory";

    // Synthetic fixture secrets. They exist only inside this container and are asserted to stay out
    // of the contributor's own results.
    private const string SyntheticUser = "reference_health_owner";
    private const string SyntheticPassword = "synthetic-reference-health-secret";
    private const string SyntheticStrangerRole = "reference_health_stranger";
    private const string SyntheticStrangerPassword = "synthetic-reference-health-stranger-secret";

    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? container;
    private string? maintenanceConnectionString;

    /// <summary>The finite base-matrix rows that must be answered without any read.</summary>
    public static TheoryData<ServiceStartupPhase, ServiceMigrationReadinessState, ServiceDatabaseReadinessState> NotBaseReadySnapshots() =>
    [
        (ServiceStartupPhase.BootstrapConfiguration, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable),
        (ServiceStartupPhase.PendingSetup, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable),
        (ServiceStartupPhase.Completed, ServiceMigrationReadinessState.NotStarted, ServiceDatabaseReadinessState.Reachable),
        (ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Running, ServiceDatabaseReadinessState.Reachable),
        (ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Failed, ServiceDatabaseReadinessState.Reachable),
        (ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Unreachable)
    ];

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("servicemantle_reference_health_maintenance")
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

    [Theory]
    [MemberData(nameof(NotBaseReadySnapshots))]
    public async Task A_snapshot_that_is_not_base_ready_is_answered_without_any_read(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration,
        ServiceDatabaseReadinessState database)
    {
        var target = await CreateMigratedTargetAsync("base_matrix");
        var factory = new CountingContextFactory(target);
        var contributor = new ReferencePostgreSqlWorkspaceReadinessContributor(factory);

        var result = await contributor.EvaluateAsync(
            new ServiceHealthSnapshot(phase, migration, database),
            Token);

        Assert.False(result.IsReady);
        Assert.Equal(ReferencePostgreSqlWorkspaceReadinessContributor.PhaseNotReady, result.ErrorCode);
        Assert.Equal(0, factory.Creations);
        Assert.Equal(0, factory.Commands);
    }

    [Fact]
    public async Task A_base_ready_snapshot_over_an_empty_table_is_rejected()
    {
        var target = await CreateMigratedTargetAsync("empty");
        var factory = new CountingContextFactory(target);
        var contributor = new ReferencePostgreSqlWorkspaceReadinessContributor(factory);

        var result = await contributor.EvaluateAsync(BaseReady(), Token);

        Assert.False(result.IsReady);
        Assert.Equal(
            ReferencePostgreSqlWorkspaceReadinessContributor.WorkspaceMissing,
            result.ErrorCode);
        Assert.Equal(1, factory.Creations);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_base_ready_snapshot_over_an_occupied_table_is_accepted(int rows)
    {
        var target = await CreateMigratedTargetAsync($"occupied{rows}");
        await SeedWorkspacesAsync(target, rows);
        var factory = new CountingContextFactory(target);
        var contributor = new ReferencePostgreSqlWorkspaceReadinessContributor(factory);
        var historyBefore = await ReadHistoryAsync(target);
        var namesBefore = await ReadWorkspaceNamesAsync(target);

        var result = await contributor.EvaluateAsync(BaseReady(), Token);

        Assert.True(result.IsReady);
        Assert.Null(result.ErrorCode);
        // The observation changed nothing and issued no write.
        Assert.Equal(historyBefore, await ReadHistoryAsync(target));
        Assert.Equal(namesBefore, await ReadWorkspaceNamesAsync(target));
        Assert.All(factory.CommandTexts, text =>
        {
            Assert.DoesNotContain("INSERT", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CREATE", text, StringComparison.OrdinalIgnoreCase);
        });
        // No row content reached the caller, and no display name reached the result.
        Assert.DoesNotContain("workspace", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_table_a_refused_read_and_an_unreachable_server_are_the_same_refusal()
    {
        var withoutSchema = await CreateEmptyTargetAsync("no_schema");
        var migrated = await CreateMigratedTargetAsync("stranger");
        await CreateStrangerRoleAsync();
        var stranger = new NpgsqlConnectionStringBuilder(migrated)
        {
            Username = SyntheticStrangerRole,
            Password = SyntheticStrangerPassword,
        }.ConnectionString;
        var unreachable =
            "Host=127.0.0.1;Port=1;Database=servicemantle_reference_unreachable;Username=unused;Password=unused;Timeout=1";

        foreach (var connectionString in new[] { withoutSchema, stranger, unreachable })
        {
            var contributor = new ReferencePostgreSqlWorkspaceReadinessContributor(
                new CountingContextFactory(connectionString));

            var result = await contributor.EvaluateAsync(BaseReady(), Token);

            Assert.False(result.IsReady);
            Assert.Equal(
                ReferencePostgreSqlWorkspaceReadinessContributor.WorkspaceProbeFailed,
                result.ErrorCode);
            var text = result.ToString();
            Assert.DoesNotContain(SyntheticPassword, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticStrangerPassword, text, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_internal_failure_without_caller_cancellation_stays_the_fixed_refusal()
    {
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();
        var ordinary = new ReferencePostgreSqlWorkspaceReadinessContributor(
            new ThrowingContextFactory(() => new InvalidOperationException(SyntheticPassword)));
        var internalCancellation = new ReferencePostgreSqlWorkspaceReadinessContributor(
            new ThrowingContextFactory(() => new OperationCanceledException(unrelated.Token)));

        var ordinaryResult = await ordinary.EvaluateAsync(BaseReady(), Token);
        var cancellationResult = await internalCancellation.EvaluateAsync(BaseReady(), Token);

        Assert.Equal(
            ReferencePostgreSqlWorkspaceReadinessContributor.WorkspaceProbeFailed,
            ordinaryResult.ErrorCode);
        Assert.Equal(
            ReferencePostgreSqlWorkspaceReadinessContributor.WorkspaceProbeFailed,
            cancellationResult.ErrorCode);
        Assert.DoesNotContain(SyntheticPassword, ordinaryResult.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_already_cancelled_evaluation_creates_no_context()
    {
        var target = await CreateMigratedTargetAsync("pre_cancelled");
        var factory = new CountingContextFactory(target);
        var contributor = new ReferencePostgreSqlWorkspaceReadinessContributor(factory);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await contributor.EvaluateAsync(BaseReady(), abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(0, factory.Creations);
    }

    [Theory]
    [InlineData("creation")]
    [InlineData("query")]
    [InlineData("release")]
    [InlineData("completion")]
    public async Task A_cancellation_around_the_owned_context_keeps_the_original_token(string moment)
    {
        var target = await CreateMigratedTargetAsync($"cancelled_{moment}");
        await SeedWorkspacesAsync(target, 1);
        using var abort = new CancellationTokenSource();
        var factory = new CountingContextFactory(target)
        {
            CancelOnCreation = moment == "creation" ? abort : null,
            CancelOnCommand = moment == "query" ? abort : null,
            CancelOnCommandCompleted = moment == "completion" ? abort : null,
            CancelOnRelease = moment == "release" ? abort : null,
        };
        var contributor = new ReferencePostgreSqlWorkspaceReadinessContributor(factory);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await contributor.EvaluateAsync(BaseReady(), abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task Each_evaluation_creates_and_releases_its_own_context()
    {
        var target = await CreateMigratedTargetAsync("per_evaluation");
        await SeedWorkspacesAsync(target, 1);
        var factory = new CountingContextFactory(target);
        var contributor = new ReferencePostgreSqlWorkspaceReadinessContributor(factory);

        await contributor.EvaluateAsync(BaseReady(), Token);
        await contributor.EvaluateAsync(BaseReady(), Token);

        Assert.Equal(2, factory.Creations);
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(2, factory.Created.Distinct().Count());
        // Every context this contributor owned was released before its evaluation ended.
        Assert.All(factory.Created, context =>
            Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker.Entries().ToArray()));
    }

    [Fact]
    public async Task Two_overlapping_evaluations_do_not_share_a_cancellation()
    {
        var target = await CreateMigratedTargetAsync("overlapping");
        await SeedWorkspacesAsync(target, 1);
        using var abort = new CancellationTokenSource();
        using var unaffected = new CancellationTokenSource();
        var cancelledFactory = new CountingContextFactory(target) { CancelOnCommand = abort };
        var completingFactory = new CountingContextFactory(target);
        var cancelled = new ReferencePostgreSqlWorkspaceReadinessContributor(cancelledFactory);
        var completing = new ReferencePostgreSqlWorkspaceReadinessContributor(completingFactory);

        var cancelledEvaluation = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelled.EvaluateAsync(BaseReady(), abort.Token));
        var completingResult = await completing
            .EvaluateAsync(BaseReady(), unaffected.Token)
            .AsTask()
            .WaitAsync(Observation, Token);

        var failure = await cancelledEvaluation.WaitAsync(Observation, Token);
        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.True(completingResult.IsReady);
        Assert.False(unaffected.IsCancellationRequested);
    }

    [Fact]
    public async Task The_real_combiner_reports_the_contributor_s_own_success_and_refusal()
    {
        var occupied = await CreateMigratedTargetAsync("combiner_ready");
        await SeedWorkspacesAsync(occupied, 1);
        var empty = await CreateMigratedTargetAsync("combiner_missing");
        var ready = new ServiceReadinessContributorCombiner(
            [new ReferencePostgreSqlWorkspaceReadinessContributor(new CountingContextFactory(occupied))]);
        var missing = new ServiceReadinessContributorCombiner(
            [new ReferencePostgreSqlWorkspaceReadinessContributor(new CountingContextFactory(empty))]);

        var readyResult = await ready.EvaluateAsync(BaseReady(), Observation, Token);
        var missingResult = await missing.EvaluateAsync(BaseReady(), Observation, Token);

        Assert.True(readyResult.IsReady);
        Assert.False(missingResult.IsReady);
        Assert.Equal(
            ReferencePostgreSqlWorkspaceReadinessContributor.WorkspaceMissing,
            missingResult.ErrorCode);
    }

    [Fact]
    public async Task An_unfinished_probe_is_classified_by_the_combiner_s_shared_budget()
    {
        var target = await CreateMigratedTargetAsync("combiner_budget");
        await SeedWorkspacesAsync(target, 1);
        var factory = new CountingContextFactory(target) { BlockCommands = true };
        var combiner = new ServiceReadinessContributorCombiner(
            [new ReferencePostgreSqlWorkspaceReadinessContributor(factory)]);
        try
        {
            var result = await combiner
                .EvaluateAsync(BaseReady(), TimeSpan.FromMilliseconds(200), Token)
                .AsTask()
                .WaitAsync(Observation, Token);

            // The contributor builds no timeout of its own; the shared budget owns this code.
            Assert.False(result.IsReady);
            Assert.Equal(
                WellKnownServiceReadinessContributorErrorCodes.ContributorTimeout,
                result.ErrorCode);
        }
        finally
        {
            // The fixture owns releasing and awaiting the probe it blocked. The test's own wait is
            // not a production SLA.
            await factory.ReleaseAsync();
        }
    }

    [Fact]
    public async Task The_combiner_reports_caller_cancellation_over_an_unfinished_probe()
    {
        var target = await CreateMigratedTargetAsync("combiner_cancelled");
        await SeedWorkspacesAsync(target, 1);
        using var abort = new CancellationTokenSource();
        var factory = new CountingContextFactory(target) { BlockCommands = true };
        var combiner = new ServiceReadinessContributorCombiner(
            [new ReferencePostgreSqlWorkspaceReadinessContributor(factory)]);
        try
        {
            var evaluation = combiner.EvaluateAsync(BaseReady(), Observation, abort.Token).AsTask();
            await factory.WaitForBlockedCommandAsync();
            await abort.CancelAsync();

            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                evaluation.WaitAsync(Observation, Token));

            Assert.Equal(abort.Token, failure.CancellationToken);
        }
        finally
        {
            await factory.ReleaseAsync();
        }
    }

    private static ServiceHealthSnapshot BaseReady() => new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    private async Task<string> CreateEmptyTargetAsync(string name)
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql,
            maintenanceConnectionString is not null);
        var databaseName = "reference_health_" + new string([.. name.Select(character =>
            char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_')]);
        await ExecuteOnMaintenanceAsync($"DROP DATABASE IF EXISTS {databaseName}");
        await ExecuteOnMaintenanceAsync($"CREATE DATABASE {databaseName}");
        return new NpgsqlConnectionStringBuilder(maintenanceConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;
    }

    /// <summary>Prepares the schema with EF's own migration, never with the sample's executor.</summary>
    private async Task<string> CreateMigratedTargetAsync(string name)
    {
        var connectionString = await CreateEmptyTargetAsync(name);
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync(Token);
        return connectionString;
    }

    private async Task SeedWorkspacesAsync(string connectionString, int rows)
    {
        await using var context = CreateContext(connectionString);
        for (var index = 0; index < rows; index++)
        {
            context.Workspaces.Add(new ReferenceWorkspace
            {
                Id = Guid.NewGuid(),
                DisplayName = $"seeded {index}",
            });
        }

        await context.SaveChangesAsync(Token);
    }

    private static ReferencePostgreSqlDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private async Task CreateStrangerRoleAsync()
    {
        // A login role that was granted nothing on the tables themselves.
        await ExecuteOnMaintenanceAsync($"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{SyntheticStrangerRole}') THEN
                    CREATE ROLE {SyntheticStrangerRole} LOGIN PASSWORD '{SyntheticStrangerPassword}';
                END IF;
            END
            $$
            """);
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

    private Task<List<string>> ReadHistoryAsync(string connectionString) =>
        ReadStringsAsync(
            connectionString,
            $"""SELECT "MigrationId" FROM public."{HistoryTable}" ORDER BY "MigrationId" """);

    private Task<List<string>> ReadWorkspaceNamesAsync(string connectionString) =>
        ReadStringsAsync(
            connectionString,
            $"""SELECT "DisplayName" FROM public."{WorkspaceTable}" ORDER BY "DisplayName" """);

    private async Task<List<string>> ReadStringsAsync(string connectionString, string query)
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

    /// <summary>
    /// The factory each evaluation creates its context from. It counts creations and statements and
    /// can drive a cancellation at a chosen moment, or hold a statement open for the fixture.
    /// </summary>
    private sealed class CountingContextFactory(string connectionString)
        : IDbContextFactory<ReferencePostgreSqlDbContext>
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly List<string> commandTexts = [];

        private Task blockedCommand = Task.CompletedTask;

        private int creations;

        private int commands;

        internal CancellationTokenSource? CancelOnCreation { get; init; }

        internal CancellationTokenSource? CancelOnCommand { get; init; }

        internal CancellationTokenSource? CancelOnCommandCompleted { get; init; }

        internal CancellationTokenSource? CancelOnRelease { get; init; }

        internal bool BlockCommands { get; init; }

        internal int Creations => Volatile.Read(ref creations);

        internal int Commands => Volatile.Read(ref commands);

        internal List<ReferencePostgreSqlDbContext> Created { get; } = [];

        internal IReadOnlyList<string> CommandTexts
        {
            get
            {
                lock (commandTexts)
                {
                    return [.. commandTexts];
                }
            }
        }

        public ReferencePostgreSqlDbContext CreateDbContext()
        {
            Interlocked.Increment(ref creations);
            CancelOnCreation?.Cancel();
            var context = new ReferencePostgreSqlDbContext(
                new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
                    .UseNpgsql(connectionString)
                    .AddInterceptors(new ProbeInterceptor(this))
                    .Options);
            lock (Created)
            {
                Created.Add(context);
            }

            return context;
        }

        internal async Task WaitForBlockedCommandAsync() =>
            await blocked.Task.WaitAsync(Observation, Token);

        internal async Task ReleaseAsync()
        {
            release.TrySetResult();
            try
            {
                await blockedCommand.WaitAsync(Observation, Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task OnCommandAsync(string commandText, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref commands);
            lock (commandTexts)
            {
                commandTexts.Add(commandText);
            }

            CancelOnCommand?.Cancel();
            if (BlockCommands)
            {
                blocked.TrySetResult();
                blockedCommand = release.Task.WaitAsync(cancellationToken);
                await blockedCommand;
            }
        }

        private sealed class ProbeInterceptor(CountingContextFactory factory)
            : DbCommandInterceptor, IDbConnectionInterceptor
        {
            public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                await factory.OnCommandAsync(command.CommandText, cancellationToken);
                return result;
            }

            public override ValueTask<DbDataReader> ReaderExecutedAsync(
                DbCommand command,
                CommandExecutedEventData eventData,
                DbDataReader result,
                CancellationToken cancellationToken = default)
            {
                factory.CancelOnCommandCompleted?.Cancel();
                return ValueTask.FromResult(result);
            }

            public ValueTask<InterceptionResult> ConnectionClosingAsync(
                DbConnection connection,
                ConnectionEventData eventData,
                InterceptionResult result)
            {
                factory.CancelOnRelease?.Cancel();
                return ValueTask.FromResult(result);
            }
        }
    }

    /// <summary>A factory whose creation fails the way the test asked.</summary>
    private sealed class ThrowingContextFactory(Func<Exception> failure)
        : IDbContextFactory<ReferencePostgreSqlDbContext>
    {
        public ReferencePostgreSqlDbContext CreateDbContext() => throw failure();
    }
}
