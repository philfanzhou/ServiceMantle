namespace ServiceMantle.AspNetCore.PhaseGate;

/// <summary>Identifies a finite endpoint surface within the management namespace.</summary>
public enum ManagementSurface
{
    /// <summary>Read-only GET/HEAD endpoints below /status, available in every phase.</summary>
    Status,
    /// <summary>Initial configuration endpoints below /bootstrap.</summary>
    Bootstrap,
    /// <summary>One-time installation endpoints below /setup.</summary>
    Setup,
    /// <summary>Other management endpoints, available only after successful startup.</summary>
    Management
}

internal sealed record ManagementSurfaceMetadata(ManagementSurface Surface);
