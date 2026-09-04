namespace ServiceMantle.Consul;

/// <summary>Contains finite, value-free Consul configuration and ownership failure categories.</summary>
public enum ConsulConfigurationError
{
    /// <summary>No complete setting snapshot is active.</summary>
    SnapshotUnavailable,
    /// <summary>The snapshot identity, schema, or enabled values are invalid.</summary>
    InvalidConfiguration,
    /// <summary>The configured client factory failed.</summary>
    ClientCreationFailed,
    /// <summary>The owned client could not be disposed.</summary>
    ClientDisposalFailed
}

/// <summary>Reports only a finite category, without setting values or an inner exception.</summary>
public sealed class ConsulConfigurationException : Exception
{
    internal ConsulConfigurationException(ConsulConfigurationError error)
        : base($"The Consul client boundary failed: {error}.") => Error = error;

    /// <summary>Gets the safe failure category.</summary>
    public ConsulConfigurationError Error { get; }
}
