using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using ServiceMantle.Http;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Adds ServiceMantle's fail-closed Problem Details exception mapping to the request pipeline.
/// </summary>
public static class ServiceMantleProblemDetailsApplicationBuilderExtensions
{
    /// <summary>
    /// Maps downstream exceptions to stable, public-safe RFC 7807 responses while the response has
    /// not started.
    /// </summary>
    /// <param name="app">The application pipeline being composed.</param>
    /// <returns>The same application builder.</returns>
    /// <exception cref="InvalidOperationException">
    /// ServiceMantle host identity has not been registered through <c>AddServiceMantle</c>.
    /// </exception>
    /// <remarks>
    /// Caller cancellation is propagated while the response has not started. Once response headers
    /// have been sent, downstream exceptions are swallowed and the sent response is not rewritten.
    /// Place the Correlation ID middleware outside this middleware so the same identifier also
    /// enriches the complete downstream logging scope.
    /// </remarks>
    public static IApplicationBuilder UseServiceMantleProblemDetails(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.ApplicationServices.GetService<ServiceMantleRegistration>() is null)
        {
            throw new InvalidOperationException(
                "The ServiceMantle Problem Details middleware requires AddServiceMantle to be called first.");
        }

        return app.UseMiddleware<ServiceMantleProblemDetailsMiddleware>();
    }
}
