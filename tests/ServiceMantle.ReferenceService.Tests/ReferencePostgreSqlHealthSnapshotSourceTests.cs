using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Health.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the reference service's live <see cref="ReferencePostgreSqlHealthSnapshotSource"/> without
/// a PostgreSQL server: the gate precondition, the field sources on a readable row, and every finite
/// failure and cancellation exit the source owns. The installation reads run against a shared-cache
/// SQLite database through the real <see cref="EfCoreServiceInstallationStore{TDbContext}"/> so the
/// store's own error codes are produced by real materialisation, not by a stubbed store.
/// </summary>
/// <remarks>
/// <para>
/// The gate result is the only precondition the source reads, and it is established directly rather
/// than by running the whole startup deployment: the subject of these tests is the source's own
/// exits, not the gate's. A ready result lets a read reach the database; a null or non-ready result
/// must fail closed before any context is created.
/// </para>
/// <para>
/// SQLite stands in for PostgreSQL only as a row store, and the factory creates contexts
/// synchronously exactly as the real <c>IDbContextFactory</c> does: the connection is opened by the
/// query, never by creation, so the caller's token is observed at the read and release boundaries
/// where the source actually cooperates with it. No assertion here depends on PostgreSQL-specific
/// SQL, and none of these tests opens a network connection.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlHealthSnapshotSourceTests
{
    private const string SyntheticSecret = "synthetic-reference-source-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ServiceId Service => ServiceId.Parse("reference-service");

    // --- Field sources on a readable row -----------------------------------------------

    [Fact]
    public async Task A_pending_row_resolves_pending_setup_with_succeeded_migration_and_reachable_database()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingRow(), createSchema: true, Token);
        var source = Source(database.Factory(), ReadyStartup());

        var snapshot = await source.GetSnapshotAsync(Token);

        Assert.Equal(ServiceStartupPhase.PendingSetup, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Reachable, snapshot.DatabaseStatus);
        Assert.Null(snapshot.ErrorCode);
    }

    [Fact]
    public async Task A_completed_row_resolves_completed_and_the_phase_never_comes_from_the_gate_result()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(CompletedRow(), createSchema: true, Token);
        // The gate froze PendingSetup at startup; the row now says Completed. The source must report
        // the row, proving the phase is re-read per request rather than copied from the gate result.
        var source = Source(database.Factory(), ReadyStartup(ServiceStartupPhase.PendingSetup));

        var snapshot = await source.GetSnapshotAsync(Token);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Reachable, snapshot.DatabaseStatus);
    }

    // --- Gate precondition ---------------------------------------------------------------

    [Fact]
    public async Task A_gate_that_never_ran_fails_closed_without_creating_a_context()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingRow(), createSchema: true, Token);
        var factory = database.Factory();
        var source = Source(factory, UnstartedStartup());

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());

        Assert.Null(failure.InnerException);
        Assert.Equal(0, factory.Creations);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task A_gate_that_is_not_ready_fails_closed_without_creating_a_context()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingRow(), createSchema: true, Token);
        var factory = database.Factory();
        var source = Source(factory, NotReadyStartup());

        await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());

        Assert.Equal(0, factory.Creations);
    }

    // --- Finite failure exits (C8) -------------------------------------------------------

    [Fact]
    public async Task A_factory_creation_failure_is_the_one_fixed_exception()
    {
        var factory = new StubFactory(() => throw new InvalidOperationException(SyntheticSecret));
        var source = Source(factory, ReadyStartup());

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());

        Assert.Null(failure.InnerException);
        Assert.Equal(ReferencePostgreSqlHealthSnapshotUnavailableException.FixedMessage, failure.Message);
        Assert.DoesNotContain(SyntheticSecret, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_installation_row_is_the_one_fixed_exception()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(entity: null, createSchema: true, Token);
        var source = Source(database.Factory(), ReadyStartup());

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());

        Assert.Null(failure.InnerException);
        // A missing row is never resolved to PendingSetup: the resolver is not handed a null state.
        Assert.Equal(ReferencePostgreSqlHealthSnapshotUnavailableException.FixedMessage, failure.Message);
    }

    [Fact]
    public async Task A_read_that_cannot_reach_the_table_is_the_one_fixed_exception()
    {
        // No schema: the query fails, and the store raises installation.storage_error.
        await using var database = await StubDatabase.OpenAsync(Token);
        var source = Source(database.Factory(), ReadyStartup());

        await AssertStoreRaisesAsync(database, "installation.storage_error");
        await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());
    }

    [Fact]
    public async Task A_read_of_a_row_with_an_invalid_status_is_the_one_fixed_exception()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(InvalidStatusRow(), createSchema: true, Token);
        var source = Source(database.Factory(), ReadyStartup());

        await AssertStoreRaisesAsync(database, "installation.entity_invalid");
        await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());
    }

    [Fact]
    public async Task A_read_of_a_row_that_violates_a_state_invariant_is_the_one_fixed_exception()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingWithCompletionRow(), createSchema: true, Token);
        var source = Source(database.Factory(), ReadyStartup());

        await AssertStoreRaisesAsync(database, "installation.state_invariant_violation");
        await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());
    }

    [Fact]
    public async Task A_release_failure_is_the_one_fixed_exception()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingRow(), createSchema: true, Token);
        var interceptor = new StubInterceptor { ThrowOnClose = new InvalidOperationException(SyntheticSecret) };
        var source = Source(database.Factory(interceptor), ReadyStartup());

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());

        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(SyntheticSecret, failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, interceptor.Closes);
    }

    [Fact]
    public async Task A_cancellation_the_caller_did_not_request_is_the_one_fixed_exception()
    {
        using var stranger = new CancellationTokenSource();
        await stranger.CancelAsync();
        var factory = new StubFactory(() => throw new OperationCanceledException(stranger.Token));
        var source = Source(factory, ReadyStartup());

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());

        Assert.Null(failure.InnerException);
    }

    // --- Cancellation exits (C7) ---------------------------------------------------------

    [Fact]
    public async Task An_already_cancelled_read_creates_no_context()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingRow(), createSchema: true, Token);
        var factory = database.Factory();
        var source = Source(factory, ReadyStartup());
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetSnapshotAsync(abort.Token).AsTask());

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.Equal(0, factory.Creations);
    }

    [Fact]
    public async Task A_caller_cancellation_during_the_read_keeps_the_caller_token()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingRow(), createSchema: true, Token);
        using var abort = new CancellationTokenSource();
        var interceptor = new StubInterceptor { CancelOnCommand = abort };
        var source = Source(database.Factory(interceptor), ReadyStartup());

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetSnapshotAsync(abort.Token).AsTask());

        Assert.Equal(abort.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task A_caller_cancellation_during_release_keeps_the_caller_token()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingRow(), createSchema: true, Token);
        using var abort = new CancellationTokenSource();
        var interceptor = new StubInterceptor { CancelOnClose = abort };
        var source = Source(database.Factory(interceptor), ReadyStartup());

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetSnapshotAsync(abort.Token).AsTask());

        Assert.Equal(abort.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task A_read_that_blocks_until_the_caller_cancels_keeps_the_caller_token()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingRow(), createSchema: true, Token);
        using var abort = new CancellationTokenSource();
        // The read blocks on the caller's own token, so cancelling the caller is what ends it; the
        // double never ignores the token it received and never needs a forcible interruption.
        var interceptor = new StubInterceptor { BlockOnCommand = true };
        var source = Source(database.Factory(interceptor), ReadyStartup());

        var read = source.GetSnapshotAsync(abort.Token).AsTask();
        await interceptor.WaitForBlockedCommandAsync();
        await abort.CancelAsync();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        Assert.Equal(abort.Token, failure.CancellationToken);
    }

    // --- Ownership and per-read context (C6, unit level) ---------------------------------

    [Fact]
    public async Task Each_read_creates_and_releases_its_own_context()
    {
        await using var database = await StubDatabase.OpenAsync(Token);
        await database.SeedAsync(PendingRow(), createSchema: true, Token);
        var interceptor = new StubInterceptor();
        var factory = database.Factory(interceptor);
        var source = Source(factory, ReadyStartup());

        await source.GetSnapshotAsync(Token);
        await source.GetSnapshotAsync(Token);

        Assert.Equal(2, factory.Creations);
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(2, factory.Created.Distinct().Count());
        Assert.Equal(2, interceptor.Closes);
        Assert.All(factory.Created, context =>
            Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker.Entries().ToArray()));
    }

    // --- Helpers -------------------------------------------------------------------------

    private static ReferencePostgreSqlHealthSnapshotSource Source(
        IDbContextFactory<ReferencePostgreSqlDbContext> factory,
        ReferencePostgreSqlStartupHostedService startup) =>
        new(factory, Service, startup);

    /// <summary>Confirms the crafted row really produces the store code the test names.</summary>
    private static async Task AssertStoreRaisesAsync(StubDatabase database, string expectedCode)
    {
        await using var context = new ReferencePostgreSqlDbContext(database.Options());
        var store = new EfCoreServiceInstallationStore<ReferencePostgreSqlDbContext>(context);
        var failure = await Assert.ThrowsAsync<ServiceInstallationStoreException>(
            () => store.FindAsync(Service, Token).AsTask());
        Assert.Equal(expectedCode, failure.ErrorCode);
    }

    private static ReferencePostgreSqlStartupHostedService ReadyStartup(
        ServiceStartupPhase phase = ServiceStartupPhase.PendingSetup) =>
        StartupWithResult(new ReferencePostgreSqlStartupResult(
            ReferencePostgreSqlStartupOutcome.Ready,
            executorWasCalled: false,
            phase));

    private static ReferencePostgreSqlStartupHostedService NotReadyStartup() =>
        StartupWithResult(new ReferencePostgreSqlStartupResult(
            ReferencePostgreSqlStartupOutcome.MigrationFailed,
            executorWasCalled: true));

    private static ReferencePostgreSqlStartupHostedService UnstartedStartup() =>
        StartupWithResult(result: null);

    /// <summary>
    /// Builds the concrete gate hosted service and, when a result is supplied, publishes it the way
    /// a finished startup would. The source reads only <c>Result</c>, so establishing that one
    /// precondition directly keeps these tests focused on the source rather than on re-running the
    /// whole deployment gate; no database connection is opened here.
    /// </summary>
    private static ReferencePostgreSqlStartupHostedService StartupWithResult(
        ReferencePostgreSqlStartupResult? result)
    {
        var coordinator = new ReferencePostgreSqlStartupCoordinator(
            new ServiceCollection().BuildServiceProvider(),
            ReferencePostgreSqlStartupTests.ReadOptions(prepareIfMissing: false),
            NullLogger<ReferencePostgreSqlStartupCoordinator>.Instance);
        var startup = new ReferencePostgreSqlStartupHostedService(coordinator);
        if (result is not null)
        {
            typeof(ReferencePostgreSqlStartupHostedService)
                .GetProperty(nameof(ReferencePostgreSqlStartupHostedService.Result))!
                .SetValue(startup, result);
        }

        return startup;
    }

    private static ServiceInstallationEntity PendingRow() => new()
    {
        ServiceId = "reference-service",
        Status = InstallationStatus.PendingSetup,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = null,
        Version = 1,
        SetupCodeGeneration = 0,
    };

    private static ServiceInstallationEntity CompletedRow() => new()
    {
        ServiceId = "reference-service",
        Status = InstallationStatus.Completed,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        Version = 1,
        SetupCodeGeneration = 0,
    };

    private static ServiceInstallationEntity InvalidStatusRow() => new()
    {
        ServiceId = "reference-service",
        Status = (InstallationStatus)99,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = null,
        Version = 1,
        SetupCodeGeneration = 0,
    };

    private static ServiceInstallationEntity PendingWithCompletionRow() => new()
    {
        ServiceId = "reference-service",
        Status = InstallationStatus.PendingSetup,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        Version = 1,
        SetupCodeGeneration = 0,
    };

    /// <summary>
    /// A synchronous context factory, matching how the real <c>IDbContextFactory</c> creates a
    /// context without opening a connection. Creation either returns a context or fails the way the
    /// test asked; the connection, and therefore the caller's token, is met at the query.
    /// </summary>
    private sealed class StubFactory(Func<ReferencePostgreSqlDbContext> create)
        : IDbContextFactory<ReferencePostgreSqlDbContext>
    {
        private int creations;

        internal int Creations => Volatile.Read(ref creations);

        internal List<ReferencePostgreSqlDbContext> Created { get; } = [];

        public ReferencePostgreSqlDbContext CreateDbContext()
        {
            Interlocked.Increment(ref creations);
            var context = create();
            lock (Created)
            {
                Created.Add(context);
            }

            return context;
        }
    }

    /// <summary>
    /// Drives the two moments these tests observe - the command about to run and the connection
    /// about to be released - and can cancel the caller, fail the release, or hold the read open on
    /// the caller's own token there.
    /// </summary>
    private sealed class StubInterceptor : DbCommandInterceptor, IDbConnectionInterceptor
    {
        private static readonly TimeSpan Observation = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int closes;

        private Task blockedCommand = Task.CompletedTask;

        internal CancellationTokenSource? CancelOnCommand { get; init; }

        internal CancellationTokenSource? CancelOnClose { get; init; }

        internal Exception? ThrowOnClose { get; init; }

        internal bool BlockOnCommand { get; init; }

        internal int Closes => Volatile.Read(ref closes);

        internal async Task WaitForBlockedCommandAsync() =>
            await blocked.Task.WaitAsync(Observation, Token);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (CancelOnCommand is not null)
            {
                CancelOnCommand.Cancel();
                throw new OperationCanceledException(CancelOnCommand.Token);
            }

            if (BlockOnCommand)
            {
                blocked.TrySetResult();
                // Held open only on the caller's token: cancelling the caller is what ends the read.
                blockedCommand = release.Task.WaitAsync(cancellationToken);
                await blockedCommand.ConfigureAwait(false);
            }

            await Task.CompletedTask.ConfigureAwait(false);
            return result;
        }

        public ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
        {
            Interlocked.Increment(ref closes);
            CancelOnClose?.Cancel();
            if (ThrowOnClose is not null)
            {
                throw ThrowOnClose;
            }

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// A shared-cache in-memory SQLite database kept alive by one open connection, so factory
    /// contexts may open and close their own connections to it exactly as they would to a server.
    /// </summary>
    private sealed class StubDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection keeper;

        private StubDatabase(SqliteConnection keeper, string connectionString)
        {
            this.keeper = keeper;
            ConnectionString = connectionString;
        }

        internal string ConnectionString { get; }

        internal static async Task<StubDatabase> OpenAsync(CancellationToken cancellationToken)
        {
            var name = "sm_ref_health_" + Guid.NewGuid().ToString("N");
            var connectionString = $"Data Source=file:{name}?mode=memory&cache=shared";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync(cancellationToken);
            return new StubDatabase(keeper, connectionString);
        }

        internal DbContextOptions<ReferencePostgreSqlDbContext> Options(
            StubInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
                .UseSqlite(ConnectionString);
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }

            return builder.Options;
        }

        internal StubFactory Factory(StubInterceptor? interceptor = null)
        {
            var options = Options(interceptor);
            return new StubFactory(() => new ReferencePostgreSqlDbContext(options));
        }

        internal async Task SeedAsync(
            ServiceInstallationEntity? entity,
            bool createSchema,
            CancellationToken cancellationToken)
        {
            await using var context = new ReferencePostgreSqlDbContext(Options());
            if (createSchema)
            {
                await context.Database.EnsureCreatedAsync(cancellationToken);
            }

            if (entity is not null)
            {
                context.ServiceInstallations.Add(entity);
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            keeper.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
