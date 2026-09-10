namespace ServiceMantle.AspNetCore.Logging;

/// <summary>Configures additional request Header names whose values are always redacted.</summary>
public sealed class SensitiveHeadersOptions
{
    /// <summary>
    /// Gets or sets additional HTTP token Header names. Built-in authentication, cookie, and API-key
    /// Header names cannot be removed.
    /// </summary>
    public IEnumerable<string> DeniedHeaderNames { get; set; } = [];
}
