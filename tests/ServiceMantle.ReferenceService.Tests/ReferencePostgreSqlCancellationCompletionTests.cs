using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the finalisation boundary of the sample's consumer-owned PostgreSQL migration executor:
/// the caller cancellation that is observed once this call's own work and its release are over, and
/// which outranks the finite observation or the completed execution that had already been computed.
/// </summary>
/// <remarks>
/// <para>
/// Every case here is deterministic and runs without a database. The execution boundary is driven
/// through EF Core's own public extensibility - an <see cref="IMigrator"/> placed in the context's
/// internal service provider - and the observation boundary through the executor's controlled
/// observation overload. Neither replaces what the database-backed inspection reads on a real
/// server; <see cref="ReferencePostgreSqlMigrationTests"/> owns that, and this file never opens a
/// connection.
/// </para>
/// <para>
/// What is asserted is the checkpoint, not the window after it: a cancellation that arrives once the
/// result is already on its way back to the caller is outside the guarantee, and so is the forcible
/// interruption of uncooperative provider I/O. A cancelled execution is not a rolled-back one.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlCancellationCompletionTests
{
    // A synthetic secret carried by a provider-side failure, asserted to stay out of the executor's
    // own diagnostics. Nothing here reaches a real server.
    private const string SyntheticProviderSecret = "synthetic-reference-provider-secret";

    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=servicemantle_reference_unreachable;Username=unused;Password=unused;Timeout=1";

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
        using var abort = new CancellationTokenSource();
        var migrator = new ControlledMigrator(async () =>
        {
            // The migration completes normally, but the caller has given up by the time it does.
            await abort.CancelAsync();
        });
        await using var context = CreateContext(migrator);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, UnreachableConnectionString);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.IsNotType<ReferencePostgreSqlMigrationFailedException>(failure);
        Assert.Equal(1, migrator.Invocations);
    }

    [Fact]
    public async Task An_execution_that_fails_as_the_caller_cancels_keeps_the_original_token()
    {
        using var abort = new CancellationTokenSource();
        var migrator = new ControlledMigrator(async () =>
        {
            await abort.CancelAsync();
            throw new InvalidOperationException(SyntheticProviderSecret);
        });
        await using var context = CreateContext(migrator);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, UnreachableConnectionString);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.DoesNotContain(SyntheticProviderSecret, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_uncancelled_execution_failure_stays_the_fixed_safe_exception()
    {
        var migrator = new ControlledMigrator(() =>
            throw new InvalidOperationException(SyntheticProviderSecret));
        await using var context = CreateContext(migrator);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, UnreachableConnectionString);

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlMigrationFailedException>(async () =>
            await executor.ExecuteAsync(Token));

        Assert.Equal(
            "The reference service PostgreSQL schema migration did not complete.",
            failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(SyntheticProviderSecret, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_internal_cancellation_is_not_disguised_as_the_caller_s_cancellation()
    {
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();
        var migrator = new ControlledMigrator(() =>
            throw new OperationCanceledException(unrelated.Token));
        await using var context = CreateContext(migrator);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, UnreachableConnectionString);

        // The caller never cancelled, so somebody else's cancellation is an ordinary failure.
        await Assert.ThrowsAsync<ReferencePostgreSqlMigrationFailedException>(async () =>
            await executor.ExecuteAsync(Token));
    }

    [Fact]
    public async Task An_execution_cancelled_up_front_never_starts_the_migration()
    {
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var migrator = new ControlledMigrator(() => { });
        await using var context = CreateContext(migrator);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, UnreachableConnectionString);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(0, migrator.Invocations);
    }

    [Theory]
    [MemberData(nameof(FiniteObservations))]
    public async Task A_finite_observation_cancelled_before_the_checkpoint_throws_the_original_token(
        MigrationObservationState state)
    {
        using var abort = new CancellationTokenSource();
        await using var context = CreateContext(new ControlledMigrator(() => { }));
        var executor = new ReferencePostgreSqlMigrationExecutor(context, async _ =>
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
        using var abort = new CancellationTokenSource();
        await using var context = CreateContext(new ControlledMigrator(() => { }));
        var executor = new ReferencePostgreSqlMigrationExecutor(context, async _ =>
        {
            // The finite result is computed first and the release runs after it, exactly as the
            // database-backed observation releases its connection and reader before returning.
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
        using var abort = new CancellationTokenSource();
        await using var context = CreateContext(new ControlledMigrator(() => { }));
        var executor = new ReferencePostgreSqlMigrationExecutor(
            context,
            async _ =>
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
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();
        await using var context = CreateContext(new ControlledMigrator(() => { }));
        var ordinary = new ReferencePostgreSqlMigrationExecutor(
            context,
            _ => throw new InvalidOperationException(SyntheticProviderSecret));
        var internalCancellation = new ReferencePostgreSqlMigrationExecutor(
            context,
            _ => throw new OperationCanceledException(unrelated.Token));

        // Neither an ordinary provider failure nor somebody else's cancellation is the caller's.
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
        await using var context = CreateContext(new ControlledMigrator(() => { }));
        var executor = new ReferencePostgreSqlMigrationExecutor(
            context,
            _ => ValueTask.FromResult(state));

        Assert.Equal(state, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task An_observation_cancelled_up_front_never_starts_the_read()
    {
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var observations = 0;
        await using var context = CreateContext(new ControlledMigrator(() => { }));
        var executor = new ReferencePostgreSqlMigrationExecutor(context, _ =>
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
    public async Task Two_independent_calls_do_not_share_a_token_or_a_result()
    {
        using var abort = new CancellationTokenSource();
        using var unaffected = new CancellationTokenSource();
        await using var cancelledContext = CreateContext(new ControlledMigrator(() => { }));
        await using var completingContext = CreateContext(new ControlledMigrator(() => { }));
        var cancelled = new ReferencePostgreSqlMigrationExecutor(cancelledContext, async _ =>
        {
            await abort.CancelAsync();
            return MigrationObservationState.CurrentVersionCompatible;
        });
        var completing = new ReferencePostgreSqlMigrationExecutor(
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
    /// Builds a context whose migrator is the controlled one. The connection string is deliberately
    /// unreachable: nothing in this file is allowed to reach a server.
    /// </summary>
    private static ReferencePostgreSqlDbContext CreateContext(IMigrator migrator)
    {
        var services = new ServiceCollection()
            .AddEntityFrameworkNpgsql()
            .AddScoped(_ => migrator)
            .BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseInternalServiceProvider(services)
            .Options;
        return new ReferencePostgreSqlDbContext(options);
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
