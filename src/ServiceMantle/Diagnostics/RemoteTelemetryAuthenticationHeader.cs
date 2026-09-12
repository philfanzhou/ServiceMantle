namespace ServiceMantle.Diagnostics;

/// <summary>Contains one resolved remote telemetry authentication header.</summary>
public sealed class RemoteTelemetryAuthenticationHeader
{
    /// <summary>Initializes a resolved authentication header.</summary>
    public RemoteTelemetryAuthenticationHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        Value = value;
    }

    /// <summary>Gets the HTTP header name.</summary>
    public string Name { get; }

    /// <summary>Gets the secret HTTP header value.</summary>
    public string Value { get; }

    /// <summary>Returns metadata only and never includes the header value.</summary>
    public override string ToString() => "RemoteTelemetryAuthenticationHeader(Resolved=True)";
}
