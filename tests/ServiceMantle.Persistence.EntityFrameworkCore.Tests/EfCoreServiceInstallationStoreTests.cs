using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ServiceMantle.Installation;
using ServiceMantle.Persistence.EntityFrameworkCore;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests;

public sealed class EfCoreServiceInstallationStoreTests
{
    [Fact]
    public async Task ModelBuilder_configures_expected_columns_and_constraints()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var entityType = context.Model.FindEntityType(typeof(ServiceInstallationEntity));
        Assert.NotNull(entityType);
        Assert.Equal("service_installations", entityType!.GetTableName());
        Assert.Equal(nameof(ServiceInstallationEntity.ServiceId), entityType.FindPrimaryKey()!.Properties.Single().Name);

        var serviceId = entityType.FindProperty(nameof(ServiceInstallationEntity.ServiceId));
        Assert.Equal("service_id", serviceId!.GetColumnName());
        Assert.Equal(128, serviceId!.GetMaxLength());
        Assert.NotEqual("BLOB", serviceId.GetColumnType(), StringComparer.OrdinalIgnoreCase);
        Assert.Null(serviceId.GetDefaultValueSql());
        Assert.Null(serviceId.GetComputedColumnSql());

        var statusProperty = entityType.FindProperty(nameof(ServiceInstallationEntity.Status));
        Assert.Equal("status", statusProperty!.GetColumnName());
        Assert.False(statusProperty!.IsNullable);
        Assert.False(statusProperty.IsConcurrencyToken);

