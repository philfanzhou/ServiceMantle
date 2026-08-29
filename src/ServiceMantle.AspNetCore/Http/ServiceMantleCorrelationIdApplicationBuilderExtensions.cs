using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Http;
using ServiceMantle.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Adds the ServiceMantle Correlation ID middleware to the request pipeline.
/// </summary>
public static class ServiceMantleCorrelationIdApplicationBuilderExtensions
{
    /// <summary>
    /// Resolves one Correlation ID per request and publishes it to the request context, the response
    /// header, and the <see cref="Microsoft.Extensions.Logging.ILogger"/> scope of the downstream
    /// pipeline.
    /// </summary>
    /// <param name="app">The application pipeline being composed.</param>
    /// <returns>The same pipeline builder.</returns>
    /// <exception cref="InvalidOperationException">
    /// ServiceMantle host identity has not been registered through <c>AddServiceMantle</c>.
    /// </exception>
    /// <remarks>
    /// Place this entry point where the pipeline needs Correlation ID enrichment to begin. Its
    /// relative order against exception handling is fixed by the pipeline composition task, not here.
    /// </remarks>
    public static IApplicationBuilder UseServiceMantleCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.ApplicationServices.GetService<ServiceLogContext>() is null)
        {
            throw new InvalidOperationException(
                "The ServiceMantle Correlation ID middleware requires AddServiceMantle to be called first.");
        }

        return app.UseMiddleware<ServiceMantleCorrelationIdMiddleware>();
    }
}
