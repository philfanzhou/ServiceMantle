using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Installation;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the completion and failure boundaries of the sample's PostgreSQL startup gate with
/// deterministic doubles: the finite result is published only after this call's own migration
/// scope has been released and the caller's token has been read one last time, a lease lost after
/// the executor ran is a finite lock outcome, an unreadable installation row is a finite invalid
/// outcome, and a caller cancellation at any stage stops the startup without a result.
/// </summary>
/// <remarks>
/// <para>
/// The gate is composed through the real <c>AddReferencePostgreSqlStartup</c> registration. Only
/// the preparation provider, the migration lock, the scoped executor, and the scoped installation
/// store are replaced, so no test here opens a database connection. What is asserted is the
/// checkpoint and the mapping, not the window after it; nothing here undoes a committed migration.
/// </para>
/// <para>
/// The real-database matrix - preparation, unusable targets, the installation states, concurrency,
/// and secret-free output against a live server - is owned by
/// <see cref="ReferencePostgreSqlStartupDatabaseTests"/>.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlStartupCompletionTests
{
    private const string FinalRecordPrefix = "Reference PostgreSQL startup deployment finished";

    // Synthetic secrets carried by doubles, asserted to stay out of the gate's own output.
    private const string SyntheticLeaseSecret = "synthetic-reference-lease-secret";
    private const string SyntheticStoreSecret = "synthetic-reference-store-secret";
    private const string SyntheticReleaseSecret = "synthetic-reference-release-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_result_and_no_final_record_are_published_before_the_scope_is_released()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new ControlledScopedExecutor(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            executor,
            recorder: recorder);
        var startup = container.GetRequiredService<ReferencePostgreSqlStartupHostedService>();

        var starting = startup.StartingAsync(Token);
        await entered.Task.WaitAsync(Token);

        // The work is done and the scope is still being released: nothing may be published yet.
        Assert.Null(startup.Result);
        Assert.Empty(recorder.FinalRecords);

        release.SetResult();
        await starting;

        Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, startup.Result!.Outcome);
        Assert.Equal(ServiceStartupPhase.PendingSetup, startup.Result.ServiceStartupPhase);
        Assert.True(executor.Released);
        // Exactly one final record for one startup call, carrying the finite outcome only.
        var record = Assert.Single(recorder.FinalRecords);
        Assert.Contains(ReferencePostgreSqlStartupOutcome.Ready.ToString(), record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_that_cancels_the_caller_stops_the_startup_with_the_original_token()
    {
        using var abort = new CancellationTokenSource();
        var executor = new ControlledScopedExecutor(async () => await abort.CancelAsync());
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            executor,
            recorder: recorder);
        var startup = container.GetRequiredService<ReferencePostgreSqlStartupHostedService>();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await startup.StartingAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        // This call published nothing: no Ready result, no final record, and no host start.
        Assert.Null(startup.Result);
        Assert.Empty(recorder.FinalRecords);
        Assert.True(executor.Released);
    }

    [Fact]
    public async Task An_ordinary_release_failure_is_a_safe_MigrationFailed()
    {
        var executor = new ControlledScopedExecutor(() =>
            throw new InvalidOperationException(SyntheticReleaseSecret));
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            executor,
            recorder: recorder);
        var coordinator = container.GetRequiredService<ReferencePostgreSqlStartupCoordinator>();

        var result = await coordinator.RunAsync(Token);

        // The orchestration and the installation read established Ready, but a release this call
        // owns did not finish, so nothing may be published as Ready.
        Assert.Equal(ReferencePostgreSqlStartupOutcome.MigrationFailed, result.Outcome);
        Assert.False(result.IsReady);
        Assert.Null(result.ServiceStartupPhase);
        var record = Assert.Single(recorder.FinalRecords);
        Assert.DoesNotContain(SyntheticReleaseSecret, record, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticReleaseSecret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lease_lost_after_the_executor_was_called_is_LockUnavailable()
    {
        var lease = new ControlledLock();
        var executor = new ControlledScopedExecutor(() => { })
        {
            OnExecute = () =>
            {
                lease.LoseLease();
                throw new InvalidOperationException(SyntheticLeaseSecret);
            },
        };
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            executor,
            lockProvider: new ControlledLockProvider(lease),
            recorder: recorder);
        var coordinator = container.GetRequiredService<ReferencePostgreSqlStartupCoordinator>();

        var result = await coordinator.RunAsync(Token);

        // The executor had already run when the lease was lost: the lock outcome carries that
        // fact, and committed side effects, if any, are not claimed to be undone.
        Assert.Equal(ReferencePostgreSqlStartupOutcome.LockUnavailable, result.Outcome);
        Assert.True(result.ExecutorWasCalled);
        Assert.False(result.IsReady);
        Assert.DoesNotContain(SyntheticLeaseSecret, result.ToString(), StringComparison.Ordinal);
        Assert.Single(recorder.FinalRecords);
    }

    [Fact]
    public async Task An_unsupported_migration_lock_fails_closed_as_LockUnavailable()
    {
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            recorder: recorder,
            lockRegistry: new DatabaseMigrationLockProviderRegistry(
                providers: null,
                DatabaseProviderIdResolver.Empty));
        var coordinator = container.GetRequiredService<ReferencePostgreSqlStartupCoordinator>();

        var result = await coordinator.RunAsync(Token);

        Assert.Equal(ReferencePostgreSqlStartupOutcome.LockUnavailable, result.Outcome);
        Assert.False(result.ExecutorWasCalled);
    }

    [Theory]
    [InlineData("installation.entity_invalid")]
    [InlineData("installation.state_invariant_violation")]
    [InlineData("installation.storage_error")]
    public async Task An_unreadable_installation_row_is_a_finite_InstallationStateInvalid(string code)
    {
        var store = new ControlledStore
        {
            OnFind = _ => throw new ServiceInstallationStoreException(
                code,
                "The stored installation state is invalid.",
                new InvalidOperationException(SyntheticStoreSecret)),
        };
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            store: store,
            recorder: recorder);
        var coordinator = container.GetRequiredService<ReferencePostgreSqlStartupCoordinator>();

        var result = await coordinator.RunAsync(Token);

        Assert.Equal(ReferencePostgreSqlStartupOutcome.InstallationStateInvalid, result.Outcome);
        Assert.False(result.IsReady);
        Assert.Null(result.ServiceStartupPhase);
        Assert.DoesNotContain(SyntheticStoreSecret, result.ToString(), StringComparison.Ordinal);
        var record = Assert.Single(recorder.FinalRecords);
        Assert.DoesNotContain(SyntheticStoreSecret, record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_installation_row_is_a_finite_InstallationStateMissing()
    {
        var store = new ControlledStore { OnFind = _ => ValueTask.FromResult<ServiceInstallationState?>(null) };
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            store: store);
        var coordinator = container.GetRequiredService<ReferencePostgreSqlStartupCoordinator>();

        var result = await coordinator.RunAsync(Token);

        Assert.Equal(ReferencePostgreSqlStartupOutcome.InstallationStateMissing, result.Outcome);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task A_completed_installation_row_publishes_the_completed_phase_without_execution()
    {
        var store = new ControlledStore
        {
            OnFind = serviceId => ValueTask.FromResult<ServiceInstallationState?>(
                ServiceInstallationState.CreatePending(serviceId).Complete()),
        };
        var executor = new ControlledScopedExecutor(() => { })
        {
            // A current database never reaches execution.
            InitialObservation = MigrationObservationState.CurrentVersionCompatible,
        };
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            executor,
            store: store);
        var coordinator = container.GetRequiredService<ReferencePostgreSqlStartupCoordinator>();

        var result = await coordinator.RunAsync(Token);

        Assert.Equal(ReferencePostgreSqlStartupOutcome.Ready, result.Outcome);
        Assert.Equal(ServiceStartupPhase.Completed, result.ServiceStartupPhase);
        Assert.False(result.ExecutorWasCalled);
    }

    [Fact]
    public async Task A_caller_cancellation_during_preparation_stops_the_startup_with_the_caller_token()
    {
        using var abort = new CancellationTokenSource();
        var preparation = new ControlledPreparationProvider
        {
            OnObserve = _ => ValueTask.FromResult(DatabaseTargetObservation.TargetMissing()),
            OnPrepare = _ => CancelThenThrowAsync<DatabaseTargetPreparationResult>(abort),
        };
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: true),
            preparation: preparation,
            recorder: recorder);
        var startup = container.GetRequiredService<ReferencePostgreSqlStartupHostedService>();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await startup.StartingAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Null(startup.Result);
        Assert.Empty(recorder.FinalRecords);
    }

    [Fact]
    public async Task A_caller_cancellation_during_lock_acquisition_stops_the_startup_with_the_caller_token()
    {
        using var abort = new CancellationTokenSource();
        var lockProvider = new ControlledLockProvider(null, _ => CancelThenThrowAsync<IDatabaseMigrationLock>(abort));
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            lockProvider: lockProvider,
            recorder: recorder);
        var startup = container.GetRequiredService<ReferencePostgreSqlStartupHostedService>();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await startup.StartingAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Null(startup.Result);
        Assert.Empty(recorder.FinalRecords);
    }

    [Fact]
    public async Task A_caller_cancellation_during_migration_stops_the_startup_with_the_caller_token()
    {
        using var abort = new CancellationTokenSource();
        var executor = new ControlledScopedExecutor(() => { })
        {
            OnExecute = () => CancelThenThrowAsync(abort),
        };
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            executor,
            recorder: recorder);
        var startup = container.GetRequiredService<ReferencePostgreSqlStartupHostedService>();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await startup.StartingAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Null(startup.Result);
        Assert.Empty(recorder.FinalRecords);
    }

    [Fact]
    public async Task A_caller_cancellation_during_the_installation_read_stops_the_startup_with_the_caller_token()
    {
        using var abort = new CancellationTokenSource();
        var store = new ControlledStore {         OnFind = _ => CancelThenThrowAsync<ServiceInstallationState?>(abort) };
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            store: store,
            recorder: recorder);
        var startup = container.GetRequiredService<ReferencePostgreSqlStartupHostedService>();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await startup.StartingAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Null(startup.Result);
        Assert.Empty(recorder.FinalRecords);
    }

    private static async ValueTask CancelThenThrowAsync(CancellationTokenSource source)
    {
        await source.CancelAsync();
        throw new InvalidOperationException("A synthetic failure the caller already outranks.");
    }

    private static async ValueTask<T> CancelThenThrowAsync<T>(CancellationTokenSource source)
    {
        await source.CancelAsync();
        throw new InvalidOperationException("A synthetic failure the caller already outranks.");
    }

    /// <summary>
    /// Composes the gate exactly as the sample registers it, and replaces only the parts this test
    /// drives: the preparation provider, the migration lock, the scoped executor, and the scoped
    /// installation store. No registration replaced here opens a database connection.
    /// </summary>
    private static ServiceProvider BuildContainer(
        ReferencePostgreSqlStartupOptions options,
        ControlledScopedExecutor? executor = null,
        IServiceInstallationStore? store = null,
        IDatabaseTargetPreparationProvider? preparation = null,
        IDatabaseMigrationLockProvider? lockProvider = null,
        DatabaseMigrationLockProviderRegistry? lockRegistry = null,
        RecordingLoggerProvider? recorder = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            if (recorder is not null)
            {
                builder.AddProvider(recorder);
                builder.SetMinimumLevel(LogLevel.Information);
            }
        });
        services.AddSingleton(ServiceId.Parse("reference-service"));
        services.AddReferencePostgreSqlStartup(options);
        // Every completion test replaces all four parts the gate resolves - the preparation
        // provider, the migration lock, the scoped executor, and the scoped installation store:
        // the registered real ones would open database connections, and no double here may.
        services.AddSingleton(new DatabaseTargetPreparationProviderRegistry(
            [preparation ?? new ControlledPreparationProvider()],
            DatabaseProviderIdResolver.Empty));

        if (lockRegistry is not null)
        {
            services.AddSingleton(lockRegistry);
        }
        else
        {
            // Every completion test runs under a controlled lease: the registered real advisory
            // lock provider would open a database connection, and no double here may.
            services.AddSingleton(new DatabaseMigrationLockProviderRegistry(
                [lockProvider ?? new ControlledLockProvider()],
                DatabaseProviderIdResolver.Empty));
        }

        if (executor is not null)
        {
            services.AddScoped<IDatabaseMigrationExecutor>(_ => executor);
        }
        else
        {
            services.AddScoped<IDatabaseMigrationExecutor>(_ => new ControlledScopedExecutor(() => { }));
        }

        if (store is not null)
        {
            services.AddScoped<IServiceInstallationStore>(_ => store);
        }
        else
        {
            services.AddScoped<IServiceInstallationStore>(_ => new ControlledStore());
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A scoped executor whose observations are fixed and whose release is the test's own
    /// operation. It runs no migration, so nothing here can change any real target.
    /// </summary>
    private sealed class ControlledScopedExecutor(Func<ValueTask> release)
        : IDatabaseMigrationExecutor, IAsyncDisposable
    {
        private int inspections;

        public ControlledScopedExecutor(Action release)
            : this(() =>
            {
                release();
                return ValueTask.CompletedTask;
            })
        {
        }

        public MigrationObservationState InitialObservation { get; init; } =
            MigrationObservationState.Empty;

        public Func<ValueTask>? OnExecute { get; init; }

        public bool Executed { get; private set; }

        public bool Released { get; private set; }

        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(++inspections == 1
                ? InitialObservation
                : MigrationObservationState.CurrentVersionCompatible);

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            Executed = true;
            if (OnExecute is not null)
            {
                await OnExecute();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Released = true;
            await release();
        }
    }

    /// <summary>A migration lease whose loss is the test's own deterministic operation.</summary>
    private sealed class ControlledLock : IDatabaseMigrationLock
    {
        private readonly CancellationTokenSource leaseLost = new();

        public string ProviderId => "PostgreSQL";

        public CancellationToken LeaseLost => leaseLost.Token;

        internal void LoseLease() => leaseLost.Cancel();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledLockProvider(
        ControlledLock? shared,
        Func<CancellationToken, ValueTask<IDatabaseMigrationLock>>? acquire)
        : IDatabaseMigrationLockProvider
    {
        public ControlledLockProvider(ControlledLock? shared = null)
            : this(shared, acquire: null)
        {
        }

        public string ProviderId => "PostgreSQL";

        public async ValueTask<IDatabaseMigrationLock> AcquireAsync(
            ServiceId serviceId,
            BootstrapDatabaseConfiguration bootstrap,
            TimeSpan acquireTimeout,
            CancellationToken cancellationToken = default)
        {
            if (acquire is not null)
            {
                return await acquire(cancellationToken);
            }

            return shared ?? new ControlledLock();
        }
    }

    /// <summary>A preparation provider whose observation and preparation are the test's own.</summary>
    private sealed class ControlledPreparationProvider : IDatabaseTargetPreparationProvider
    {
        public Func<CancellationToken, ValueTask<DatabaseTargetObservation>>? OnObserve { get; init; }

        public Func<CancellationToken, ValueTask<DatabaseTargetPreparationResult>>? OnPrepare { get; init; }

        public string ProviderId => "PostgreSQL";

        public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            OnObserve is null
                ? ValueTask.FromResult(DatabaseTargetObservation.TargetConnectable())
                : OnObserve(cancellationToken);

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            OnPrepare is null
                ? throw new NotSupportedException("No preparation was configured.")
                : OnPrepare(cancellationToken);
    }

    /// <summary>An installation store whose read is the test's own.</summary>
    private sealed class ControlledStore : IServiceInstallationStore
    {
        public Func<ServiceId, ValueTask<ServiceInstallationState?>>? OnFind { get; init; }

        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            OnFind is null
                ? ValueTask.FromResult<ServiceInstallationState?>(
                    ServiceInstallationState.CreatePending(serviceId))
                : OnFind(serviceId);

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gate never creates an installation row.");

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gate never completes an installation.");
    }

    /// <summary>Records what the gate wrote, so a premature success record is visible.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> lines = [];

        internal IReadOnlyList<string> FinalRecords
        {
            get
            {
                lock (lines)
                {
                    return [.. lines.Where(line =>
                        line.Contains(FinalRecordPrefix, StringComparison.Ordinal))];
                }
            }
        }

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
