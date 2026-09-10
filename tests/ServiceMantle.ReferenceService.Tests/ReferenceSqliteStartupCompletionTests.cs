using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.Sqlite;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the completion boundary of the sample's SQLite startup gate: a finite result is published
/// and recorded only after this call's own migration scope has been released and the caller's token
/// has been read one last time.
/// </summary>
/// <remarks>
/// <para>
/// The gate is composed through the real <c>AddReferenceSqliteStartup</c> registration and runs the
/// real preparation provider against a real file. Only the scoped <see cref="IDatabaseMigrationExecutor"/>
/// is replaced, by an adapter that reports a compatible schema - so no migration runs - and whose
/// release the test drives. The startup step that is called is the real
/// <see cref="ReferenceSqliteStartupHostedService"/>.
/// </para>
/// <para>
/// What is asserted is the checkpoint, not the window after it. A cancellation that arrives once the
/// result is already on its way back is outside the guarantee, a release that hangs is not forcibly
/// terminated, and nothing here undoes a published file or a committed migration.
/// </para>
/// </remarks>
public sealed class ReferenceSqliteStartupCompletionTests
{
    private const string FinalRecordPrefix = "Reference SQLite startup deployment finished";

    // A synthetic secret carried by a release failure, asserted to stay out of the gate's own
    // output. It exists only inside this test.
    private const string SyntheticReleaseSecret = "synthetic-reference-release-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_result_and_no_final_record_are_published_before_the_scope_is_released()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new ControlledScopedExecutor(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(directory, executor, recorder);
        var startup = container.GetRequiredService<ReferenceSqliteStartupHostedService>();

        var starting = startup.StartingAsync(Token);
        await entered.Task.WaitAsync(Token);

        // The work is done and the scope is still being released: nothing may be published yet.
        Assert.Null(startup.Result);
        Assert.Empty(recorder.FinalRecords);

        release.SetResult();
        await starting;

        Assert.Equal(ReferenceSqliteStartupOutcome.Ready, startup.Result!.Outcome);
        Assert.True(executor.Released);
        // Exactly one final record for one startup call, carrying the finite outcome only.
        var record = Assert.Single(recorder.FinalRecords);
        Assert.Contains(
            ReferenceSqliteStartupOutcome.Ready.ToString(),
            record,
            StringComparison.Ordinal);
        Assert.DoesNotContain(directory.DatabasePath, record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_that_cancels_the_caller_stops_the_startup_with_the_original_token()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        using var abort = new CancellationTokenSource();
        var executor = new ControlledScopedExecutor(async () =>
        {
            // The release completes normally, but the caller has given up by the time it does.
            await abort.CancelAsync();
        });
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(directory, executor, recorder);
        var startup = container.GetRequiredService<ReferenceSqliteStartupHostedService>();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await startup.StartingAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        // This call published nothing: no Ready result, no final record, and no host start.
        Assert.Null(startup.Result);
        Assert.Empty(recorder.FinalRecords);
        Assert.True(executor.Released);
        Assert.False(executor.Executed);
    }

