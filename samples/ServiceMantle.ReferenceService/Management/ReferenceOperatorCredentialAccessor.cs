namespace ServiceMantle.ReferenceService.Management;

/// <summary>
/// The scoped holder for the credentials of the one login request being processed.
/// </summary>
/// <remarks>
/// The credentials exist only between the login adapter, which parses them out of the admitted
/// request body, and the identity provider, which consumes them inside the same request scope.
/// Nothing reads them afterwards, and they are never written to a log, a response, or an exception.
/// </remarks>
public sealed class ReferenceOperatorCredentialAccessor
{
    private (string Username, string Secret)? credentials;

    /// <summary>Replaces the held credentials. Called once by the login adapter.</summary>
    public void Set(string username, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        credentials = (username, secret);
    }

    /// <summary>Reads the held credentials, or false when none were set for this scope.</summary>
    public bool TryRead(out string username, out string secret)
    {
        (username, secret) = credentials ?? ("", "");
        return credentials is not null;
    }

    /// <summary>Discards the held credentials without echoing them.</summary>
    public void Clear() => credentials = null;
}
