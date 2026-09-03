using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using ServiceMantle.Http;

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
    /// relative order against exception handling is fixed by UseServiceMantlePipeline when composing
    /// the complete ServiceMantle HTTP pipeline.
    /// </remarks>
    public static IApplicationBuilder UseServiceMantleCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The guard resolves the assembly-internal AddServiceMantle marker rather than any of the
        // public services that call registers. A consumer can register ServiceLogContext (or
        // ServiceId, InstanceId, BootstrapFileStore) directly, so those types prove nothing about
        // whether the host identity AddServiceMantle owns was ever established.
        if (app.ApplicationServices.GetService<ServiceMantleRegistration>() is null)
        {
            throw new InvalidOperationException(
                "The ServiceMantle Correlation ID middleware requires AddServiceMantle to be called first.");
        }

        ServiceMantlePipelineComposition.RecordUse(app);
        return app.UseMiddleware<ServiceMantleCorrelationIdMiddleware>();
    }
}
