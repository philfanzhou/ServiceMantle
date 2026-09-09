using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private static ReferenceDbContext CreateContext(ReferenceSqliteStartupOptions options) =>
        new(new DbContextOptionsBuilder<ReferenceDbContext>()
            .UseSqlite(options.TargetConnectionString)
            .Options);

    private static ServiceProvider BuildContainer(
        TemporaryDirectory directory,
        bool prepareIfMissing,
        ExecutorCounters counters)
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

        internal Barrier? Overlap { get; init; }

        internal int Inspects => Volatile.Read(ref inspects);

        internal int Executes => Volatile.Read(ref executes);

        internal int MaximumConcurrent => Volatile.Read(ref maximumConcurrent);

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
            await inner.ExecuteAsync(cancellationToken);
        }
    }
}
