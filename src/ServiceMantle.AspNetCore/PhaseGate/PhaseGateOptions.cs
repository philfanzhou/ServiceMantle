namespace ServiceMantle.AspNetCore.PhaseGate;

/// <summary>Configures the fixed management namespace and phase observation wait.</summary>
public sealed class PhaseGateOptions
{
    /// <summary>Gets or sets the absolute management prefix, defaulting to /management.</summary>
    public string ManagementPathPrefix { get; set; } = "/management";
    /// <summary>Gets or sets the asynchronous observation timeout, between 50 ms and 30 seconds.</summary>
    public TimeSpan SnapshotTimeout { get; set; } = TimeSpan.FromSeconds(1);
}

internal sealed record PhaseHealthMetadata(string Path);
