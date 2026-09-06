using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using ServiceMantle.Management;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the transactional setting update endpoint of the ServiceMantle management API v1 surface.
/// </summary>
public static class ServiceMantleSettingUpdateEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>POST /settings</c> into the group returned by
    /// <c>MapServiceMantleManagementApiV1</c>.
    /// </summary>
    /// <param name="endpoints">The protected management API v1 route group.</param>
    /// <param name="executor">
    /// The consumer-owned transaction boundary. It returns Applied only after commit completes.
    /// </param>
    /// <returns>The same endpoint route builder.</returns>
    /// <exception cref="InvalidOperationException">
    /// The management API v1 capability or executor is missing, the endpoint is mapped more than
    /// once, or it is not a direct child of the management API v1 group.
    /// </exception>
    public static IEndpointRouteBuilder MapServiceMantleSettingUpdates(
        this IEndpointRouteBuilder endpoints,
        ServiceMantleSettingUpdateExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (executor is null)
        {
            throw ServiceMantleSettingUpdateMapping.MissingExecutor();
        }

        var services = endpoints.ServiceProvider;
        var state = services.GetService<ServiceMantleManagementApiState>() ??
            throw ServiceMantleSettingUpdateMapping.Failure();
        string root;
        try
        {
            root = state.GetRootPath();
        }
        catch (InvalidOperationException)
        {
            throw ServiceMantleSettingUpdateMapping.Failure();
        }

        ServiceMantleSettingUpdateMapping.RecordMap(services);
        var expectedPath = root + ServiceMantleSettingUpdateMapping.Path;
        Func<HttpContext, Task<IResult>> handler = context =>
            ServiceMantleSettingUpdateHandlers.UpdateAsync(context, executor);
        endpoints.MapPost(ServiceMantleSettingUpdateMapping.Path, handler)
            .Finally(builder => ServiceMantleSettingUpdateMapping.Validate(builder, expectedPath));
        return endpoints;
    }
}
