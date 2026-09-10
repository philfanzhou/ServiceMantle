using ServiceMantle.AspNetCore.PhaseGate;

namespace ServiceMantle.AspNetCore.ManagementApi;

/// <summary>
/// Configures the versioned root of the protected ServiceMantle management API v1 group.
/// </summary>
/// <remarks>
/// The values are forwarded to the existing phase gate, which owns path normalization, the 128
/// character limit, the permitted character set, and the <c>/health</c> conflict rules. A host that
/// also registers the phase gate explicitly must use settings that normalize to exactly these ones.
/// </remarks>
public sealed class ManagementApiOptions
{
    /// <summary>
    /// Gets or sets the absolute versioned management root, defaulting to <c>/management/v1</c>.
    /// </summary>
    /// <remarks>
    /// The root must end with an independent <see cref="ManagementApiDefaults.VersionSegment"/>
    /// segment, for example <c>/ops/admin/v1</c>. Any other value fails before the host starts.
    /// </remarks>
    public string RootPath { get; set; } = ManagementApiDefaults.DefaultRootPath;

    /// <summary>
    /// Gets or sets the phase gate's asynchronous observation timeout for this root.
    /// </summary>
    /// <remarks>
    /// This is the existing <see cref="PhaseGateOptions.SnapshotTimeout"/>; it is
    /// exposed here so that a host does not have to register the phase gate a second time to change
    /// it. The permitted range is owned by the phase gate.
    /// </remarks>
    public TimeSpan SnapshotTimeout { get; set; } = new PhaseGateOptions().SnapshotTimeout;
}
