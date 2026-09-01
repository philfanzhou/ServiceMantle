using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.SqlServer;
using ServiceMantle.Database.SqlServer.Migration;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.SqlServer.Tests;

public sealed class SqlServerRegistrationTests
{
    [Fact]
    public void Explicit_registration_resolves_only_SQL_Server_capabilities()
    {
        var services = new ServiceCollection();
        services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddBootstrapDatabaseProvider<SqlServerBootstrapDatabaseProvider>()
            .AddDatabaseTargetPreparationProvider<SqlServerDatabaseTargetPreparationProvider>()
            .AddMigrationLockProvider<SqlServerMigrationLockProvider>();

        using var serviceProvider = services.BuildServiceProvider();
        var bootstrapRegistry = serviceProvider.GetRequiredService<BootstrapDatabaseProviderRegistry>();
        var preparationRegistry = serviceProvider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();
        var lockRegistry = serviceProvider.GetRequiredService<DatabaseMigrationLockProviderRegistry>();

        Assert.True(bootstrapRegistry.TryGetProvider(WellKnownDatabaseProviderIds.SqlServer, out var bootstrap));
        Assert.IsType<SqlServerBootstrapDatabaseProvider>(bootstrap);
        Assert.True(preparationRegistry.TryGetProvider(WellKnownDatabaseProviderIds.SqlServer, out var preparation));
        Assert.IsType<SqlServerDatabaseTargetPreparationProvider>(preparation);
        Assert.True(lockRegistry.TryGetProvider(WellKnownDatabaseProviderIds.SqlServer, out var migrationLock));
        Assert.IsType<SqlServerMigrationLockProvider>(migrationLock);

        Assert.False(bootstrapRegistry.TryGetProvider(WellKnownDatabaseProviderIds.PostgreSql, out _));
        Assert.False(preparationRegistry.TryGetProvider(WellKnownDatabaseProviderIds.PostgreSql, out _));
        Assert.False(lockRegistry.TryGetProvider(WellKnownDatabaseProviderIds.PostgreSql, out _));
    }

    [Fact]
    public void Unregistered_SQL_Server_preparation_capability_fails_closed()
    {
        var services = new ServiceCollection();
        services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"));

        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();

        Assert.False(registry.TryGetProvider(WellKnownDatabaseProviderIds.SqlServer, out var provider));
        Assert.Null(provider);
        Assert.Equal(
            "database_target_preparation.capability_not_supported",
            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported);
    }
}
