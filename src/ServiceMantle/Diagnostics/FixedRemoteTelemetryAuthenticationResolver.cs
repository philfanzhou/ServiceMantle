namespace ServiceMantle.Diagnostics;

/// <summary>Resolves one exact non-secret name to an immutable fixed telemetry authentication header.</summary>
/// <remarks>The value is held in memory and only exposed through a successful header.Value.
/// Register explicitly as IRemoteTelemetryAuthenticationResolver. No configuration or environment
/// lookup, rotation, memory erasure or protection against caller logging/serialization is provided.</remarks>
public sealed class FixedRemoteTelemetryAuthenticationResolver : IRemoteTelemetryAuthenticationResolver
{
    private readonly string name;
    private readonly RemoteTelemetryAuthenticationHeader header;

    /// <summary>Captures an ordinal resolver name, HTTP token header name and nonempty value without CR/LF.</summary>
    public FixedRemoteTelemetryAuthenticationResolver(string name, string headerName, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(headerName);
        ArgumentNullException.ThrowIfNull(value);
        if (name.Length is < 1 or > 128 || !name.All(c =>
            c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
            throw new ArgumentException("The telemetry resolver name is invalid.", nameof(name));
        if (headerName.Length is < 1 or > 128 || !headerName.All(c =>
            char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
            throw new ArgumentException("The telemetry header name is invalid.", nameof(headerName));
        if (value.Length == 0 || value.Any(c => c is '\r' or '\n'))
            throw new ArgumentException("The telemetry header value is invalid.", nameof(value));
        this.name = name;
        header = new RemoteTelemetryAuthenticationHeader(headerName, value);
    }

    /// <inheritdoc />
    public bool TryResolve(string name, out RemoteTelemetryAuthenticationHeader? header)
    {
        header = string.Equals(this.name, name, StringComparison.Ordinal) ? this.header : null;
        return header is not null;
    }

    /// <summary>Returns fixed metadata without captured names or values.</summary>
    public override string ToString() => "FixedRemoteTelemetryAuthenticationResolver(Fixed=True)";
}
