using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>Connects ASP.NET Core Data Protection to ServiceMantle EF Core persistence.</summary>
public static class ServiceMantleDataProtectionBuilderExtensions
{
    /// <summary>
    /// Persists this service's Data Protection keys through a dedicated EF Core context and an
    /// external Bootstrap root key.
    /// </summary>
    /// <typeparam name="TDbContext">The consuming business DbContext type.</typeparam>
    /// <param name="builder">The Data Protection builder.</param>
    /// <param name="serviceId">The service that owns the isolated key ring.</param>
    /// <param name="rootKeyResolver">
    /// A callback that obtains the external root key from the dependency injection container.
    /// </param>
    /// <returns>The same builder for fluent use.</returns>
    public static IDataProtectionBuilder PersistKeysToServiceMantleEfCore<TDbContext>(
        this IDataProtectionBuilder builder,
        ServiceId serviceId,
        Func<IServiceProvider, string> rootKeyResolver)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(rootKeyResolver);

        builder.Services.AddSingleton(serviceProvider =>
            new EfCoreDataProtectionKeyRepository<TDbContext>(
                serviceProvider.GetRequiredService<IDbContextFactory<TDbContext>>(),
                serviceId,
                () => rootKeyResolver(serviceProvider)));
        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<EfCoreDataProtectionKeyRepository<TDbContext>>(
                (options, repository) => options.XmlRepository = repository);

        return builder;
    }
}
