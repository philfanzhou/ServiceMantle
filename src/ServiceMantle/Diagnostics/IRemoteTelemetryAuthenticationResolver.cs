namespace ServiceMantle.Diagnostics;

/// <summary>Resolves a remote telemetry authentication header by a non-secret configuration name.</summary>
public interface IRemoteTelemetryAuthenticationResolver
{
    /// <summary>Attempts to resolve one authentication header.</summary>
    bool TryResolve(string name, out RemoteTelemetryAuthenticationHeader? header);
}
