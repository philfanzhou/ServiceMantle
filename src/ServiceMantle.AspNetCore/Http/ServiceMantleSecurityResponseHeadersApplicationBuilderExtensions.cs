using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Adds the ServiceMantle security response-header middleware.</summary>
public static class ServiceMantleSecurityResponseHeadersApplicationBuilderExtensions
{
    /// <summary>Applies mandatory headers to selected endpoints while headers remain unsent.</summary>
    /// <exception cref="InvalidOperationException">The capability has not been registered.</exception>
    public static IApplicationBuilder UseServiceMantleSecurityResponseHeaders(
        this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetService<ServiceMantleSecurityResponseHeadersRegistration>() is null)
        {
            throw new InvalidOperationException(
                "The ServiceMantle security response-header middleware requires AddSecurityResponseHeaders.");
        }

        ServiceMantlePipelineComposition.RecordUse(app);
        return app.UseMiddleware<ServiceMantleSecurityResponseHeadersMiddleware>();
    }
}
