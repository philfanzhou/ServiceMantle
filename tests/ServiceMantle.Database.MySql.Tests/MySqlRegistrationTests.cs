using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MySql;
using ServiceMantle.Database.MySql.Migration;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.MySql.Tests;

public sealed class MySqlRegistrationTests
{
    [Fact]
    public void Explicit_registration_resolves_only_the_MySql_capabilities()
    {
        var services = new ServiceCollection();
        services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddBootstrapDatabaseProvider<MySqlBootstrapDatabaseProvider>()
            .AddDatabaseTargetPreparationProvider<MySqlDatabaseTargetPreparationProvider>()
            .AddMigrationLockProvider<MySqlMigrationLockProvider>();

        using var serviceProvider = services.BuildServiceProvider();
        var bootstrapRegistry = serviceProvider.GetRequiredService<BootstrapDatabaseProviderRegistry>();
        var preparationRegistry = serviceProvider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();
        var lockRegistry = serviceProvider.GetRequiredService<DatabaseMigrationLockProviderRegistry>();

        Assert.True(bootstrapRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MySql, out var bootstrap));
        Assert.IsType<MySqlBootstrapDatabaseProvider>(bootstrap);
        Assert.True(preparationRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MySql, out var preparation));
        Assert.IsType<MySqlDatabaseTargetPreparationProvider>(preparation);
        Assert.True(lockRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MySql, out var migrationLock));
        Assert.IsType<MySqlMigrationLockProvider>(migrationLock);

        Assert.False(bootstrapRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MariaDb, out _));
        Assert.False(preparationRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MariaDb, out _));
        Assert.False(lockRegistry.TryGetProvider(WellKnownDatabaseProviderIds.MariaDb, out _));
    }

    [Fact]
    public void Unregistered_MySql_preparation_capability_fails_closed()
    {
        var services = new ServiceCollection();
        services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"));

        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();

        Assert.False(registry.TryGetProvider(WellKnownDatabaseProviderIds.MySql, out var provider));
        Assert.Null(provider);
        Assert.Equal(
            "database_target_preparation.capability_not_supported",
            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported);
    }
}
