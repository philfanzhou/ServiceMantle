using Microsoft.EntityFrameworkCore;
using ServiceMantle.Persistence.Relational.Mapping;

namespace ServiceMantle.Persistence.Relational;

/// <summary>
/// Contract that a business DbContext must implement for ServiceMantle installation persistence.
/// </summary>
public interface IServiceDbContext
{
    /// <summary>
    /// Gets installation entities.
    /// </summary>
    DbSet<ServiceInstallationEntity> ServiceInstallations { get; }

    /// <summary>
    /// Saves changes using the current unit of work.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

