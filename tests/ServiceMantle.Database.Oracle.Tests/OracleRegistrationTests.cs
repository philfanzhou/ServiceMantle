using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Oracle;
using ServiceMantle.Database.Oracle.Migration;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

public sealed class OracleRegistrationTests
{
    [Fact]
    public void Standard_registration_resolves_all_Oracle_capabilities_case_insensitively()
    {
        var services = new ServiceCollection();
        services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddBootstrapDatabaseProvider<OracleBootstrapDatabaseProvider>()
            .AddDatabaseTargetPreparationProvider<OracleDatabaseTargetPreparationProvider>()
            .AddMigrationLockProvider<OracleMigrationLockProvider>();

        using var serviceProvider = services.BuildServiceProvider();
        var bootstrapRegistry = serviceProvider.GetRequiredService<BootstrapDatabaseProviderRegistry>();
        var preparationRegistry = serviceProvider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();
        var migrationLockRegistry = serviceProvider.GetRequiredService<DatabaseMigrationLockProviderRegistry>();

        Assert.True(bootstrapRegistry.TryGetProvider("oracle", out var bootstrap));
        Assert.IsType<OracleBootstrapDatabaseProvider>(bootstrap);
        Assert.True(preparationRegistry.TryGetProvider("ORACLE", out var preparation));
        Assert.IsType<OracleDatabaseTargetPreparationProvider>(preparation);
        Assert.True(migrationLockRegistry.TryGetProvider("oracle", out var migrationLock));
        Assert.IsType<OracleMigrationLockProvider>(migrationLock);
    }

    [Fact]
    public void Bootstrap_registration_does_not_implicitly_add_preparation()
    {
        var services = new ServiceCollection();
        services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddBootstrapDatabaseProvider<OracleBootstrapDatabaseProvider>();

        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();

        Assert.False(registry.TryGetProvider(WellKnownDatabaseProviderIds.Oracle, out var provider));
        Assert.Null(provider);
        Assert.Equal(
            "database_target_preparation.capability_not_supported",
            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported);

        var migrationRegistry = serviceProvider.GetRequiredService<DatabaseMigrationLockProviderRegistry>();
        Assert.False(migrationRegistry.TryGetProvider(
            WellKnownDatabaseProviderIds.Oracle,
            out var migrationLock));
        Assert.Null(migrationLock);
        Assert.Equal("migration.lock_not_supported", WellKnownMigrationErrorCodes.LockNotSupported);
    }
}
