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
    public static ServiceMantleBuilder AddServiceMantle(
        this IHostApplicationBuilder builder,
        ServiceId serviceId,
        InstanceId instanceId,
        string? bootstrapFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Services.AddServiceMantle(serviceId, instanceId, bootstrapFilePath);
    }
}