        var versionProperty = entityType.FindProperty(nameof(ServiceInstallationEntity.Version));
        Assert.Equal("version", versionProperty!.GetColumnName());
        Assert.True(versionProperty!.IsConcurrencyToken);
        Assert.False(versionProperty.IsNullable);
    }

    [Fact]
    public async Task FindAsync_returns_null_for_missing_service_id()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(context);

        var result = await store.FindAsync(ServiceId.Parse("not-found"), TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindAsync_returns_existing_installation_state()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = serviceId.Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 01, 01, 00, 00, 00, DateTimeKind.Utc),
            CompletedAtUtc = null,
            Version = 1
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = CreateStore(context);
        var result = await store.FindAsync(serviceId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(serviceId, result!.ServiceId);
        Assert.Equal(InstallationStatus.PendingSetup, result.Status);
    }

    [Fact]
    public async Task FindAsync_does_not_mix_different_service_ids()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var otherService = ServiceId.Parse("other-service");
        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = ServiceId.Parse("alpha").Value,
            Status = InstallationStatus.Completed,
            CreatedAtUtc = new DateTime(2026, 01, 01, 00, 00, 00, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 01, 01, 00, 01, 00, DateTimeKind.Utc),
            Version = 2
        });
        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = otherService.Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 01, 01, 00, 02, 00, DateTimeKind.Utc),
            Version = 1
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = CreateStore(context);
        var result = await store.FindAsync(otherService, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(otherService, result!.ServiceId);
        Assert.Equal(InstallationStatus.PendingSetup, result.Status);
    }

    [Fact]
    public async Task CreatePendingAsync_creates_initial_state_with_expected_defaults()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        var fixedTime = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new EfCoreServiceInstallationStore<TestDbContext>(context, new FixedTimeProvider(fixedTime));

        var result = await store.CreatePendingAsync(serviceId, TestContext.Current.CancellationToken);

        Assert.Equal(InstallationStatus.PendingSetup, result.Status);

        var saved = await context.ServiceInstallations.AsNoTracking().SingleAsync(
            item => item.ServiceId == serviceId.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(fixedTime.UtcDateTime, saved.CreatedAtUtc);
        Assert.Null(saved.CompletedAtUtc);
        Assert.Equal(1, saved.Version);
    }

    [Fact]
    public async Task CreatePendingAsync_returns_existing_when_already_completed()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = serviceId.Value,
            Status = InstallationStatus.Completed,
            CreatedAtUtc = new DateTime(2026, 08, 01, 00, 00, 00, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 08, 01, 00, 01, 00, DateTimeKind.Utc),
            Version = 3
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = CreateStore(context);
        var result = await store.CreatePendingAsync(serviceId, TestContext.Current.CancellationToken);

        Assert.True(result.IsCompleted);
        Assert.Equal(InstallationStatus.Completed, result.Status);
    }

    [Fact]
    public async Task CreatePendingAsync_is_idempotent_under_concurrent_creation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var contextA = CreateContext(connection);
        await using var contextB = CreateContext(connection);
        await contextA.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        var fixedTime = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var storeA = new EfCoreServiceInstallationStore<TestDbContext>(contextA, new FixedTimeProvider(fixedTime));
        var storeB = new EfCoreServiceInstallationStore<TestDbContext>(contextB, new FixedTimeProvider(fixedTime));

        var createATask = storeA.CreatePendingAsync(serviceId, TestContext.Current.CancellationToken).AsTask();
        var createBTask = storeB.CreatePendingAsync(serviceId, TestContext.Current.CancellationToken).AsTask();
        var results = await Task.WhenAll(createATask, createBTask);

        Assert.All(results, item => Assert.Equal(InstallationStatus.PendingSetup, item.Status));

        var rowCount = await contextA.ServiceInstallations.CountAsync(
            item => item.ServiceId == serviceId.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, rowCount);
    }

    [Fact]
    public async Task CreatePendingAsync_detaches_competing_added_entity_after_unique_conflict()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var contextB = CreateContext(connection);
        await using var contextA = CreateContext(
            connection,
            beforeSaveChangesAsync: async _ =>
            {
                contextB.ServiceInstallations.Add(new ServiceInstallationEntity
                {
                    ServiceId = "signacore",
                    Status = InstallationStatus.PendingSetup,
                    CreatedAtUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
                    Version = 1
                });
                await contextB.SaveChangesAsync(TestContext.Current.CancellationToken);
            });
        await contextA.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        var store = CreateStore(contextA);
        var result = await store.CreatePendingAsync(serviceId, TestContext.Current.CancellationToken);

        Assert.Equal(InstallationStatus.PendingSetup, result.Status);
        Assert.DoesNotContain(
            contextA.ChangeTracker.Entries<ServiceInstallationEntity>(),
            entry => entry.Entity.ServiceId == serviceId.Value && entry.State == EntityState.Added);

        contextA.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = "other-service",
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            Version = 1
        });
        await contextA.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            1,
            await contextA.ServiceInstallations.CountAsync(
                item => item.ServiceId == serviceId.Value,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreatePendingAsync_respects_cancellation_token()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = CreateStore(context);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.CreatePendingAsync(ServiceId.Parse("signacore"), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task MarkCompletedAsync_refuses_to_complete_a_pending_installation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var created = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var serviceId = ServiceId.Parse("signacore");
        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = serviceId.Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = created.UtcDateTime,
            Version = 1
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var completedTime = new DateTimeOffset(2026, 8, 1, 10, 5, 0, TimeSpan.Zero);
        var store = new EfCoreServiceInstallationStore<TestDbContext>(context, new FixedTimeProvider(completedTime));
        var exception = await Assert.ThrowsAsync<ServiceInstallationStoreException>(() =>
            store.MarkCompletedAsync(serviceId, TestContext.Current.CancellationToken).AsTask());

        // Completing a pending installation must consume its Setup Code, so this entry point cannot
        // bypass validation any more.
        Assert.Equal("installation.setup_code_required", exception.ErrorCode);

        var entity = await context.ServiceInstallations.AsNoTracking().SingleAsync(
            item => item.ServiceId == serviceId.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(InstallationStatus.PendingSetup, entity.Status);
        Assert.Null(entity.CompletedAtUtc);
        Assert.Equal(1, entity.Version);
    }
    [Fact]
    public async Task MarkCompletedAsync_is_idempotent_for_completed_state()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        var completedAt = new DateTime(2026, 8, 1, 10, 1, 0, DateTimeKind.Utc);
        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = serviceId.Value,
            Status = InstallationStatus.Completed,
            CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = completedAt,
            Version = 4
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = CreateStore(context);
        var first = await store.MarkCompletedAsync(serviceId, TestContext.Current.CancellationToken);
        var second = await store.MarkCompletedAsync(serviceId, TestContext.Current.CancellationToken);

        Assert.True(first.IsCompleted);
        Assert.True(second.IsCompleted);

        var row = await context.ServiceInstallations.AsNoTracking().SingleAsync(
            item => item.ServiceId == serviceId.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(4, row.Version);
    }

    [Fact]
    public async Task MarkCompletedAsync_throws_for_missing_record()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var store = CreateStore(context);
        var ex = await Assert.ThrowsAsync<ServiceInstallationStoreException>(() =>
            store.MarkCompletedAsync(ServiceId.Parse("signacore"), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("installation.not_found", ex.ErrorCode);
    }

    [Fact]
    public async Task MarkCompletedAsync_refuses_concurrent_pending_completion_without_data_loss()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var contextA = CreateContext(connection);
        await using var contextB = CreateContext(connection);
        await contextA.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        contextA.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = serviceId.Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc),
            Version = 5
        });
        await contextA.SaveChangesAsync(TestContext.Current.CancellationToken);

        var fixedTime = new DateTimeOffset(2026, 8, 1, 11, 5, 0, TimeSpan.Zero);
        var storeA = new EfCoreServiceInstallationStore<TestDbContext>(contextA, new FixedTimeProvider(fixedTime));
        var storeB = new EfCoreServiceInstallationStore<TestDbContext>(contextB, new FixedTimeProvider(fixedTime));

        foreach (var store in new[] { storeA, storeB })
        {
            var exception = await Assert.ThrowsAsync<ServiceInstallationStoreException>(() =>
                store.MarkCompletedAsync(serviceId, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal("installation.setup_code_required", exception.ErrorCode);
        }

        var entity = await contextA.ServiceInstallations.AsNoTracking().SingleAsync(
            item => item.ServiceId == serviceId.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(InstallationStatus.PendingSetup, entity.Status);
        Assert.Equal(5, entity.Version);
        Assert.Equal(
            1,
            await contextA.ServiceInstallations.CountAsync(
                item => item.ServiceId == serviceId.Value,
                TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task MarkCompletedAsync_leaves_no_tracked_entry_behind_for_a_pending_installation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = serviceId.Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc),
            Version = 1
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var store = new EfCoreServiceInstallationStore<TestDbContext>(
            context,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 1, 11, 5, 0, TimeSpan.Zero)));
        var exception = await Assert.ThrowsAsync<ServiceInstallationStoreException>(() =>
            store.MarkCompletedAsync(serviceId, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("installation.setup_code_required", exception.ErrorCode);
        Assert.DoesNotContain(
            context.ChangeTracker.Entries<ServiceInstallationEntity>(),
            entry => entry.Entity.ServiceId == serviceId.Value);

        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = "other-service",
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc),
            Version = 1
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await context.ServiceInstallations.AsNoTracking().SingleAsync(
            item => item.ServiceId == serviceId.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(InstallationStatus.PendingSetup, stored.Status);
        Assert.Equal(1, stored.Version);
    }
    [Fact]
    public async Task Entity_state_validation_rejects_pending_completion_timestamp()
    {
        var exception = await ReadInvalidEntityAsync(new ServiceInstallationEntity
        {
            ServiceId = "signacore",
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 8, 1, 10, 1, 0, DateTimeKind.Utc),
            Version = 1
        });

        Assert.Equal("installation.state_invariant_violation", exception.ErrorCode);
    }

    [Fact]
    public async Task Entity_state_validation_rejects_completed_without_completion_timestamp()
    {
        var exception = await ReadInvalidEntityAsync(new ServiceInstallationEntity
        {
            ServiceId = "signacore",
            Status = InstallationStatus.Completed,
            CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            Version = 1
        });

        Assert.Equal("installation.state_invariant_violation", exception.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Entity_state_validation_rejects_non_positive_version(int version)
    {
        var exception = await ReadInvalidEntityAsync(new ServiceInstallationEntity
        {
            ServiceId = "signacore",
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            Version = version
        });

        Assert.Equal("installation.state_invariant_violation", exception.ErrorCode);
    }

    [Fact]
    public async Task Entity_state_validation_rejects_default_creation_time()
    {
        var exception = await ReadInvalidEntityAsync(new ServiceInstallationEntity
        {
            ServiceId = "signacore",
            Status = InstallationStatus.PendingSetup,
            Version = 1
        });

        Assert.Equal("installation.state_invariant_violation", exception.ErrorCode);
    }

    [Fact]
    public void Entity_state_validation_rejects_invalid_service_id()
    {
        var exception = Assert.Throws<ServiceInstallationStoreException>(() =>
            ServiceInstallationEntityStateMapper.ConvertToState(new ServiceInstallationEntity
            {
                ServiceId = "not a service id",
                Status = InstallationStatus.PendingSetup,
                CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
                Version = 1
            }));

        Assert.Equal("installation.entity_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Entity_state_validation_rejects_non_canonical_service_id()
    {
        var exception = Assert.Throws<ServiceInstallationStoreException>(() =>
            ServiceInstallationEntityStateMapper.ConvertToState(new ServiceInstallationEntity
            {
                ServiceId = "SignaCore",
                Status = InstallationStatus.PendingSetup,
                CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
                Version = 1
            }));

        Assert.Equal("installation.entity_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Entity_state_validation_rejects_undefined_status()
    {
        var exception = Assert.Throws<ServiceInstallationStoreException>(() =>
            ServiceInstallationEntityStateMapper.ConvertToState(new ServiceInstallationEntity
            {
                ServiceId = "signacore",
                Status = (InstallationStatus)99,
                CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
                Version = 1
            }));

        Assert.Equal("installation.entity_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task Entity_state_validation_rejects_completion_before_creation()
    {
        var exception = await ReadInvalidEntityAsync(new ServiceInstallationEntity
        {
            ServiceId = "signacore",
            Status = InstallationStatus.Completed,
            CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 8, 1, 9, 59, 0, DateTimeKind.Utc),
            Version = 1
        });

        Assert.Equal("installation.state_invariant_violation", exception.ErrorCode);
    }

    [Fact]
    public async Task MarkCompletedAsync_rejects_early_completion_without_partial_changes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        var entity = new ServiceInstallationEntity
        {
            ServiceId = serviceId.Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            Version = 1
        };
        context.ServiceInstallations.Add(entity);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = new EfCoreServiceInstallationStore<TestDbContext>(
            context,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 1, 9, 59, 0, TimeSpan.Zero)));
        var exception = await Assert.ThrowsAsync<ServiceInstallationStoreException>(() =>
            store.MarkCompletedAsync(serviceId, TestContext.Current.CancellationToken).AsTask());

        // The pending row is refused before the clock check is ever reached.
        Assert.Equal("installation.setup_code_required", exception.ErrorCode);
        Assert.Equal(InstallationStatus.PendingSetup, entity.Status);
        Assert.Null(entity.CompletedAtUtc);
        Assert.Equal(1, entity.Version);

        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = "other-service",
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            Version = 1
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await context.ServiceInstallations.AsNoTracking().SingleAsync(
            item => item.ServiceId == serviceId.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(InstallationStatus.PendingSetup, stored.Status);
        Assert.Null(stored.CompletedAtUtc);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task MarkCompletedAsync_respects_cancellation_token()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var serviceId = ServiceId.Parse("signacore");
        context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = serviceId.Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc),
            Version = 1
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var source = new CancellationTokenSource();
        source.Cancel();
        var store = CreateStore(context);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.MarkCompletedAsync(serviceId, source.Token).AsTask());
    }

    [Fact]
    public async Task Multiple_service_ids_are_isolated_in_same_physical_database()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var alpha = ServiceId.Parse("alpha");
        var beta = ServiceId.Parse("beta");
        context.ServiceInstallations.AddRange(
            new ServiceInstallationEntity
            {
                ServiceId = alpha.Value,
                Status = InstallationStatus.PendingSetup,
                CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
                Version = 1
            },
            new ServiceInstallationEntity
            {
                ServiceId = beta.Value,
                Status = InstallationStatus.PendingSetup,
                CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
                Version = 1
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = CreateStore(context);
        var exception = await Assert.ThrowsAsync<ServiceInstallationStoreException>(() =>
            store.MarkCompletedAsync(alpha, TestContext.Current.CancellationToken).AsTask());

        // Refusing to complete alpha must leave beta entirely untouched.
        Assert.Equal("installation.setup_code_required", exception.ErrorCode);
        Assert.Equal(
            InstallationStatus.PendingSetup,
            (await context.ServiceInstallations.AsNoTracking().SingleAsync(
                item => item.ServiceId == alpha.Value,
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            InstallationStatus.PendingSetup,
            (await context.ServiceInstallations.AsNoTracking().SingleAsync(
                item => item.ServiceId == beta.Value,
                TestContext.Current.CancellationToken)).Status);
        Assert.Null(await store.FindAsync(ServiceId.Parse("gamma"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ToString_does_not_include_secrets_or_connection_information()
    {
        using var connection = new SqliteConnection("Data Source=file:smoke-test?mode=memory&cache=shared");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        const string connectionSecret = "Password=smoke-password";
        var entity = new ServiceInstallationEntity
        {
            ServiceId = ServiceId.Parse("signacore").Value,
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            Version = 1
        };
        context.ServiceInstallations.Add(entity);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCoreServiceInstallationStore<TestDbContext>(context);

        var result = await store.FindAsync(ServiceId.Parse("signacore"), TestContext.Current.CancellationToken);
        var stateText = result!.ToString();
        var entityText = entity.ToString();

        var missingStateException = await Assert.ThrowsAsync<ServiceInstallationStoreException>(() =>
            store.MarkCompletedAsync(ServiceId.Parse("other-service"), TestContext.Current.CancellationToken).AsTask());
        var exceptionText = missingStateException.ToString();

        Assert.DoesNotContain(connectionSecret, stateText, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionSecret, entityText, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionSecret, exceptionText, StringComparison.Ordinal);
        Assert.DoesNotContain("Mode=memory", connection.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelBuilder_can_be_invoked_multiple_times_without_duplicate_mapping()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection, invokeExtensionTwice: true);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var entityType = context.Model.FindEntityType(typeof(ServiceInstallationEntity));
        Assert.NotNull(entityType);
        Assert.Equal("service_installations", entityType!.GetTableName());
    }

    [Fact]
    public async Task ModelBuilder_reapplies_complete_mapping_after_consumer_primary_key_configuration()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection, configurePrimaryKeyFirst: true);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var entityType = context.Model.FindEntityType(typeof(ServiceInstallationEntity));
        Assert.NotNull(entityType);
        Assert.Equal("service_installations", entityType!.GetTableName());
        Assert.Equal(nameof(ServiceInstallationEntity.ServiceId), entityType.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal("service_id", entityType.FindProperty(nameof(ServiceInstallationEntity.ServiceId))!.GetColumnName());
        Assert.Equal(128, entityType.FindProperty(nameof(ServiceInstallationEntity.ServiceId))!.GetMaxLength());
        Assert.Equal("status", entityType.FindProperty(nameof(ServiceInstallationEntity.Status))!.GetColumnName());
        Assert.Equal("version", entityType.FindProperty(nameof(ServiceInstallationEntity.Version))!.GetColumnName());
        Assert.True(entityType.FindProperty(nameof(ServiceInstallationEntity.Version))!.IsConcurrencyToken);
    }

    private static async Task<ServiceInstallationStoreException> ReadInvalidEntityAsync(
        ServiceInstallationEntity entity)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.ServiceInstallations.Add(entity);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = CreateStore(context);
        return await Assert.ThrowsAsync<ServiceInstallationStoreException>(() =>
            store.FindAsync(ServiceId.Parse("signacore"), TestContext.Current.CancellationToken).AsTask());
    }

    private static EfCoreServiceInstallationStore<TestDbContext> CreateStore(TestDbContext context) =>
        new(context, TimeProvider.System);

    private static TestDbContext CreateContext(
        SqliteConnection connection,
        bool invokeExtensionTwice = false,
        bool configurePrimaryKeyFirst = false,
        Func<CancellationToken, Task>? beforeSaveChangesAsync = null) =>
        new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>()
                .UseSqlite(connection)
                .Options,
            invokeExtensionTwice,
            configurePrimaryKeyFirst,
            beforeSaveChangesAsync);

    private sealed class TestDbContext : DbContext, IServiceMantleDbContext
    {
        private readonly bool invokeExtensionTwice;
        private readonly bool configurePrimaryKeyFirst;
        private Func<CancellationToken, Task>? beforeSaveChangesAsync;

        public TestDbContext(
            DbContextOptions<TestDbContext> options,
            bool invokeExtensionTwice,
            bool configurePrimaryKeyFirst,
            Func<CancellationToken, Task>? beforeSaveChangesAsync)
            : base(options)
        {
            this.invokeExtensionTwice = invokeExtensionTwice;
            this.configurePrimaryKeyFirst = configurePrimaryKeyFirst;
            this.beforeSaveChangesAsync = beforeSaveChangesAsync;
        }

        public DbSet<ServiceInstallationEntity> ServiceInstallations { get; set; } = null!;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (beforeSaveChangesAsync is { } callback)
            {
                beforeSaveChangesAsync = null;
                await callback(cancellationToken);
            }

            return await base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            if (configurePrimaryKeyFirst)
            {
                modelBuilder.Entity<ServiceInstallationEntity>().HasKey(item => item.ServiceId);
            }

            modelBuilder.AddServiceMantleInstallation();
            if (invokeExtensionTwice)
            {
                modelBuilder.AddServiceMantleInstallation();
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
