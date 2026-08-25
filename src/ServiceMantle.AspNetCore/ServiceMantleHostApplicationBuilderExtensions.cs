using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.AspNetCore;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Registers ServiceMantle through the common .NET host builder contract.
/// </summary>
public static class ServiceMantleHostApplicationBuilderExtensions
{
    /// <summary>
    /// Registers the provider-independent ServiceMantle hosting foundation.
    /// </summary>
    /// <param name="builder">The common .NET host application builder.</param>
    /// <param name="serviceId">The stable identity shared by all service instances.</param>
    /// <param name="instanceId">The identity of this running instance.</param>
    /// <param name="bootstrapFilePath">An optional explicit local Bootstrap file path.</param>
    /// <param name="serviceVersion">
    /// An optional explicit service version. When omitted, the entry assembly version is used.
    /// </param>
    /// <returns>A builder used to add optional ServiceMantle capabilities.</returns>
    public static ServiceMantleBuilder AddServiceMantle(
        this IHostApplicationBuilder builder,
        ServiceId serviceId,
        InstanceId instanceId,
        string? bootstrapFilePath = null,
        string? serviceVersion = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Services.AddServiceMantle(
            serviceId,
            instanceId,
            bootstrapFilePath,
            serviceVersion);
    }
}
