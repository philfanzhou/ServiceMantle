using System.Data.Common;
using System.Net.Sockets;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using ServiceMantle.Configuration;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

/// <summary>
/// Real PostgreSQL coverage for the encrypted Data Protection key repository under Npgsql's
/// retrying execution strategy (<c>EnableRetryOnFailure</c>) — the configuration of the consumer
/// regression reported for the 0.1.9 upgrade. Covers first persistence, a transient failure
/// before commit retried as one whole transactional unit, duplicate/concurrent idempotency, and
/// the rebuilt-host key-ring round trip.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class PostgreSqlDataProtectionKeyRepositoryTests : IAsyncLifetime
{
    private const string RootKey = "postgres-root-key-for-data-protection-retry-tests-5a91c2";
    private static readonly ServiceId Service = ServiceId.Parse("orders-api");

    private PostgreSqlContainer? container;
    private string? connectionString;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRunPostgreSqlTests())
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("servicemantle_data_protection_keys")
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
    public async Task First_store_element_succeeds_under_the_retrying_strategy()
    {
        var (options, _) = await PrepareAsync();

        // The guard that makes this test meaningful: these options really run under a retrying
        // execution strategy, which rejects a user-initiated transaction opened outside it — the
        // exact failure of the reported regression — as soon as an EF operation runs inside the
        // strategy. StoreElement succeeding on the same options therefore proves its whole
        // transaction runs inside the strategy's Execute delegate, not just SaveChanges.
        await using (var guard = new KeyDbContext(options))
        {
            await using var guardTransaction = await guard.Database.BeginTransactionAsync(
                TestContext.Current.CancellationToken);
            var rejection = await Assert.ThrowsAsync<InvalidOperationException>(
                () => guard.Set<DataProtectionKeyEntity>().AnyAsync(TestContext.Current.CancellationToken));
            Assert.Contains("user-initiated transactions", rejection.Message, StringComparison.Ordinal);
            await guardTransaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        var keyId = Guid.NewGuid();
        const string keyMaterial = "postgres-first-key-material";
        var element = CreateKey(keyId, keyMaterial);
        var repository = Repository(options);

        repository.StoreElement(element, $"key-{keyId:D}");

        await using (var context = new KeyDbContext(options))
        {
            var row = await context.Set<DataProtectionKeyEntity>()
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, await context.Set<DataProtectionKeyEntity>().CountAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(Service.Value, row.ServiceId);
            Assert.Equal($"key-{keyId:D}", row.KeyId);
            Assert.StartsWith("sm:v1:", row.EncryptedXml, StringComparison.Ordinal);
            Assert.DoesNotContain("<key", row.EncryptedXml, StringComparison.Ordinal);
            Assert.DoesNotContain(keyMaterial, row.EncryptedXml, StringComparison.Ordinal);
        }

        var loaded = Assert.Single(repository.GetAllElements());
        Assert.True(XNode.DeepEquals(element, loaded));
    }

    [Fact]
    public async Task Transient_failure_before_commit_is_retried_as_one_unit_without_partial_rows()
    {
        var (options, interceptor) = await PrepareAsync(new TransientOnceInterceptor());
        var repository = Repository(options);
        var keyId = Guid.NewGuid();
        var element = CreateKey(keyId, "postgres-transient-key-material");

        // The first attempt fails after the transaction began but before the commit; the retrying
        // strategy must re-run the whole unit and succeed without a second row or a fragment.
        repository.StoreElement(element, $"key-{keyId:D}");

        Assert.True(interceptor.Injected);
        await using var context = new KeyDbContext(options);
        Assert.Equal(
            1,
            await context.Set<DataProtectionKeyEntity>().CountAsync(TestContext.Current.CancellationToken));
        var loaded = Assert.Single(repository.GetAllElements());
        Assert.True(XNode.DeepEquals(element, loaded));
    }

    [Fact]
    public async Task Concurrent_duplicate_writes_have_one_winner_and_one_closed_failure()
    {
        var (options, _) = await PrepareAsync();
        var keyId = Guid.NewGuid();
        var element = CreateKey(keyId, "postgres-concurrent-key-material");
        var repositoryA = Repository(options);
        var repositoryB = Repository(options);

        var attempts = await Task.WhenAll(
            Task.Run(
                () => Record.Exception(() => repositoryA.StoreElement(
                    new XElement(element),
                    $"key-{keyId:D}")),
                TestContext.Current.CancellationToken),
            Task.Run(
                () => Record.Exception(() => repositoryB.StoreElement(
                    new XElement(element),
                    $"key-{keyId:D}")),
                TestContext.Current.CancellationToken));

        Assert.Single(attempts, exception => exception is null);
        var failure = Assert.IsType<DataProtectionKeyRepositoryException>(
            Assert.Single(attempts, exception => exception is not null));
        Assert.Equal(WellKnownDataProtectionKeyRepositoryErrorCodes.DuplicateKey, failure.ErrorCode);
        Assert.DoesNotContain(RootKey, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("<key", failure.ToString(), StringComparison.Ordinal);
        await using var context = new KeyDbContext(options);
        Assert.Equal(
            1,
            await context.Set<DataProtectionKeyEntity>().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rebuilt_host_reads_the_full_key_ring_and_decrypts_pre_restart_payloads()
    {
        var (options, _) = await PrepareAsync();
        string protectedPayload;
        using (var firstHost = BuildServiceProvider(options))
        {
            var protector = firstHost.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("postgres-restart-roundtrip");
            protectedPayload = protector.Protect("postgres-restart-payload");
        }

        using var rebuiltHost = BuildServiceProvider(CreateOptions());
        var keys = rebuiltHost.GetRequiredService<IKeyManager>().GetAllKeys();
        Assert.NotEmpty(keys);

        var rebuiltProtector = rebuiltHost.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("postgres-restart-roundtrip");
        Assert.Equal("postgres-restart-payload", rebuiltProtector.Unprotect(protectedPayload));
    }

    private async Task<(DbContextOptions<KeyDbContext> Options, TransientOnceInterceptor Interceptor)>
        PrepareAsync(TransientOnceInterceptor? interceptor = null)
    {
        Assert.SkipUnless(
            ShouldRunPostgreSqlTests() && connectionString is not null,
            "PostgreSQL tests disabled or container not initialized.");

        var options = CreateOptions(interceptor);
        await using var schemaContext = new KeyDbContext(options);
        await schemaContext.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await schemaContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return (options, interceptor!);
    }

    /// <summary>Every context of this class runs under Npgsql's retrying execution strategy.</summary>
    private DbContextOptions<KeyDbContext> CreateOptions(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<KeyDbContext>()
            .UseNpgsql(connectionString!, ConfigureRetry);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private static void ConfigureRetry(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.EnableRetryOnFailure();

    private static EfCoreDataProtectionKeyRepository<KeyDbContext> Repository(
        DbContextOptions<KeyDbContext> options) =>
        new(new KeyDbContextFactory(options), Service, () => RootKey);

    private static ServiceProvider BuildServiceProvider(DbContextOptions<KeyDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<KeyDbContext>>(new KeyDbContextFactory(options));
        services.AddDataProtection()
            .PersistKeysToServiceMantleEfCore<KeyDbContext>(Service, _ => RootKey);
        return services.BuildServiceProvider();
    }

    private static XElement CreateKey(Guid keyId, string keyMaterial) =>
        new(
            "key",
            new XAttribute("id", keyId.ToString("D")),
            new XAttribute("version", "1"),
            new XElement("descriptor", new XElement("masterKey", keyMaterial)));

    private static bool ShouldRunPostgreSqlTests() =>
        Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    /// <summary>
    /// Fails the repository's first INSERT with a genuinely transient-classified failure
    /// (<see cref="NpgsqlException"/> wrapping an <see cref="IOException"/>), exactly once, so
    /// the real <c>NpgsqlRetryingExecutionStrategy</c> decides to retry it. The asynchronous
    /// schema-setup commands do not pass through the synchronous interception point, and the
    /// command-text filter keeps every other statement out.
    /// </summary>
    private sealed class TransientOnceInterceptor : DbCommandInterceptor
    {
        internal const string TargetedInsertPrefix = "INSERT INTO service_data_protection_keys";

        private int injected;

        internal bool Injected => Volatile.Read(ref injected) > 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.StartsWith(TargetedInsertPrefix, StringComparison.Ordinal) &&
                Interlocked.Exchange(ref injected, 1) == 0)
            {
                throw new NpgsqlException(
                    "A simulated transient connection failure.",
                    new IOException("Simulated connection reset."));
            }

            return result;
        }
    }

    private sealed class KeyDbContextFactory(
        DbContextOptions<KeyDbContext> options) : IDbContextFactory<KeyDbContext>
    {
        public KeyDbContext CreateDbContext() => new(options);
    }

    private sealed class KeyDbContext(
        DbContextOptions<KeyDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleDataProtectionKeys();
    }
}