    [Fact]
    public async Task A_cancelled_release_stops_the_coordinator_with_the_original_token()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        using var abort = new CancellationTokenSource();
        var executor = new ControlledScopedExecutor(async () => await abort.CancelAsync());
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(directory, executor, recorder);
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.RunAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Empty(recorder.FinalRecords);
    }

    [Fact]
    public async Task An_ordinary_release_failure_is_a_safe_MigrationFailed()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var executor = new ControlledScopedExecutor(() =>
            throw new InvalidOperationException(SyntheticReleaseSecret));
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(directory, executor, recorder);
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();

        var result = await coordinator.RunAsync(Token);

        Assert.Equal(ReferenceSqliteStartupOutcome.MigrationFailed, result.Outcome);
        Assert.False(result.IsReady);
        var record = Assert.Single(recorder.FinalRecords);
        Assert.DoesNotContain(SyntheticReleaseSecret, record, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SyntheticReleaseSecret,
            result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_internal_cancellation_during_the_release_is_a_MigrationFailed()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();
        var executor = new ControlledScopedExecutor(() =>
            throw new OperationCanceledException(unrelated.Token));
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(directory, executor, recorder);
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();

        // The caller never cancelled, so somebody else's cancellation is an ordinary failure.
        var result = await coordinator.RunAsync(Token);

        Assert.Equal(ReferenceSqliteStartupOutcome.MigrationFailed, result.Outcome);
    }

    [Fact]
    public async Task A_release_failure_that_meets_a_caller_cancellation_reports_the_cancellation()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        using var abort = new CancellationTokenSource();
        var executor = new ControlledScopedExecutor(async () =>
        {
            await abort.CancelAsync();
            throw new InvalidOperationException(SyntheticReleaseSecret);
        });
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(directory, executor, recorder);
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.RunAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.DoesNotContain(SyntheticReleaseSecret, failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(recorder.FinalRecords);
    }

    [Fact]
    public async Task A_migration_that_did_not_succeed_is_still_MigrationFailed_after_the_release()
    {
        using var directory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(directory);
        var executor = new ControlledScopedExecutor(() => { })
        {
            Observation = MigrationObservationState.InspectionFailed
        };
        var recorder = new RecordingLoggerProvider();
        await using var container = BuildContainer(directory, executor, recorder);
        var coordinator = container.GetRequiredService<ReferenceSqliteStartupCoordinator>();

        var result = await coordinator.RunAsync(Token);

        Assert.Equal(ReferenceSqliteStartupOutcome.MigrationFailed, result.Outcome);
        Assert.False(executor.Executed);
        Assert.True(executor.Released);
        Assert.Single(recorder.FinalRecords);
    }

    [Fact]
    public async Task Two_independent_startups_do_not_share_a_cancellation_or_a_result()
    {
        using var cancelledDirectory = TemporaryDirectory.Create();
        using var completingDirectory = TemporaryDirectory.Create();
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(cancelledDirectory);
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(completingDirectory);
        using var abort = new CancellationTokenSource();
        using var unaffected = new CancellationTokenSource();
        var cancelledExecutor = new ControlledScopedExecutor(async () => await abort.CancelAsync());
        var completingExecutor = new ControlledScopedExecutor(() => { });
        var cancelledRecorder = new RecordingLoggerProvider();
        var completingRecorder = new RecordingLoggerProvider();
        await using var cancelledContainer =
            BuildContainer(cancelledDirectory, cancelledExecutor, cancelledRecorder);
        await using var completingContainer =
            BuildContainer(completingDirectory, completingExecutor, completingRecorder);

        var cancelledStartup = cancelledContainer
            .GetRequiredService<ReferenceSqliteStartupHostedService>();
        var completingStartup = completingContainer
            .GetRequiredService<ReferenceSqliteStartupHostedService>();
        var cancelledCall = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelledStartup.StartingAsync(abort.Token));
        await completingStartup.StartingAsync(unaffected.Token);

        var failure = await cancelledCall;
        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Null(cancelledStartup.Result);
        Assert.Equal(ReferenceSqliteStartupOutcome.Ready, completingStartup.Result!.Outcome);
        Assert.False(unaffected.IsCancellationRequested);
        Assert.Empty(cancelledRecorder.FinalRecords);
        Assert.Single(completingRecorder.FinalRecords);
    }

    /// <summary>
    /// Composes the gate exactly as the sample registers it, and replaces only the scoped executor
    /// so this call's scope release is the part the test drives.
    /// </summary>
    private static ServiceProvider BuildContainer(
        TemporaryDirectory directory,
        ControlledScopedExecutor executor,
        RecordingLoggerProvider recorder)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ReferenceSqliteStartupOptions.EnabledKey] = "true",
                [ReferenceSqliteStartupOptions.DeploymentModeKey] = "SingleInstance",
                [ReferenceSqliteStartupOptions.PrepareIfMissingKey] = "false",
                [ReferenceSqliteStartupOptions.DatabasePathKey] = directory.DatabasePath,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(recorder);
            builder.SetMinimumLevel(LogLevel.Information);
        });
        services.AddSingleton(ServiceId.Parse("reference-service"));
        var options = services.AddReferenceSqliteStartup(configuration)!;
        services.AddDbContext<ReferenceDbContext>(builder =>
            builder.UseSqlite(options.TargetConnectionString));
        services.AddScoped<IDatabaseMigrationExecutor>(_ => executor);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A scoped executor whose observation is fixed and whose release is the test's own operation.
    /// It runs no migration, so nothing here can change the file it was pointed at.
    /// </summary>
    private sealed class ControlledScopedExecutor(Func<ValueTask> release)
        : IDatabaseMigrationExecutor, IAsyncDisposable
    {
        public ControlledScopedExecutor(Action release)
            : this(() =>
            {
                release();
                return ValueTask.CompletedTask;
            })
        {
        }

        public MigrationObservationState Observation { get; init; } =
            MigrationObservationState.CurrentVersionCompatible;

        public bool Executed { get; private set; }

        public bool Released { get; private set; }

        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Observation);

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            Executed = true;
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            Released = true;
            await release();
        }
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
