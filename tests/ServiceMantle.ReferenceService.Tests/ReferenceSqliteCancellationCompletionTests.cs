using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.Sqlite;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the finalisation boundary of the sample's consumer-owned SQLite migration executor: the
/// caller cancellation that is observed once this call's own work and its release are over, and
/// which outranks the finite observation or the completed migration that had already been computed.
/// </summary>
/// <remarks>
/// <para>
/// Every case here is deterministic. The execution boundary is driven through EF Core's own public
/// extensibility - an <see cref="IMigrator"/> placed in the context's internal service provider -
/// and the observation boundary through the executor's controlled observation overload. Neither
/// changes what the file-backed inspection reads; <see cref="ReferenceSqliteMigrationTests"/> owns
/// the finite history matrix against real files.
/// </para>
/// <para>
/// What is asserted is the checkpoint, not the window after it. A cancellation that arrives while
/// the result is already on its way back to the caller is outside the guarantee, uncooperative
/// synchronous SQLite I/O is not forcibly interrupted, and a cancelled execution is not a
/// rolled-back one.
/// </para>
/// </remarks>
public sealed class ReferenceSqliteCancellationCompletionTests
{
    // A synthetic secret carried by a provider-side failure, asserted to stay out of this
    // executor's own cancellation reporting. Nothing here opens a real database.
    private const string SyntheticProviderSecret = "synthetic-reference-sqlite-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<MigrationObservationState> FiniteObservations() =>
    [
        MigrationObservationState.Empty,
        MigrationObservationState.CurrentVersionCompatible,
        MigrationObservationState.PendingMigration,
        MigrationObservationState.VersionTooNew,
        MigrationObservationState.InspectionFailed
    ];

