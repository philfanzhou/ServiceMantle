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
    /// Gets or sets the non-negative Setup Code issuance generation.
    /// </summary>
    /// <remarks>
    /// The counter starts at 0 and is a non-sensitive issuance history marker that is retained after
    /// completion, so material that was issued and then deleted can never look like a row that was
    /// never issued.
    /// </remarks>
    public int SetupCodeGeneration { get; set; }

    /// <summary>
    /// Gets or sets the versioned Setup Code digest, or null when no code is outstanding.
    /// </summary>
    public string? SetupCodeDigest { get; set; }

    /// <summary>
    /// Gets or sets the UTC Setup Code issuance timestamp.
    /// </summary>
    public DateTime? SetupCodeIssuedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the UTC Setup Code expiry timestamp.
    /// </summary>
    public DateTime? SetupCodeExpiresAtUtc { get; set; }

    /// <summary>
    /// Returns a safe projection for debugging.
    /// </summary>
    public override string ToString() =>
        $"ServiceInstallationEntity(ServiceId={ServiceId}, Status={Status}, Version={Version}, "
        + $"SetupCodeGeneration={SetupCodeGeneration})";
}

