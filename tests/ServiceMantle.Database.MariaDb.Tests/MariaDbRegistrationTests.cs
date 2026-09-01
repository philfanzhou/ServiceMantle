using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MariaDb;
using ServiceMantle.Database.MariaDb.Migration;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.MariaDb.Tests;

public sealed class MariaDbRegistrationTests
{
    [Fact]
    public void Explicit_registration_resolves_only_the_MariaDb_capabilities()
    {
        var services = new ServiceCollection();
        services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddBootstrapDatabaseProvider<MariaDbBootstrapDatabaseProvider>()
            .AddDatabaseTargetPreparationProvider<MariaDbDatabaseTargetPreparationProvider>()
            .AddMigrationLockProvider<MariaDbMigrationLockProvider>();

        using var serviceProvider = services.BuildServiceProvider();
        var bootstrapRegistry = serviceProvider.GetRequiredService<BootstrapDatabaseProviderRegistry>();
        var preparationRegistry = serviceProvider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();
        var lockRegistry = serviceProvider.GetRequiredService<DatabaseMigrationLockProviderRegistry>();

        Assert.True(bootstrapRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MariaDb, out var bootstrap));
        Assert.IsType<MariaDbBootstrapDatabaseProvider>(bootstrap);
        Assert.True(preparationRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MariaDb, out var preparation));
        Assert.IsType<MariaDbDatabaseTargetPreparationProvider>(preparation);
        Assert.True(lockRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MariaDb, out var migrationLock));
        Assert.IsType<MariaDbMigrationLockProvider>(migrationLock);

        Assert.False(bootstrapRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MySql, out _));
        Assert.False(preparationRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MySql, out _));
        Assert.False(lockRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MySql, out _));
    }

    [Fact]
    public void Unregistered_MariaDb_preparation_capability_fails_closed()
    {
        var services = new ServiceCollection();
        services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"));

        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();

        Assert.False(registry.TryGetProvider(WellKnownDatabaseProviderIds.MariaDb, out var provider));
        Assert.Null(provider);
        Assert.Equal(
            "database_target_preparation.capability_not_supported",
            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported);
    }
}
