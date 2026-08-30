using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests;

public sealed class ServiceSettingPersistenceTests
{
    private static readonly ServiceId Service = ServiceId.Parse("signacore");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Model_maps_one_service_aggregate_with_a_concurrency_version()
    {
        await using var harness = await Harness.CreateAsync();
        await using var context = harness.Factory().CreateDbContext();
        var entity = context.Model.FindEntityType(typeof(ServiceSettingEntity));

        Assert.NotNull(entity);
        Assert.Equal("service_settings", entity!.GetTableName());
        Assert.Equal(
            nameof(ServiceSettingEntity.ServiceId),
            entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal("service_id", entity.FindProperty(nameof(ServiceSettingEntity.ServiceId))!.GetColumnName());
        Assert.Equal("values_json", entity.FindProperty(nameof(ServiceSettingEntity.ValuesJson))!.GetColumnName());
        Assert.Equal("version", entity.FindProperty(nameof(ServiceSettingEntity.Version))!.GetColumnName());
        Assert.True(entity.FindProperty(nameof(ServiceSettingEntity.Version))!.IsConcurrencyToken);
        Assert.Equal("updated_at_utc", entity.FindProperty(nameof(ServiceSettingEntity.UpdatedAtUtc))!.GetColumnName());
        Assert.Equal("updated_by", entity.FindProperty(nameof(ServiceSettingEntity.UpdatedBy))!.GetColumnName());
        Assert.Equal("restart_required", entity.FindProperty(nameof(ServiceSettingEntity.RestartRequired))!.GetColumnName());
        Assert.Null(entity.FindProperty("InstanceId"));
    }

    [Fact]
    public async Task Missing_service_has_an_empty_version_zero_snapshot()
    {
        await using var harness = await Harness.CreateAsync();

        var snapshot = await harness.Store().LoadAsync(
            Service,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, snapshot.Version);
        Assert.Empty(snapshot.Values);
        Assert.Null(snapshot.UpdatedAtUtc);
        Assert.Null(snapshot.UpdatedBy);
        Assert.False(snapshot.RestartRequired);
    }

    [Fact]
    public async Task Batch_changes_and_version_commit_as_one_service_aggregate()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        const string firstSecret = "Password=first-secret";
        const string secondSecret = "Password=second-secret";

        var first = await store.UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?>
            {
                ["smtp.password"] = firstSecret,
                ["feature.enabled"] = "true",
            }, "operator-sensitive-id", restartRequired: true),
            TestContext.Current.CancellationToken);
        var second = await store.UpdateAsync(
            Service,
            Update(1, new Dictionary<string, string?>
            {
                ["smtp.password"] = secondSecret,
                ["feature.enabled"] = null,
                ["worker.count"] = "4",
            }, "operator-2", restartRequired: false),
            TestContext.Current.CancellationToken);
        var snapshot = await store.LoadAsync(Service, TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.Equal(1, first.Version);
        Assert.True(second.Succeeded);
        Assert.Equal(2, second.Version);
        Assert.Equal(2, snapshot.Version);
        Assert.Equal(2, snapshot.Values.Count);
        Assert.Equal(secondSecret, snapshot.Values["smtp.password"]);
        Assert.Equal("4", snapshot.Values["worker.count"]);
        Assert.False(snapshot.Values.ContainsKey("feature.enabled"));
        Assert.Equal(Now, snapshot.UpdatedAtUtc);
        Assert.Equal("operator-2", snapshot.UpdatedBy);
        Assert.False(snapshot.RestartRequired);
        Assert.DoesNotContain(firstSecret, first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secondSecret, second.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secondSecret, snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("operator-2", snapshot.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expected_version_mismatch_rejects_the_entire_batch_without_writes()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        await store.UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?> { ["mode"] = "original" }),
            TestContext.Current.CancellationToken);

        var result = await store.UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?>
            {
                ["mode"] = "replacement-secret",
                ["other"] = "partial-secret",
            }),
            TestContext.Current.CancellationToken);
        var snapshot = await store.LoadAsync(Service, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.VersionConflict, result.ErrorCode);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(
            new Dictionary<string, string> { ["mode"] = "original" },
            snapshot.Values);
        Assert.DoesNotContain("replacement-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Different_services_are_isolated_and_have_independent_versions()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        var other = ServiceId.Parse("other-service");

        await store.UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?> { ["key"] = "alpha" }),
            TestContext.Current.CancellationToken);
        await store.UpdateAsync(
            other,
            Update(0, new Dictionary<string, string?> { ["key"] = "beta" }),
            TestContext.Current.CancellationToken);

        var first = await store.LoadAsync(Service, TestContext.Current.CancellationToken);
        var second = await store.LoadAsync(other, TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Version);
        Assert.Equal(1, second.Version);
        Assert.Equal("alpha", first.Values["key"]);
        Assert.Equal("beta", second.Values["key"]);
    }

    [Fact]
    public async Task Commit_failure_rolls_back_values_version_and_metadata()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Store().UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?> { ["key"] = "original" }),
            TestContext.Current.CancellationToken);
        var failingStore = harness.Store(new ThrowingCommitInterceptor(
            () => new InvalidOperationException("Host=db;Password=commit-secret")));

        var result = await failingStore.UpdateAsync(
            Service,
            Update(1, new Dictionary<string, string?> { ["key"] = "replacement-secret" }),
            TestContext.Current.CancellationToken);
        var snapshot = await harness.Store().LoadAsync(Service, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.StorageError, result.ErrorCode);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal("original", snapshot.Values["key"]);
        Assert.DoesNotContain("commit-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_before_commit_rolls_back_the_batch()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Store().UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?> { ["key"] = "original" }),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var cancellingStore = harness.Store(new ThrowingCommitInterceptor(() =>
        {
            cancellation.Cancel();
            return new OperationCanceledException("provider cancellation detail", cancellation.Token);
        }));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cancellingStore.UpdateAsync(
                Service,
                Update(1, new Dictionary<string, string?> { ["key"] = "cancelled-secret" }),
                cancellation.Token).AsTask());
        var snapshot = await harness.Store().LoadAsync(Service, TestContext.Current.CancellationToken);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.DoesNotContain("provider cancellation detail", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal("original", snapshot.Values["key"]);
    }

    [Fact]
    public async Task Internal_commit_failure_is_not_reclassified_as_caller_cancellation()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Store().UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?> { ["key"] = "original" }),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var failingStore = harness.Store(new ThrowingCommitInterceptor(() =>
        {
            cancellation.Cancel();
            return new InvalidOperationException("provider failure after cancellation");
        }));

        var result = await failingStore.UpdateAsync(
            Service,
            Update(1, new Dictionary<string, string?> { ["key"] = "replacement-secret" }),
            cancellation.Token);
        var snapshot = await harness.Store().LoadAsync(Service, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.StorageError, result.ErrorCode);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal("original", snapshot.Values["key"]);
        Assert.DoesNotContain("replacement-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Constraint_failure_is_classified_without_provider_or_value_details()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store(
            beforeSave: _ => throw new DbUpdateException(
                "constraint failure Password=provider-secret",
                new SqlStateDbException("23514", "constraint provider detail")));

        var result = await store.UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?> { ["key"] = "value-secret" }),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.ConstraintViolation, result.ErrorCode);
        Assert.DoesNotContain("provider-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("value-secret", result.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, (await harness.Store().LoadAsync(
            Service,
            TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task Non_constraint_update_failure_uses_the_storage_error_classification()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store(
            beforeSave: _ => throw new DbUpdateException(
                "write failure Password=provider-secret",
                new SqliteException("disk I/O error Password=provider-secret", 10)));

        var result = await store.UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?> { ["key"] = "value-secret" }),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.StorageError, result.ErrorCode);
        Assert.DoesNotContain("provider-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("value-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_failures_use_safe_exception_and_result_channels()
    {
        await using var harness = await Harness.CreateAsync();
        const string providerDetail = "Host=db;Username=admin;Password=read-secret";
        var store = harness.Store(new ThrowingCommandInterceptor(
            () => new InvalidOperationException(providerDetail)));

        var exception = await Assert.ThrowsAsync<ServiceSettingStoreException>(() =>
            store.LoadAsync(Service, TestContext.Current.CancellationToken).AsTask());
        var result = await store.UpdateAsync(
            Service,
            Update(0, new Dictionary<string, string?> { ["key"] = "value-secret" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.StorageError, exception.ErrorCode);
        Assert.DoesNotContain("read-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Host=db", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.StorageError, result.ErrorCode);
        Assert.DoesNotContain("value-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_storage_and_exhausted_version_fail_without_reusing_a_version()
    {
        await using var harness = await Harness.CreateAsync();
        await using (var context = harness.Factory().CreateDbContext())
        {
            context.Set<ServiceSettingEntity>().Add(new ServiceSettingEntity
            {
                ServiceId = Service.Value,
                ValuesJson = "{not-json Password=stored-secret}",
                Version = 1,
                UpdatedAtUtc = Now.UtcDateTime,
                UpdatedBy = "operator-1",
            });
            context.Set<ServiceSettingEntity>().Add(new ServiceSettingEntity
            {
                ServiceId = "exhausted-service",
                ValuesJson = "{}",
                Version = long.MaxValue,
                UpdatedAtUtc = Now.UtcDateTime,
                UpdatedBy = "operator-1",
            });
            context.Set<ServiceSettingEntity>().Add(new ServiceSettingEntity
            {
                ServiceId = "metadata-corrupt-service",
                ValuesJson = "{}",
                Version = 1,
                UpdatedAtUtc = Now.UtcDateTime,
                UpdatedBy = " ",
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var corruptException = await Assert.ThrowsAsync<ServiceSettingStoreException>(() =>
            harness.Store().LoadAsync(Service, TestContext.Current.CancellationToken).AsTask());
        var corruptUpdate = await harness.Store().UpdateAsync(
            Service,
            Update(1, new Dictionary<string, string?> { ["key"] = "new-secret" }),
            TestContext.Current.CancellationToken);
        var exhaustedUpdate = await harness.Store().UpdateAsync(
            ServiceId.Parse("exhausted-service"),
            Update(long.MaxValue, new Dictionary<string, string?> { ["key"] = "new-secret" }),
            TestContext.Current.CancellationToken);
        var metadataCorruptException = await Assert.ThrowsAsync<ServiceSettingStoreException>(() =>
            harness.Store().LoadAsync(
                ServiceId.Parse("metadata-corrupt-service"),
                TestContext.Current.CancellationToken).AsTask());
        var metadataCorruptUpdate = await harness.Store().UpdateAsync(
            ServiceId.Parse("metadata-corrupt-service"),
            Update(1, new Dictionary<string, string?> { ["key"] = "new-secret" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.StorageCorrupt, corruptException.ErrorCode);
        Assert.DoesNotContain("stored-secret", corruptException.ToString(), StringComparison.Ordinal);
        Assert.Null(corruptException.InnerException);
        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.StorageCorrupt, corruptUpdate.ErrorCode);
        Assert.Equal(WellKnownServiceSettingStoreErrorCodes.VersionExhausted, exhaustedUpdate.ErrorCode);
        Assert.DoesNotContain("new-secret", exhaustedUpdate.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            WellKnownServiceSettingStoreErrorCodes.StorageCorrupt,
            metadataCorruptException.ErrorCode);
        Assert.Null(metadataCorruptException.InnerException);
        Assert.Equal(
            WellKnownServiceSettingStoreErrorCodes.StorageCorrupt,
            metadataCorruptUpdate.ErrorCode);
    }

    private static ServiceSettingStoreUpdate Update(
        long expectedVersion,
        IReadOnlyDictionary<string, string?> changes,
        string updatedBy = "operator-1",
        bool restartRequired = false) =>
        new(expectedVersion, changes, updatedBy, restartRequired);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<SettingDbContext> options;

        private Harness(SqliteConnection connection, DbContextOptions<SettingDbContext> options)
        {
            this.connection = connection;
            this.options = options;
        }

        internal static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<SettingDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var context = new SettingDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new Harness(connection, options);
        }

        internal SettingDbContextFactory Factory(
            IInterceptor? interceptor = null,
            Func<CancellationToken, Task>? beforeSave = null)
        {
            if (interceptor is null)
            {
                return new SettingDbContextFactory(options, beforeSave);
            }

            var interceptedOptions = new DbContextOptionsBuilder<SettingDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;
            return new SettingDbContextFactory(interceptedOptions, beforeSave);
        }

        internal EfCoreServiceSettingStore<SettingDbContext> Store(
            IInterceptor? interceptor = null,
            Func<CancellationToken, Task>? beforeSave = null) =>
            new(Factory(interceptor, beforeSave), new FixedTimeProvider(Now));

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class SettingDbContextFactory(
        DbContextOptions<SettingDbContext> options,
        Func<CancellationToken, Task>? beforeSave = null)
        : IDbContextFactory<SettingDbContext>
    {
        public SettingDbContext CreateDbContext() => new(options, beforeSave);

        public ValueTask<SettingDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CreateDbContext());
        }
    }

    private sealed class SettingDbContext(
        DbContextOptions<SettingDbContext> options,
        Func<CancellationToken, Task>? beforeSave = null)
        : DbContext(options)
    {
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (beforeSave is not null)
            {
                await beforeSave(cancellationToken);
            }

            return await base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleSettings();
    }

    private sealed class ThrowingCommitInterceptor(Func<Exception> failure) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) => throw failure();
    }

    private sealed class ThrowingCommandInterceptor(Func<Exception> failure) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) => throw failure();
    }

    private sealed class SqlStateDbException(string sqlState, string message) : DbException(message)
    {
        public override string? SqlState => sqlState;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
