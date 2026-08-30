using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Adds the ServiceMantle-owned Forwarded Headers middleware instance.</summary>
public static class ServiceMantleForwardedHeadersApplicationBuilderExtensions
{
    /// <summary>Applies the immutable forwarded-header trust boundary configured at startup.</summary>
    /// <exception cref="InvalidOperationException">
    /// The ServiceMantle forwarded-header capability has not been registered.
    /// </exception>
    public static IApplicationBuilder UseServiceMantleForwardedHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetService<ServiceMantleForwardedHeadersSnapshotProvider>() is null)
        {
            throw new InvalidOperationException(
                "The ServiceMantle forwarded-header middleware requires AddForwardedHeaders.");
        }

        return app.UseMiddleware<ServiceMantleForwardedHeadersMiddleware>();
    }
}
