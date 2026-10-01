using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle;
using ServiceMantle.Health;
using ServiceMantle.Migration;

namespace ServiceMantle.Persistence.Relational;

/// <summary>
/// Registers the EF Core database health snapshot source for the ServiceMantle health
/// endpoints.
/// </summary>
public static class EfCoreHealthSnapshotServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scoped <see cref="IServiceHealthSnapshotSource"/> that reads the startup
    /// database gate's <see cref="StartupDatabaseReceipt"/> and probes the scoped
    /// <typeparamref name="TDbContext"/> database read-only.
    /// </summary>
    /// <typeparam name="TDbContext">The consuming business DbContext type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceId">
    /// The service identity; its normalized value is the error-code prefix when
    /// <paramref name="errorCodePrefix"/> is null.
    /// </param>
    /// <param name="failureClassifier">
    /// The provider-specific failure classifier; for PostgreSQL use
    /// <c>PostgreSqlDatabaseProbeFailureClassifier</c> from the
    /// <c>ServiceMantle.Database.PostgreSql</c> package.
    /// </param>
    /// <param name="probeMode">The probe strength; defaults to the mapped-schema probe.</param>
    /// <param name="errorCodePrefix">
    /// An explicit safe error-code prefix; when null, the prefix is derived from
    /// <paramref name="serviceId"/>.
    /// </param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// <para>
    /// The source resolves the <see cref="StartupDatabaseReceipt"/> registered by
    /// <c>AddStartupDatabaseGate</c> (ServiceMantle.Web) and the scoped
    /// <typeparamref name="TDbContext"/>, so both must be registered for resolution to succeed.
    /// The registration itself changes nothing until the health endpoints resolve the source.
    /// </para>
    /// <para>
    /// Invalid arguments fail here, at registration time, with stable English messages; the
    /// error-code prefix must keep every projected code within the snapshot contract's
    /// 128-character bound.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddServiceMantleEfCoreHealthSnapshotSource<TDbContext>(
        this IServiceCollection services,
        ServiceId serviceId,
        IServiceDatabaseProbeFailureClassifier failureClassifier,
        EfCoreHealthSnapshotProbeMode probeMode = EfCoreHealthSnapshotProbeMode.MappedSchema,
        string? errorCodePrefix = null)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(failureClassifier);
        if (!Enum.IsDefined(probeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(probeMode));
        }

        var resolvedPrefix = errorCodePrefix ?? serviceId.Value;
        if (errorCodePrefix is null &&
            resolvedPrefix.Length > EfCoreHealthSnapshotSource<TDbContext>.MaximumErrorCodePrefixLength)
        {
            throw new ArgumentException(
                "The service identifier is too long to derive a health error code prefix; " +
                "pass an explicit errorCodePrefix.",
                nameof(serviceId));
        }

        EfCoreHealthSnapshotSource<TDbContext>.ValidateErrorCodePrefix(resolvedPrefix);

        services.TryAddScoped<EfCoreHealthSnapshotSource<TDbContext>>(serviceProvider =>
            new EfCoreHealthSnapshotSource<TDbContext>(
                serviceProvider.GetRequiredService<StartupDatabaseReceipt>(),
                serviceProvider.GetRequiredService<TDbContext>(),
                failureClassifier,
                resolvedPrefix,
                probeMode));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IServiceHealthSnapshotSource,
            EfCoreHealthSnapshotSource<TDbContext>>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<EfCoreHealthSnapshotSource<TDbContext>>()));
        return services;
    }
}
