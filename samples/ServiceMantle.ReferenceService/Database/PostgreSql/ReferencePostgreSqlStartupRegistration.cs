using Microsoft.EntityFrameworkCore;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Installation;
using ServiceMantle.Migration;
using ServiceMantle.Persistence.EntityFrameworkCore;

namespace ServiceMantle.ReferenceService.Database.PostgreSql;

/// <summary>
/// Registers the sample's opt-in PostgreSQL startup deployment gate on the consuming service's
/// own container, using public ServiceMantle provider and orchestration APIs only.
/// </summary>
public static class ReferencePostgreSqlStartupRegistration
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
    public static ReferencePostgreSqlStartupOptions? AddReferencePostgreSqlStartup(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReferencePostgreSqlStartupOptions.Read(configuration);
        if (options is null)
        {
            return null;
        }

        return services.AddReferencePostgreSqlStartup(options);
    }

    /// <summary>Adds the gate for already-read, fixed inputs.</summary>
    /// <param name="services">The consuming service's container.</param>
    /// <param name="options">The explicit inputs read before any registration.</param>
    /// <returns>The inputs that were registered.</returns>
    public static ReferencePostgreSqlStartupOptions AddReferencePostgreSqlStartup(
        this IServiceCollection services,
        ReferencePostgreSqlStartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);

        // The bootstrap, preparation, and lock registries share the one provider-id resolver
        // snapshot this registry owns. Registering the bootstrap provider declares neither a
        // preparation capability nor a deployment capability: PostgreSQL deliberately declares no
        // deployment capability, because the gate always orchestrates under the real advisory
        // lock instead of a deployment-mode overload.
        services.AddSingleton<IBootstrapDatabaseProvider, PostgreSqlBootstrapDatabaseProvider>();
        services.AddSingleton(provider => new BootstrapDatabaseProviderRegistry(
            provider.GetServices<IBootstrapDatabaseProvider>()));
        services.AddSingleton<PostgreSqlDatabaseTargetPreparationProvider>();
        services.AddSingleton<IDatabaseTargetPreparationProvider>(provider =>
            provider.GetRequiredService<PostgreSqlDatabaseTargetPreparationProvider>());
        services.AddSingleton(provider => new DatabaseTargetPreparationProviderRegistry(
            provider.GetServices<IDatabaseTargetPreparationProvider>(),
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));

        // PostgreSQL is serialized by the real session-level advisory lock, not by a process-local
        // turn: concurrent hosts on the same target exclude each other through the server.
        services.AddSingleton(provider => new DatabaseMigrationLockProviderRegistry(
            [new PostgreSqlMigrationLockProvider()],
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));

        // The runtime context, the installation store, the schema executor's inspection
        // connection, and the composite initialization executor all share one orchestration scope
        // and the target connection string. The administrative connection is never registered:
        // it exists only inside the coordinator's single preparation request.
        services.AddDbContext<ReferencePostgreSqlDbContext>(dbContextOptions =>
            dbContextOptions.UseNpgsql(options.TargetConnectionString));
        services.AddScoped<IServiceInstallationStore>(provider =>
            new EfCoreServiceInstallationStore<ReferencePostgreSqlDbContext>(
                provider.GetRequiredService<ReferencePostgreSqlDbContext>()));
        services.AddScoped<IDatabaseMigrationExecutor>(provider => new ReferencePostgreSqlInstallationInitializationExecutor(
            provider.GetRequiredService<ReferencePostgreSqlDbContext>(),
            new ReferencePostgreSqlMigrationExecutor(
                provider.GetRequiredService<ReferencePostgreSqlDbContext>(),
                provider.GetRequiredService<ReferencePostgreSqlStartupOptions>().TargetConnectionString),
            provider.GetRequiredService<IServiceInstallationStore>(),
            provider.GetRequiredService<ServiceId>()));
        services.AddSingleton<ReferencePostgreSqlStartupCoordinator>();
        services.AddSingleton<ReferencePostgreSqlStartupHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<ReferencePostgreSqlStartupHostedService>());
        return options;
    }
}
