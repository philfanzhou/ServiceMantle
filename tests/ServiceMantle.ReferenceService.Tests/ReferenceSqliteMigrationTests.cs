using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Sqlite;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.Sqlite;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the finite EF history states the sample's consumer-owned migration executor observes, and
/// the orchestration around it: one execution per target, a fresh inspection afterwards, caller
/// cancellation, and process-local serialization of one canonical target.
/// </summary>
public sealed class ReferenceSqliteMigrationTests
{
    private const string WorkspaceMigration = "20260903000000_InitialReferenceWorkspace";
    private const string FutureMigration = "20260904000000_FutureReferenceStep";
    private const string LaterMigration = "20260905000000_LaterReferenceStep";
    private const string HistoryTable = "__EFMigrationsHistory";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_readable_database_is_adoptable_and_the_workspace_migration_applies()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(options);
        var executor = new ReferenceSqliteMigrationExecutor(context, options);

        var initial = await executor.InspectAsync(Token);
        await executor.ExecuteAsync(Token);
        var final = await executor.InspectAsync(Token);

        Assert.Equal(MigrationObservationState.Empty, initial);
        Assert.Equal(MigrationObservationState.CurrentVersionCompatible, final);
        Assert.Equal([WorkspaceMigration], await ReadHistoryAsync(directory));
    }

    [Fact]
    public async Task A_current_database_is_reported_compatible_and_is_never_changed_by_an_inspection()
    {
        using var directory = TemporaryDirectory.Create();
        await MigrateAsync(directory);
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(options);
        var executor = new ReferenceSqliteMigrationExecutor(context, options);
        var before = await File.ReadAllBytesAsync(directory.DatabasePath, Token);

        var state = await executor.InspectAsync(Token);

        Assert.Equal(MigrationObservationState.CurrentVersionCompatible, state);
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.DatabasePath, Token));
        Assert.Empty(Directory.GetFiles(directory.Path, "reference.db-*"));
    }

    [Fact]
    public async Task A_strict_prefix_of_the_known_set_is_a_pending_migration()
    {
        using var directory = TemporaryDirectory.Create();
        await MigrateAsync(directory);
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(options);
        // A test-owned migration set, so the sample keeps exactly one production migration.
        var executor = new ReferenceSqliteMigrationExecutor(
            context,
            options,
            [WorkspaceMigration, FutureMigration]);

        Assert.Equal(MigrationObservationState.PendingMigration, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task An_unknown_history_record_is_refused_as_a_newer_version()
    {
        using var directory = TemporaryDirectory.Create();
        await MigrateAsync(directory);
        await InsertHistoryAsync(directory, FutureMigration);
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(options);
        var executor = new ReferenceSqliteMigrationExecutor(context, options);

        Assert.Equal(MigrationObservationState.VersionTooNew, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task A_gap_in_the_applied_records_is_an_inspection_failure()
    {
        using var directory = TemporaryDirectory.Create();
        await MigrateAsync(directory);
        await InsertHistoryAsync(directory, LaterMigration);
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(options);
        var executor = new ReferenceSqliteMigrationExecutor(
            context,
            options,
            [WorkspaceMigration, FutureMigration, LaterMigration]);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task Application_tables_without_a_history_are_an_inspection_failure()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(
            directory,
            """CREATE TABLE "someone_elses_table" ("Id" INTEGER)""");
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(options);
        var executor = new ReferenceSqliteMigrationExecutor(context, options);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task A_missing_workspace_table_is_an_inspection_failure()
    {
        using var directory = TemporaryDirectory.Create();
        await MigrateAsync(directory);
        await ExecuteAsync(directory, """DROP TABLE "reference_workspaces" """);
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(options);
        var executor = new ReferenceSqliteMigrationExecutor(context, options);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task An_unreadable_history_is_an_inspection_failure()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(
            directory,
            $"""CREATE TABLE "{HistoryTable}" ("Unexpected" TEXT NOT NULL)""");
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(options);
        var executor = new ReferenceSqliteMigrationExecutor(context, options);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task Caller_cancellation_keeps_the_original_token_and_starts_no_stage()
    {
        using var directory = TemporaryDirectory.Create();
        var counters = new ExecutorCounters();
        await using var container = BuildContainer(directory, prepareIfMissing: true, counters);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await container.GetRequiredService<ReferenceSqliteStartupCoordinator>().RunAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(0, counters.Inspects);
        Assert.Equal(0, counters.Executes);
        // Nothing was observed, prepared, or retried.
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Two_scopes_on_one_target_serialize_and_execute_once()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var counters = new ExecutorCounters();
        await using var container = BuildContainer(directory, prepareIfMissing: false, counters);
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();

        var results = await Task.WhenAll(
            Task.Run(async () => await coordinator.RunAsync(Token), Token),
            Task.Run(async () => await coordinator.RunAsync(Token), Token));

        Assert.All(results, result => Assert.Equal(ReferenceSqliteStartupOutcome.Ready, result.Outcome));
        Assert.Single(results, result => result.ExecutorWasCalled);
        Assert.Equal(1, counters.Executes);
        // The first call inspects, executes, and inspects again; the second call does not reuse that
        // answer - it inspects the target for itself before deciding.
        Assert.Equal(3, counters.Inspects);
        Assert.Equal(1, counters.MaximumConcurrent);
    }

    [Fact]
    public async Task Two_different_targets_do_not_block_each_other()
    {
        using var first = TemporaryDirectory.Create();
        using var second = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(first);
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(second);
        // Both executors wait on the same barrier inside their first inspection, so the pair can
        // only complete when the two targets are orchestrated at the same time.
        using var overlap = new Barrier(2);
        var firstCounters = new ExecutorCounters { Overlap = overlap };
        var secondCounters = new ExecutorCounters { Overlap = overlap };
        await using var firstContainer = BuildContainer(first, prepareIfMissing: false, firstCounters);
        await using var secondContainer = BuildContainer(second, prepareIfMissing: false, secondCounters);

        var results = await Task.WhenAll(
            Task.Run(
                async () => await firstContainer
                    .GetRequiredService<ReferenceSqliteStartupCoordinator>().RunAsync(Token),
                Token),
            Task.Run(
                async () => await secondContainer
                    .GetRequiredService<ReferenceSqliteStartupCoordinator>().RunAsync(Token),
                Token));

        Assert.All(results, result => Assert.Equal(ReferenceSqliteStartupOutcome.Ready, result.Outcome));
        Assert.All(results, result => Assert.True(result.ExecutorWasCalled));
    }

    [Fact]
    public async Task A_sibling_observation_never_overlaps_a_migration_write_window()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var counters = new ExecutorCounters
        {
            ExecuteEntered = NewGate(),
            ExecuteRelease = NewGate(),
        };
        var recording = new RecordingPreparationProvider(
            new SqliteDatabaseTargetPreparationProvider(),
            counters);
        await using var container = BuildContainer(
            directory,
            prepareIfMissing: false,
            counters,
            preparation => recording);
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();

        var first = Task.Run(async () => await coordinator.RunAsync(Token), Token);
        await counters.ExecuteEntered!.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var second = Task.Run(async () => await coordinator.RunAsync(Token), Token);
        counters.ExecuteRelease!.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(ReferenceSqliteStartupOutcome.Ready, result.Outcome));
        // The decisive assertion: the sibling's observation ran strictly after the first call's
        // migration write window closed, never inside it. The recording provider also proves the
        // first call itself never observed while its own executor was writing.
        var sequence = counters.Sequence;
        var enter = Array.IndexOf(sequence, "migration:execute:enter");
        var exit = Array.IndexOf(sequence, "migration:execute:exit");
        var siblingObserve = Array.IndexOf(sequence, "observe:sibling");
        Assert.True(enter >= 0, $"no enter record: {string.Join(", ", sequence)}");
        Assert.True(exit > enter, $"no exit after enter: {string.Join(", ", sequence)}");
        Assert.True(
            siblingObserve > exit,
            $"The sibling observation at {siblingObserve} overlapped the write window [{enter}, {exit}]: " +
            string.Join(", ", sequence));
        var firstObserve = Array.IndexOf(sequence, "observe:first");
        Assert.InRange(firstObserve, 0, enter);
    }

    [Fact]
    public async Task An_observation_that_throws_is_reported_as_TargetUnavailable()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var counters = new ExecutorCounters();
        await using var container = BuildContainer(
            directory,
            prepareIfMissing: false,
            counters,
            preparation => new ThrowingObservationProvider(new SqliteDatabaseTargetPreparationProvider()));
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();

        var result = await coordinator.RunAsync(Token);

        Assert.Equal(ReferenceSqliteStartupOutcome.TargetUnavailable, result.Outcome);
        Assert.False(result.ExecutorWasCalled);
        Assert.Equal(0, counters.Executes);
    }

    [Fact]
    public async Task An_observation_of_a_target_a_foreign_writer_holds_is_reported_as_TargetUnavailable()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var counters = new ExecutorCounters();
        await using var container = BuildContainer(directory, prepareIfMissing: false, counters);
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();

        // A writer that is not a sibling call on the coordinator's turn - the guarantee covers
        // sibling calls only, and this foreign transaction is observable through its journal.
        await using var writer = new SqliteConnection(Writable(directory));
        await writer.OpenAsync(Token);
        await using (var create = writer.CreateCommand())
        {
            create.CommandText = """CREATE TABLE "foreign_writer" ("Id" INTEGER)""";
            await create.ExecuteNonQueryAsync(Token);
        }

        await using (var transaction = writer.BeginTransaction())
        {
            await using var insert = writer.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """INSERT INTO "foreign_writer" VALUES (1)""";
            await insert.ExecuteNonQueryAsync(Token);
            Assert.Contains(
                Directory.GetFiles(directory.Path, "reference.db-*"),
                file => file.Contains("journal", StringComparison.Ordinal));

            // The observation returns a status that is neither connectable nor missing - the
            // journal of the foreign write makes the target look conflicted.
            var result = await coordinator.RunAsync(Token);

            Assert.Equal(ReferenceSqliteStartupOutcome.TargetUnavailable, result.Outcome);
            Assert.False(result.ExecutorWasCalled);
        }

        Assert.Equal(0, counters.Executes);
    }

    [Fact]
    public async Task A_caller_cancellation_while_waiting_for_the_turn_keeps_the_caller_token()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var counters = new ExecutorCounters
        {
            ExecuteEntered = NewGate(),
            ExecuteRelease = NewGate(),
        };
        await using var container = BuildContainer(directory, prepareIfMissing: false, counters);
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();
        using var abort = new CancellationTokenSource();

        var first = Task.Run(async () => await coordinator.RunAsync(Token), Token);
        await counters.ExecuteEntered!.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        // The second call is stuck waiting for the turn the first call holds when it is cancelled.
        var second = Task.Run(async () => await coordinator.RunAsync(abort.Token), Token);
        await abort.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
        Assert.Equal(abort.Token, failure.CancellationToken);

        counters.ExecuteRelease!.SetResult();
        var firstResult = await first;
        Assert.Equal(ReferenceSqliteStartupOutcome.Ready, firstResult.Outcome);
        Assert.Equal(1, counters.Executes);
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ReferenceDbContext CreateContext(ReferenceSqliteStartupOptions options) =>
        new(new DbContextOptionsBuilder<ReferenceDbContext>()
            .UseSqlite(options.TargetConnectionString)
            .Options);

    private static ServiceProvider BuildContainer(
        TemporaryDirectory directory,
        bool prepareIfMissing,
        ExecutorCounters counters,
        Func<IDatabaseTargetPreparationProvider, IDatabaseTargetPreparationProvider>? preparation = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ReferenceSqliteStartupOptions.EnabledKey] = "true",
                [ReferenceSqliteStartupOptions.DeploymentModeKey] = "SingleInstance",
                [ReferenceSqliteStartupOptions.PrepareIfMissingKey] =
                    prepareIfMissing ? "true" : "false",
                [ReferenceSqliteStartupOptions.DatabasePathKey] = directory.DatabasePath,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ServiceId.Parse("reference-service"));
        var options = services.AddReferenceSqliteStartup(configuration)!;
        services.AddDbContext<ReferenceDbContext>(builder =>
            builder.UseSqlite(options.TargetConnectionString));
        if (preparation is not null)
        {
            // The registry is built from the descriptor collection when it is first resolved, so
            // replacing the single preparation descriptor swaps what the gate observes through.
            var descriptor = services.Single(service =>
                service.ServiceType == typeof(IDatabaseTargetPreparationProvider));
            services.Remove(descriptor);
            services.AddSingleton<IDatabaseTargetPreparationProvider>(provider =>
                preparation(provider.GetRequiredService<SqliteDatabaseTargetPreparationProvider>()));
        }

        // The production executor stays in the loop; only its calls are counted.
        services.AddScoped<IDatabaseMigrationExecutor>(provider => new CountingExecutor(
            new ReferenceSqliteMigrationExecutor(
                provider.GetRequiredService<ReferenceDbContext>(),
                provider.GetRequiredService<ReferenceSqliteStartupOptions>()),
            counters));
        return services.BuildServiceProvider();
    }

    private static async Task MigrateAsync(TemporaryDirectory directory)
    {
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(options);
        await context.Database.MigrateAsync(Token);
    }

    private static Task InsertHistoryAsync(TemporaryDirectory directory, string migrationId) =>
        ExecuteAsync(
            directory,
            $"""INSERT INTO "{HistoryTable}" ("MigrationId", "ProductVersion") VALUES ('{migrationId}', '10.0.0')""");

    private static async Task ExecuteAsync(TemporaryDirectory directory, string statement)
    {
        await using var connection = new SqliteConnection(Writable(directory));
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<List<string>> ReadHistoryAsync(TemporaryDirectory directory)
    {
        await using var connection = new SqliteConnection(Writable(directory));
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""SELECT "MigrationId" FROM "{HistoryTable}" ORDER BY "MigrationId" """;
        var applied = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static string Writable(TemporaryDirectory directory) => new SqliteConnectionStringBuilder
    {
        DataSource = directory.DatabasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Cache = SqliteCacheMode.Private,
        Pooling = false,
    }.ConnectionString;

    private sealed class ExecutorCounters
    {
        private int inspects;
        private int executes;
        private int concurrent;
        private int maximumConcurrent;
        private readonly List<string> sequence = [];

        internal Barrier? Overlap { get; init; }

        internal TaskCompletionSource? ExecuteEntered { get; init; }

        internal TaskCompletionSource? ExecuteRelease { get; init; }

        internal int Inspects => Volatile.Read(ref inspects);

        internal int Executes => Volatile.Read(ref executes);

        internal int MaximumConcurrent => Volatile.Read(ref maximumConcurrent);

        internal string[] Sequence
        {
            get
            {
                lock (sequence)
                {
                    return [.. sequence];
                }
            }
        }

        internal void Record(string step)
        {
            lock (sequence)
            {
                sequence.Add(step);
            }
        }

        internal IDisposable Enter(bool execute)
        {
            Interlocked.Increment(ref execute ? ref executes : ref inspects);
            var current = Interlocked.Increment(ref concurrent);
            var observed = Volatile.Read(ref maximumConcurrent);
            while (current > observed &&
                Interlocked.CompareExchange(ref maximumConcurrent, current, observed) != observed)
            {
                observed = Volatile.Read(ref maximumConcurrent);
            }

            return new Leave(this);
        }

        private void Exit() => Interlocked.Decrement(ref concurrent);

        private sealed class Leave(ExecutorCounters owner) : IDisposable
        {
            public void Dispose() => owner.Exit();
        }
    }

    private sealed class CountingExecutor(
        IDatabaseMigrationExecutor inner,
        ExecutorCounters counters) : IDatabaseMigrationExecutor
    {
        private bool signalled;

        public async ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default)
        {
            using var entered = counters.Enter(execute: false);
            if (!signalled && counters.Overlap is { } overlap)
            {
                signalled = true;
                overlap.SignalAndWait(TimeSpan.FromSeconds(10));
            }

            return await inner.InspectAsync(cancellationToken);
        }

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            using var entered = counters.Enter(execute: true);
            if (counters.ExecuteEntered is { } gate)
            {
                counters.Record("migration:execute:enter");
                gate.TrySetResult();
                await (counters.ExecuteRelease?.Task ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            await inner.ExecuteAsync(cancellationToken);
            counters.Record("migration:execute:exit");
        }
    }

    /// <summary>
    /// Delegates everything to the real provider except the observation, which throws. The
    /// delegation of the target identity keeps the coordinator's turn keying intact, so the
    /// failure under test is the observation itself.
    /// </summary>
    private sealed class ThrowingObservationProvider(
        SqliteDatabaseTargetPreparationProvider inner) :
        IDatabaseTargetPreparationProvider,
        IDatabaseDeploymentCapabilityProvider
    {
        public string ProviderId => inner.ProviderId;

        public BootstrapDatabaseTargetKind TargetKind => inner.TargetKind;

        public DatabaseDeploymentCapability Capability => inner.Capability;

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic observation failure");

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            inner.PrepareAsync(request, timeout, cancellationToken);

        public ValueTask<string> GetCanonicalTargetIdentityAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            inner.GetCanonicalTargetIdentityAsync(target, cancellationToken);
    }

    /// <summary>
    /// Records the order of observations against the executor's recorded migration steps, so an
    /// observation that overlaps a migration write window is visible in the sequence.
    /// </summary>
    private sealed class RecordingPreparationProvider(
        SqliteDatabaseTargetPreparationProvider inner,
        ExecutorCounters counters) :
        IDatabaseTargetPreparationProvider,
        IDatabaseDeploymentCapabilityProvider
    {
        private int observations;

        public string ProviderId => inner.ProviderId;

        public BootstrapDatabaseTargetKind TargetKind => inner.TargetKind;

        public DatabaseDeploymentCapability Capability => inner.Capability;

        public async ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken)
        {
            var result = await inner.ObserveAsync(target, cancellationToken);
            counters.Record(
                Interlocked.Increment(ref observations) == 1 ? "observe:first" : "observe:sibling");
            return result;
        }

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            inner.PrepareAsync(request, timeout, cancellationToken);

        public ValueTask<string> GetCanonicalTargetIdentityAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            inner.GetCanonicalTargetIdentityAsync(target, cancellationToken);
    }
}
