using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServiceMantle.Installation;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

/// <summary>
/// Real PostgreSQL consumer-owned transaction coverage for Setup Code consumption and contributors.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class PostgreSqlServiceSetupTransactionTests : IAsyncLifetime
{
    private static readonly ServiceId Service = ServiceId.Parse("setup-transaction-service");
    private static readonly DateTime CreatedAtUtc =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RegisteredAtUtc =
        new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private const string StorageErrorCode = "installation.storage_error";
    private const string RejectionErrorCode = "product.setup_rejected";

    private PostgreSqlContainer? container;
    private string? connectionString;

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("servicemantle_setup_transactions")
            .WithUsername("test-user")
            .WithPassword("test-password")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        connectionString = container.GetConnectionString();
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
    public async Task Concurrent_completion_has_one_commit_and_one_safe_conflict()
    {
        var setup = await ResetAndIssueAsync();
        var options = CreateOptions();
        var barrier = new TwoActorBarrier(TimeSpan.FromSeconds(30));
        var stagedActors = new ConcurrentBag<string>();
        var coordinator = new ConsumerSetupTransactionCoordinator(options);
        var actorA = new ProductContributor("actor-a", RegistrationBehavior.Success);
        var actorB = new ProductContributor("actor-b", RegistrationBehavior.Success);

        var completionA = coordinator.CompleteAsync(
            Service,
            setup.Candidate,
            actorA,
            barrier.FirstActorAsync,
            stagedActors,
            TestContext.Current.CancellationToken);
        var completionB = coordinator.CompleteAsync(
            Service,
            setup.Candidate,
            actorB,
            barrier.SecondActorAsync,
            stagedActors,
            TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(completionA, completionB)
            .WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);

        Assert.Equal(["actor-a", "actor-b"], stagedActors.Order(StringComparer.Ordinal));
        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result =>
            !result.Succeeded &&
            result.ErrorCode == WellKnownSetupCodeErrorCodes.ConcurrencyConflict);
        Assert.Equal(1, actorA.RegisterCallCount);
        Assert.Equal(1, actorB.RegisterCallCount);
        AssertSafe(results, setup);

        await using var verification = new SetupTransactionDbContext(options);
        var installation = await ReadInstallationAsync(verification);
        var productRows = await verification.ProductRegistrations
            .AsNoTracking()
            .Where(item => item.ServiceId == Service.Value)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstallationStatus.Completed, installation.Status);
        Assert.NotNull(installation.CompletedAtUtc);
        Assert.Equal(3, installation.Version);
        Assert.Equal(1, installation.SetupCodeGeneration);
        Assert.Null(installation.SetupCodeDigest);
        Assert.Null(installation.SetupCodeIssuedAtUtc);
        Assert.Null(installation.SetupCodeExpiresAtUtc);
        var committedProduct = Assert.Single(productRows);
        Assert.Contains(committedProduct.Actor, new[] { "actor-a", "actor-b" });
    }

    [Theory]
    [InlineData(RegistrationBehavior.Rejection, RejectionErrorCode)]
    [InlineData(RegistrationBehavior.Exception, WellKnownServiceSetupErrorCodes.ContributorFailed)]
    public async Task Contributor_failure_rolls_back_and_fresh_scope_can_retry(
        RegistrationBehavior behavior,
        string expectedErrorCode)
    {
        var setup = await ResetAndIssueAsync();
        var options = CreateOptions();
        var contributor = new ProductContributor("failed-actor", behavior);
        var coordinator = new ConsumerSetupTransactionCoordinator(options);

        var result = await coordinator.CompleteAsync(
            Service,
            setup.Candidate,
            contributor,
            beforeSave: null,
            stagedActors: null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Equal(1, contributor.RegisterCallCount);
        AssertSafe([result], setup);
        await AssertPendingCodeValidAndNoProductRowsAsync(options, setup.Candidate);
        await AssertFreshRetrySucceedsAsync(options, setup, $"retry-{behavior}");
    }

    [Fact]
    public async Task Caller_cancellation_rolls_back_and_fresh_scope_can_retry()
    {
        var setup = await ResetAndIssueAsync();
        var options = CreateOptions();
        var contributor = new ProductContributor("cancelled-actor", RegistrationBehavior.WaitForCancellation);
        var coordinator = new ConsumerSetupTransactionCoordinator(options);
        using var cancellation = new CancellationTokenSource();
        var completion = coordinator.CompleteAsync(
            Service,
            setup.Candidate,
            contributor,
            beforeSave: null,
            stagedActors: null,
            cancellation.Token);
        await contributor.RegistrationStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => completion);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, contributor.RegisterCallCount);
        Assert.DoesNotContain(setup.Candidate, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(setup.Digest, exception.ToString(), StringComparison.Ordinal);
        await AssertPendingCodeValidAndNoProductRowsAsync(options, setup.Candidate);
        await AssertFreshRetrySucceedsAsync(options, setup, "retry-cancellation");
    }

    private async Task<SetupMaterial> ResetAndIssueAsync()
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql,
            connectionString is not null);
        var options = CreateOptions();
        await using var context = new SetupTransactionDbContext(options);
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = Service.Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = CreatedAtUtc,
            Version = 1,
            SetupCodeGeneration = 0,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var issue = await new EfCoreServiceSetupCodeStore<SetupTransactionDbContext>(context)
            .CreateAsync(Service, TestContext.Current.CancellationToken);
        Assert.True(issue.IsIssued);
        return new SetupMaterial(
            issue.SetupCode!.Reveal(),
            SetupCodeDigest.Compute(issue.SetupCode).Value);
    }

    private DbContextOptions<SetupTransactionDbContext> CreateOptions()
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql,
            connectionString is not null);
        return new DbContextOptionsBuilder<SetupTransactionDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    private static async Task AssertPendingCodeValidAndNoProductRowsAsync(
        DbContextOptions<SetupTransactionDbContext> options,
        string candidate)
    {
        await using var context = new SetupTransactionDbContext(options);
        var installation = await ReadInstallationAsync(context);
        var validation = await new EfCoreServiceSetupCodeStore<SetupTransactionDbContext>(context)
            .ValidateAsync(Service, candidate, TestContext.Current.CancellationToken);

        Assert.Equal(InstallationStatus.PendingSetup, installation.Status);
        Assert.Null(installation.CompletedAtUtc);
        Assert.Equal(2, installation.Version);
        Assert.NotNull(installation.SetupCodeDigest);
        Assert.NotNull(installation.SetupCodeIssuedAtUtc);
        Assert.NotNull(installation.SetupCodeExpiresAtUtc);
        Assert.True(validation.IsValid);
        Assert.Equal(
            0,
            await context.ProductRegistrations.AsNoTracking().CountAsync(
                item => item.ServiceId == Service.Value,
                TestContext.Current.CancellationToken));
    }

    private async Task AssertFreshRetrySucceedsAsync(
        DbContextOptions<SetupTransactionDbContext> options,
        SetupMaterial setup,
        string actor)
    {
        var result = await new ConsumerSetupTransactionCoordinator(options).CompleteAsync(
            Service,
            setup.Candidate,
            new ProductContributor(actor, RegistrationBehavior.Success),
            beforeSave: null,
            stagedActors: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        AssertSafe([result], setup);
        await using var verification = new SetupTransactionDbContext(options);
        var installation = await ReadInstallationAsync(verification);
        Assert.Equal(InstallationStatus.Completed, installation.Status);
        Assert.Null(installation.SetupCodeDigest);
        Assert.Equal(
            1,
            await verification.ProductRegistrations.AsNoTracking().CountAsync(
                item => item.ServiceId == Service.Value,
                TestContext.Current.CancellationToken));
    }

    private void AssertSafe(
        IEnumerable<SetupCompletionResult> results,
        SetupMaterial setup)
    {
        var parsed = new NpgsqlConnectionStringBuilder(connectionString);
        var forbidden = new[]
        {
            setup.Candidate,
            setup.Digest,
            connectionString!,
            parsed.Host,
            parsed.Username,
            parsed.Password,
            "UPDATE service_installations",
            "Npgsql",
            "DbUpdateConcurrencyException",
        };

        foreach (var result in results)
        {
            var projection = result.ToString();
            foreach (var value in forbidden.OfType<string>().Where(value => value.Length != 0))
            {
                Assert.DoesNotContain(value, projection, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static Task<ServiceInstallationEntity> ReadInstallationAsync(
        SetupTransactionDbContext context) =>
        context.ServiceInstallations
            .AsNoTracking()
            .SingleAsync(
                item => item.ServiceId == Service.Value,
                TestContext.Current.CancellationToken);

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    public enum RegistrationBehavior
    {
        Success,
        Rejection,
        Exception,
        WaitForCancellation,
    }

    private sealed record SetupMaterial(string Candidate, string Digest);

    private sealed class SetupCompletionResult
    {
        private SetupCompletionResult(string? errorCode)
        {
            ErrorCode = errorCode;
        }

        public bool Succeeded => ErrorCode is null;

        public string? ErrorCode { get; }

        internal static SetupCompletionResult Success() => new(errorCode: null);

        internal static SetupCompletionResult Failure(string errorCode) => new(errorCode);

        public override string ToString() =>
            $"SetupCompletionResult(Succeeded={Succeeded}, ErrorCode={ErrorCode ?? "<none>"})";
    }

    private sealed class ConsumerSetupTransactionCoordinator(
        DbContextOptions<SetupTransactionDbContext> options)
    {
        internal async Task<SetupCompletionResult> CompleteAsync(
            ServiceId serviceId,
            string candidate,
            ProductContributor contributor,
            Func<CancellationToken, ValueTask>? beforeSave,
            ConcurrentBag<string>? stagedActors,
            CancellationToken cancellationToken)
        {
            await using var context = new SetupTransactionDbContext(options);
            await using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                contributor.Attach(context);
                var orchestration = await new ServiceSetupOrchestrator(
                    [contributor],
                    new DbContextSetupStagingScope(context))
                    .OrchestrateAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!orchestration.Succeeded)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return SetupCompletionResult.Failure(orchestration.ErrorCode!);
                }

                var consumption = await new EfCoreServiceSetupCodeStore<SetupTransactionDbContext>(context)
                    .StageConsumeAsync(serviceId, candidate, cancellationToken)
                    .ConfigureAwait(false);
                if (!consumption.IsStaged)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return SetupCompletionResult.Failure(consumption.ErrorCode!);
                }

                stagedActors?.Add(contributor.Actor);
                if (beforeSave is not null)
                {
                    await beforeSave(cancellationToken).ConfigureAwait(false);
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SetupCompletionResult.Success();
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SetupCompletionResult.Failure(
                    WellKnownSetupCodeErrorCodes.ConcurrencyConflict);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SetupCompletionResult.Failure(StorageErrorCode);
            }
        }
    }

    private sealed class DbContextSetupStagingScope(SetupTransactionDbContext context)
        : IServiceSetupStagingScope
    {
        public bool HasPendingChanges
        {
            get
            {
                context.ChangeTracker.DetectChanges();
                return context.ChangeTracker.Entries().Any(entry =>
                    entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
            }
        }

        public ValueTask DiscardPendingChangesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ChangeTracker.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProductContributor(
        string actor,
        RegistrationBehavior behavior) : IServiceSetupContributor
    {
        private int registerCallCount;
        private SetupTransactionDbContext? context;

        public int Order => 10;

        internal string Actor => actor;

        internal int RegisterCallCount => Volatile.Read(ref registerCallCount);

        internal TaskCompletionSource RegistrationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Attach(SetupTransactionDbContext dbContext)
        {
            if (Interlocked.CompareExchange(ref context, dbContext, comparand: null) is not null)
            {
                throw new InvalidOperationException();
            }
        }

        public ValueTask<ServiceSetupContributorResult> ValidateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ServiceSetupContributorResult.Success());
        }

        public async ValueTask<ServiceSetupContributorResult> RegisterAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref registerCallCount);
            var attachedContext = context ?? throw new InvalidOperationException();
            attachedContext.ProductRegistrations.Add(new SetupProductRegistration
            {
                ServiceId = Service.Value,
                Actor = actor,
                RegisteredAtUtc = RegisteredAtUtc,
            });
            RegistrationStarted.TrySetResult();

            return behavior switch
            {
                RegistrationBehavior.Success => ServiceSetupContributorResult.Success(),
                RegistrationBehavior.Rejection => ServiceSetupContributorResult.Rejected(RejectionErrorCode),
                RegistrationBehavior.Exception => throw new InvalidOperationException(
                    "Host=db.internal;Username=admin;Password=provider-secret;SELECT 1"),
                RegistrationBehavior.WaitForCancellation => await WaitForCancellationAsync(
                    cancellationToken),
                _ => throw new InvalidOperationException(),
            };
        }

        private static async ValueTask<ServiceSetupContributorResult> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return ServiceSetupContributorResult.Success();
        }
    }

    private sealed class SetupTransactionDbContext(
        DbContextOptions<SetupTransactionDbContext> options) : DbContext(options), IServiceMantleDbContext
    {
        public DbSet<ServiceInstallationEntity> ServiceInstallations => Set<ServiceInstallationEntity>();

        internal DbSet<SetupProductRegistration> ProductRegistrations =>
            Set<SetupProductRegistration>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddServiceMantleInstallation();
            modelBuilder.Entity<SetupProductRegistration>(entity =>
            {
                entity.ToTable("setup_product_registrations");
                entity.HasKey(item => new { item.ServiceId, item.Actor });
                entity.Property(item => item.ServiceId)
                    .HasColumnName("service_id")
                    .HasMaxLength(128)
                    .IsRequired();
                entity.Property(item => item.Actor)
                    .HasColumnName("actor")
                    .HasMaxLength(64)
                    .IsRequired();
                entity.Property(item => item.RegisteredAtUtc)
                    .HasColumnName("registered_at_utc")
                    .IsRequired();
            });
        }
    }

    private sealed class SetupProductRegistration
    {
        public string ServiceId { get; set; } = string.Empty;

        public string Actor { get; set; } = string.Empty;

        public DateTime RegisteredAtUtc { get; set; }
    }
}
