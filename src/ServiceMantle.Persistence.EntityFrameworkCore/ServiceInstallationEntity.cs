using ServiceMantle.Installation;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// Entity storing service installation state in a shared business database.
/// </summary>
public sealed class ServiceInstallationEntity
{
    /// <summary>
    /// Gets or sets the canonical service identifier.
    /// </summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the installation status.
    /// </summary>
    public InstallationStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the UTC creation timestamp.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the UTC completion timestamp.
    /// </summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the optimistic concurrency version.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Returns a safe projection for debugging.
    /// </summary>
    public override string ToString() =>
        $"ServiceInstallationEntity(ServiceId={ServiceId}, Status={Status}, Version={Version})";
}

