using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.ManagementApi.SettingUpdates;

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
        SettingUpdateExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (executor is null)
        {
            throw SettingUpdateMapping.MissingExecutor();
        }

        var services = endpoints.ServiceProvider;
        var state = services.GetService<ManagementApiState>() ??
            throw SettingUpdateMapping.Failure();
        string root;
        try
        {
            root = state.GetRootPath();
        }
        catch (InvalidOperationException)
        {
            throw SettingUpdateMapping.Failure();
        }

        SettingUpdateMapping.RecordMap(services);
        var expectedPath = root + SettingUpdateMapping.Path;
        Func<HttpContext, Task<IResult>> handler = context =>
            SettingUpdateHandlers.UpdateAsync(context, executor);
        endpoints.MapPost(SettingUpdateMapping.Path, handler)
            .Finally(builder => SettingUpdateMapping.Validate(builder, expectedPath));
        return endpoints;
    }
}
