using ServiceMantle.Health;

namespace ServiceMantle.AspNetCore.Health;

/// <summary>
/// Supplies one caller-owned, read-only health snapshot without performing setup or migration.
/// </summary>
public interface IServiceHealthSnapshotSource
{
    /// <summary>Reads one immutable snapshot for the current request.</summary>
    ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);
}
