using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Sqlite;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Data;

namespace ServiceMantle.ReferenceService.Database.Sqlite;

/// <summary>
/// Registers the sample's opt-in SQLite startup deployment gate on the consuming service's own
/// container, using public ServiceMantle provider and orchestration APIs only.
/// </summary>
public static class ReferenceSqliteStartupRegistration
{
    /// <summary>
    /// Adds the gate when it is explicitly switched on, and returns the fixed inputs it will use.
    /// </summary>
    /// <param name="services">The consuming service's container.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The explicit inputs, or null when the gate is off.</returns>
    /// <exception cref="InvalidOperationException">
    /// The gate is on and an input is missing or unusable. Nothing is registered in that case.
    /// </exception>
    public static ReferenceSqliteStartupOptions? AddReferenceSqliteStartup(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReferenceSqliteStartupOptions.Read(configuration);
        if (options is null)
        {
            return null;
        }

        services.AddSingleton(options);

        // Registering the bootstrap provider does not by itself declare a preparation or a
        // deployment capability: each one is registered explicitly, and all three registries share
        // the one provider-id resolver snapshot this registry owns.
        services.AddSingleton<IBootstrapDatabaseProvider, SqliteBootstrapDatabaseProvider>();
        services.AddSingleton(provider => new BootstrapDatabaseProviderRegistry(
            provider.GetServices<IBootstrapDatabaseProvider>()));
        services.AddSingleton<SqliteDatabaseTargetPreparationProvider>();
        services.AddSingleton<IDatabaseTargetPreparationProvider>(provider =>
            provider.GetRequiredService<SqliteDatabaseTargetPreparationProvider>());
        services.AddSingleton<IDatabaseDeploymentCapabilityProvider>(provider =>
            provider.GetRequiredService<SqliteDatabaseTargetPreparationProvider>());
        services.AddSingleton(provider => new DatabaseTargetPreparationProviderRegistry(
            provider.GetServices<IDatabaseTargetPreparationProvider>(),
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.AddSingleton(provider => new DatabaseDeploymentCapabilityRegistry(
            provider.GetServices<IDatabaseDeploymentCapabilityProvider>(),
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));

        // No distributed lease is requested or registered: the single-instance orchestration
        // serializes the canonical target inside this process and promises nothing beyond it.
        services.AddSingleton(provider => new DatabaseMigrationLockProviderRegistry(
            providers: null,
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));

        services.AddScoped<IDatabaseMigrationExecutor>(provider => new ReferenceSqliteMigrationExecutor(
            provider.GetRequiredService<ReferenceDbContext>(),
            provider.GetRequiredService<ReferenceSqliteStartupOptions>()));
        services.AddSingleton<ReferenceSqliteStartupCoordinator>();
        services.AddSingleton<ReferenceSqliteStartupHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<ReferenceSqliteStartupHostedService>());
        return options;
    }
}