    [Fact]
    public async Task An_execution_cancelled_before_a_normal_completion_is_not_reported_as_success()
    {
        using var directory = TemporaryDirectory.Create();
        using var abort = new CancellationTokenSource();
        var migrator = new ControlledMigrator(async () =>
        {
            // The migration completes normally, but the caller has given up by the time it does.
            await abort.CancelAsync();
        });
        await using var context = CreateContext(directory, migrator);
        var executor = new ReferenceSqliteMigrationExecutor(
            context,
            ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false));

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(1, migrator.Invocations);
        // The observation is read-only and nothing here was allowed to create the file.
        Assert.False(File.Exists(directory.DatabasePath));
    }

    [Fact]
    public async Task An_execution_cancelled_up_front_never_starts_the_migration()
    {
        using var directory = TemporaryDirectory.Create();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var migrator = new ControlledMigrator(() => { });
        await using var context = CreateContext(directory, migrator);
        var executor = new ReferenceSqliteMigrationExecutor(
            context,
            ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false));

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(0, migrator.Invocations);
        Assert.False(File.Exists(directory.DatabasePath));
    }

    [Fact]
    public async Task An_uncancelled_execution_failure_keeps_its_own_exception()
    {
        using var directory = TemporaryDirectory.Create();
        var migrator = new ControlledMigrator(() =>
            throw new InvalidOperationException(SyntheticProviderSecret));
        await using var context = CreateContext(directory, migrator);
        var executor = new ReferenceSqliteMigrationExecutor(
            context,
            ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false));

        // This executor does not classify migration failures; that behaviour is unchanged.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(Token));

        Assert.Equal(SyntheticProviderSecret, failure.Message);
    }

    [Fact]
    public async Task An_internal_execution_cancellation_is_not_the_caller_s_cancellation()
    {
        using var directory = TemporaryDirectory.Create();
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();
        var migrator = new ControlledMigrator(() =>
            throw new OperationCanceledException(unrelated.Token));
        await using var context = CreateContext(directory, migrator);
        var executor = new ReferenceSqliteMigrationExecutor(
            context,
            ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false));

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(Token));

        // Somebody else's cancellation is reported as it was, never re-labelled as the caller's.
        Assert.Equal(unrelated.Token, failure.CancellationToken);
        Assert.NotEqual(Token, failure.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(FiniteObservations))]
    public async Task A_finite_observation_cancelled_before_the_checkpoint_throws_the_original_token(
        MigrationObservationState state)
    {
        using var directory = TemporaryDirectory.Create();
        using var abort = new CancellationTokenSource();
        await using var context = CreateContext(directory, new ControlledMigrator(() => { }));
        var executor = new ReferenceSqliteMigrationExecutor(context, async _ =>
        {
            await abort.CancelAsync();
            return state;
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.InspectAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(FiniteObservations))]
    public async Task An_observation_cancelled_while_its_connection_is_released_is_not_masked(
        MigrationObservationState state)
    {
        using var directory = TemporaryDirectory.Create();
        using var abort = new CancellationTokenSource();
        await using var context = CreateContext(directory, new ControlledMigrator(() => { }));
        var executor = new ReferenceSqliteMigrationExecutor(context, async _ =>
        {
            // The finite result is decided first and the release runs after it, exactly as the
            // file-backed observation releases its reader and connection before returning.
            await using var release = new CancelOnRelease(abort);
            return state;
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.InspectAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task An_observation_that_fails_as_the_caller_cancels_reports_the_cancellation()
    {
        using var directory = TemporaryDirectory.Create();
        using var abort = new CancellationTokenSource();
        await using var context = CreateContext(directory, new ControlledMigrator(() => { }));
        var executor = new ReferenceSqliteMigrationExecutor(context, async _ =>
        {
            await abort.CancelAsync();
            throw new InvalidOperationException(SyntheticProviderSecret);
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.InspectAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.DoesNotContain(SyntheticProviderSecret, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_uncancelled_observation_failure_stays_InspectionFailed()
    {
        using var directory = TemporaryDirectory.Create();
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();
        await using var context = CreateContext(directory, new ControlledMigrator(() => { }));
        var ordinary = new ReferenceSqliteMigrationExecutor(
            context,
            _ => throw new InvalidOperationException(SyntheticProviderSecret));
        var internalCancellation = new ReferenceSqliteMigrationExecutor(
            context,
            _ => throw new OperationCanceledException(unrelated.Token));

        Assert.Equal(MigrationObservationState.InspectionFailed, await ordinary.InspectAsync(Token));
        Assert.Equal(
            MigrationObservationState.InspectionFailed,
            await internalCancellation.InspectAsync(Token));
    }

    [Theory]
    [MemberData(nameof(FiniteObservations))]
    public async Task An_uncancelled_observation_keeps_its_finite_classification(
        MigrationObservationState state)
    {
        using var directory = TemporaryDirectory.Create();
        await using var context = CreateContext(directory, new ControlledMigrator(() => { }));
        var executor = new ReferenceSqliteMigrationExecutor(
            context,
            _ => ValueTask.FromResult(state));

        Assert.Equal(state, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task An_observation_cancelled_up_front_never_starts_the_read()
    {
        using var directory = TemporaryDirectory.Create();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var observations = 0;
        await using var context = CreateContext(directory, new ControlledMigrator(() => { }));
        var executor = new ReferenceSqliteMigrationExecutor(context, _ =>
        {
            observations++;
            return ValueTask.FromResult(MigrationObservationState.Empty);
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.InspectAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(0, observations);
    }

    [Fact]
    public async Task The_file_backed_observation_of_a_missing_target_creates_nothing()
    {
        using var directory = TemporaryDirectory.Create();
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        await using var context = CreateContext(directory, new ControlledMigrator(() => { }));
        var executor = new ReferenceSqliteMigrationExecutor(context, options);

        // The real read-only observation, not the controlled seam: an absent file is refused.
        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Two_independent_calls_do_not_share_a_token_or_a_result()
    {
        using var directory = TemporaryDirectory.Create();
        using var abort = new CancellationTokenSource();
        using var unaffected = new CancellationTokenSource();
        await using var cancelledContext = CreateContext(directory, new ControlledMigrator(() => { }));
        await using var completingContext = CreateContext(directory, new ControlledMigrator(() => { }));
        var cancelled = new ReferenceSqliteMigrationExecutor(cancelledContext, async _ =>
        {
            await abort.CancelAsync();
            return MigrationObservationState.CurrentVersionCompatible;
        });
        var completing = new ReferenceSqliteMigrationExecutor(
            completingContext,
            _ => ValueTask.FromResult(MigrationObservationState.PendingMigration));

        var cancelledCall = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelled.InspectAsync(abort.Token));
        var completingCall = completing.InspectAsync(unaffected.Token).AsTask();

        var failure = await cancelledCall;
        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(MigrationObservationState.PendingMigration, await completingCall);
        Assert.False(unaffected.IsCancellationRequested);
    }

    /// <summary>
    /// Builds a context whose migrator is the controlled one, over the sample's own writable
    /// connection. That connection is <c>ReadWrite</c>, so no case here can create the file.
    /// </summary>
    private static ReferenceDbContext CreateContext(TemporaryDirectory directory, IMigrator migrator)
    {
        var options = ReferenceSqliteStartupTests.ReadOptions(directory, prepareIfMissing: false);
        var services = new ServiceCollection()
            .AddEntityFrameworkSqlite()
            .AddScoped(_ => migrator)
            .BuildServiceProvider();
        var contextOptions = new DbContextOptionsBuilder<ReferenceDbContext>()
            .UseSqlite(options.TargetConnectionString)
            .UseInternalServiceProvider(services)
            .Options;
        return new ReferenceDbContext(contextOptions);
    }

    /// <summary>An <see cref="IMigrator"/> whose one migration is whatever the test hands it.</summary>
    private sealed class ControlledMigrator(Func<Task> migrateAsync) : IMigrator
    {
        public ControlledMigrator(Action migrate)
            : this(() =>
            {
                migrate();
                return Task.CompletedTask;
            })
        {
        }

        public int Invocations { get; private set; }

        public async Task MigrateAsync(
            string? targetMigration = null,
            CancellationToken cancellationToken = default)
        {
            Invocations++;
            await migrateAsync();
        }

        public void Migrate(string? targetMigration = null) =>
            throw new NotSupportedException("The executor only migrates asynchronously.");

        public string GenerateScript(
            string? fromMigration = null,
            string? toMigration = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default) =>
            throw new NotSupportedException("The executor never generates a script.");

        public bool HasPendingModelChanges() =>
            throw new NotSupportedException("The executor never asks for pending model changes.");
    }

    /// <summary>Cancels the caller's own source as this call's resources are released.</summary>
    private sealed class CancelOnRelease(CancellationTokenSource source) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await source.CancelAsync();
    }
}
