using System.Collections.Frozen;

namespace ServiceMantle.Logging;

/// <summary>Defines immutable built-in policy values used by structured log sanitization.</summary>
public static class StructuredLogSanitizerDefaults
{
    /// <summary>
    /// Gets the authentication, cookie, and API-key Header names that are always denied.
    /// Configured allow rules cannot remove these names.
    /// </summary>
    public static IReadOnlySet<string> BuiltInDeniedHeaderNames { get; } = new[]
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "X-Auth-Token"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
