namespace ServiceMantle.AspNetCore;

/// <summary>Configures the fixed management namespace and phase observation wait.</summary>
public sealed class ServiceMantlePhaseGateOptions
{
    /// <summary>Gets or sets the absolute management prefix, defaulting to /management.</summary>
    public string ManagementPathPrefix { get; set; } = "/management";
    /// <summary>Gets or sets the asynchronous observation timeout, between 50 ms and 30 seconds.</summary>
    public TimeSpan SnapshotTimeout { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Identifies a finite endpoint surface within the management namespace.</summary>
public enum ServiceMantleManagementSurface
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

internal sealed record ServiceMantleManagementSurfaceMetadata(ServiceMantleManagementSurface Surface);
internal sealed record ServiceMantlePhaseHealthMetadata(string Path);
