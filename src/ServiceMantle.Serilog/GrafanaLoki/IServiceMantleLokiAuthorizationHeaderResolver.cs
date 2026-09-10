namespace ServiceMantle.Serilog.GrafanaLoki;

/// <summary>Resolves a runtime authorization header value from a non-secret name.</summary>
public interface IServiceMantleLokiAuthorizationHeaderResolver
{
    /// <summary>Resolves the complete Authorization header value for the supplied name.</summary>
    /// <param name="name">The non-secret resolver entry name.</param>
    /// <returns>The header value, or <see langword="null"/> when no value is available.</returns>
    string? ResolveAuthorizationHeader(string name);
}
