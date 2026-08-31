using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ServiceMantle.Logging;

namespace ServiceMantle.AspNetCore;

internal sealed class ServiceMantleSensitiveHeaderSanitizer
{
    internal ServiceMantleSensitiveHeaderSanitizer(ServiceMantleSensitiveHeaderRegistry registry)
    {
        Sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            DeniedHeaderNames = registry.GetRequiredSnapshot()
        });
    }

    internal StructuredLogSanitizer Sanitizer { get; }
}

/// <summary>
/// Creates fail-closed structured diagnostic projections of ASP.NET Core request Headers.
/// </summary>
public sealed class ServiceMantleRequestHeaderDiagnosticProjector
{
    private readonly StructuredLogSanitizer sanitizer;

    internal ServiceMantleRequestHeaderDiagnosticProjector(
        ServiceMantleSensitiveHeaderSanitizer ownedSanitizer)
    {
        sanitizer = ownedSanitizer.Sanitizer;
    }

    /// <summary>
    /// Projects request Headers into a new sanitized graph. Denied values become
    /// <c>[REDACTED]</c>; enumeration failures produce only a stable failure marker.
    /// </summary>
    /// <param name="headers">The request Header collection to project.</param>
    /// <returns>A new sink-neutral sanitized Header graph.</returns>
    public IReadOnlyDictionary<string, object?> Project(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return sanitizer.SanitizeHeaders(headers.Select(header =>
            new KeyValuePair<string, object?>(header.Key, ProjectValues(header.Value))));
    }

    private static object? ProjectValues(StringValues values) => values.Count switch
    {
        0 => Array.Empty<string>(),
        1 => values[0],
        _ => values.ToArray()
    };
}
