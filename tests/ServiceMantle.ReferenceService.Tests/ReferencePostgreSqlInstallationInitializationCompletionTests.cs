using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Installation;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the qualification and delegation boundaries of the sample's composite initialization
/// executor without a database: which observations qualify <c>ExecuteAsync</c> at all, what a
/// pending-migration target is delegated to, and that an unqualified execution has no side
/// effects.
/// </summary>
/// <remarks>
/// <para>
/// Every case here is deterministic and runs without a database. The observation boundary is
/// driven through the schema executor's controlled observation overload, the migration boundary
/// through EF Core's own public extensibility - an <see cref="IMigrator"/> placed in the
/// context's internal service provider - and the installation row boundary through a counting
/// store double. Nothing here reaches a real server; the atomic transaction itself is covered by
/// <see cref="ReferencePostgreSqlInstallationInitializationTests"/> on a real PostgreSQL server.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlInstallationInitializationCompletionTests
{
    private const string UnqualifiedFailureMessage =
        "The reference service PostgreSQL installation initialization did not complete.";

    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=servicemantle_reference_unreachable;Username=unused;Password=unused;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<MigrationObservationState> DisqualifyingObservations() =>
    [
        MigrationObservationState.CurrentVersionCompatible,
        MigrationObservationState.VersionTooNew,
        MigrationObservationState.InspectionFailed
    ];

    [Fact]
    public async Task An_execution_without_any_observation_is_a_fixed_failure_with_no_side_effects()
    {
        var migrator = new ControlledMigrator(() => { });
        await using var context = CreateContext(migrator);
        var store = new CountingStore();
        var executor = CreateExecutor(context, store, _ => ValueTask.FromResult(MigrationObservationState.Empty));

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlInstallationInitializationFailedException>(
            async () => await executor.ExecuteAsync(Token));

        Assert.Equal(UnqualifiedFailureMessage, failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(0, migrator.Invocations);
        Assert.Equal(0, store.CreatePendingCalls);
    }

    [Theory]
    [MemberData(nameof(DisqualifyingObservations))]
    public async Task A_disqualifying_observation_is_a_fixed_failure_with_no_side_effects(
        MigrationObservationState state)
    {
        var migrator = new ControlledMigrator(() => { });
        await using var context = CreateContext(migrator);
        var store = new CountingStore();
        var executor = CreateExecutor(context, store, _ => ValueTask.FromResult(state));

        Assert.Equal(state, await executor.InspectAsync(Token));

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlInstallationInitializationFailedException>(
            async () => await executor.ExecuteAsync(Token));

        Assert.Equal(UnqualifiedFailureMessage, failure.Message);
        Assert.Null(failure.InnerException);
        // The schema executor's migration never ran and the store was never asked for a row.
        Assert.Equal(0, migrator.Invocations);
        Assert.Equal(0, store.CreatePendingCalls);
    }

    [Fact]
    public async Task A_pending_migration_is_delegated_without_writing_an_installation_row()
    {
        var migrator = new ControlledMigrator(() => { });
        await using var context = CreateContext(migrator);
        var store = new CountingStore();
        var executor = CreateExecutor(
            context,
            store,
            _ => ValueTask.FromResult(MigrationObservationState.PendingMigration));

        Assert.Equal(MigrationObservationState.PendingMigration, await executor.InspectAsync(Token));
        await executor.ExecuteAsync(Token);

        // The schema executor's own execution boundary ran once; the initialization row is
        // deliberately not backfilled onto an old database.
        Assert.Equal(1, migrator.Invocations);
        Assert.Equal(0, store.CreatePendingCalls);
    }

    [Fact]
    public async Task An_empty_observation_cancelled_up_front_never_starts_the_transaction()
    {
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var migrator = new ControlledMigrator(() => { });
        await using var context = CreateContext(migrator);
        var store = new CountingStore();
        var executor = CreateExecutor(
            context,
            store,
            _ => ValueTask.FromResult(MigrationObservationState.Empty));
        await executor.InspectAsync(Token);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(0, migrator.Invocations);
        Assert.Equal(0, store.CreatePendingCalls);
    }

    [Fact]
    public async Task A_cancelled_observation_is_not_recorded_as_a_qualification()
    {
        using var abort = new CancellationTokenSource();
        var migrator = new ControlledMigrator(() => { });
        await using var context = CreateContext(migrator);
        var store = new CountingStore();
        var executor = CreateExecutor(
            context,
            store,
            async _ =>
            {
                await abort.CancelAsync();
                return MigrationObservationState.Empty;
            });

        var observation = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.InspectAsync(abort.Token));
        Assert.Equal(abort.Token, observation.CancellationToken);

        // The caller's cancellation was the observation's own, so the scope still holds no
        // completed observation and the execution is the fixed unqualified failure, not an
        // attempt to initialize anything.
        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlInstallationInitializationFailedException>(
            async () => await executor.ExecuteAsync(Token));
        Assert.Equal(UnqualifiedFailureMessage, failure.Message);
        Assert.Equal(0, migrator.Invocations);
        Assert.Equal(0, store.CreatePendingCalls);
    }

    private static ReferencePostgreSqlInstallationInitializationExecutor CreateExecutor(
        ReferencePostgreSqlDbContext context,
        IServiceInstallationStore store,
        Func<CancellationToken, ValueTask<MigrationObservationState>> observation) =>
        new(
            context,
            new ReferencePostgreSqlMigrationExecutor(context, observation),
            store,
            ServiceId.Parse("reference-init"));

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
            throw new NotSupportedException(
                "The scripted initialization is covered by the real-database tests.");

        public bool HasPendingModelChanges() =>
            throw new NotSupportedException("The executor never asks for pending model changes.");
    }

    /// <summary>Counts the installation-row writes the executor asks for.</summary>
    private sealed class CountingStore : IServiceInstallationStore
    {
        public int CreatePendingCalls { get; private set; }

        public ValueTask<ServiceInstallationState?> FindAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never finds installation state.");

        public ValueTask<ServiceInstallationState> CreatePendingAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            CreatePendingCalls++;
            return ValueTask.FromResult(ServiceInstallationState.CreatePending(serviceId));
        }

        public ValueTask<ServiceInstallationState> MarkCompletedAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor never completes an installation.");
    }
}
