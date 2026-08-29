using System.Diagnostics.CodeAnalysis;
using ServiceMantle.Http;

namespace Microsoft.AspNetCore.Http;

/// <summary>
/// Reads the Correlation ID established by the ServiceMantle Correlation ID middleware.
/// </summary>
public static class ServiceMantleCorrelationIdHttpContextExtensions
{
    /// <summary>
    /// Gets the Correlation ID the middleware resolved for this request.
    /// </summary>
    /// <param name="context">The current request context.</param>
    /// <returns>
    /// The resolved Correlation ID, or <see langword="null"/> when the middleware has not run for
    /// this request.
    /// </returns>
    /// <remarks>
    /// The value is only read back from the request slot; the request header is never parsed again,
    /// so this always returns the same value the response header and the log scope carry.
    /// </remarks>
    public static string? GetServiceMantleCorrelationId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CorrelationIdRequestSlot.Get(context);
    }

    /// <summary>
    /// Tries to get the Correlation ID the middleware resolved for this request.
    /// </summary>
    /// <param name="context">The current request context.</param>
    /// <param name="correlationId">The resolved Correlation ID when the middleware has run.</param>
    /// <returns><see langword="true"/> when a Correlation ID is available.</returns>
    public static bool TryGetServiceMantleCorrelationId(
        this HttpContext context,
        [NotNullWhen(true)] out string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(context);
        correlationId = CorrelationIdRequestSlot.Get(context);
        return correlationId is not null;
    }
}
