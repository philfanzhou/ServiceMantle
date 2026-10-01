using ServiceMantle.Logging;

namespace ServiceMantle.Logging.Remote;

/// <summary>
/// A <see cref="IRemoteLogAuthorizationResolver"/> that answers exactly one entry name with one
/// fixed Authorization header value captured at registration time.
/// </summary>
/// <remarks>
/// The value is held in memory only and never rendered; <see cref="ToString"/> returns the entry
/// name alone. Every other resolver name resolves to null.
/// </remarks>
public sealed class FixedRemoteLogAuthorizationResolver : IRemoteLogAuthorizationResolver
{
    private readonly string name;
    private readonly string? value;

    /// <summary>Creates a resolver answering <paramref name="name"/> with <paramref name="value"/>.</summary>
    /// <param name="name">The non-secret resolver entry name.</param>
    /// <param name="value">The Authorization header value, or null to resolve nothing.</param>
    public FixedRemoteLogAuthorizationResolver(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
        this.value = value;
    }

    /// <inheritdoc />
    public string? ResolveAuthorizationHeader(string resolverName) =>
        string.Equals(resolverName, name, StringComparison.Ordinal) ? value : null;

    /// <summary>Returns the entry name only; never the Authorization value.</summary>
    public override string ToString() => $"FixedRemoteLogAuthorizationResolver(Name={name})";
}
