using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the read-only audit query endpoint of the ServiceMantle management API v1 surface.
/// </summary>
public static class ServiceMantleAuditQueryEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET /audit</c> into the group returned by
    /// <c>MapServiceMantleManagementApiV1</c>.
    /// </summary>
    /// <param name="endpoints">The protected management API v1 route group.</param>
    /// <returns>The same endpoint route builder.</returns>
    /// <remarks>
    /// The endpoint is opt-in and adapts one scoped <c>IManagementAuditQueryService</c> call per
    /// accepted request. It maps no write, search, or detail endpoint and never saves or commits the
    /// consuming application's unit of work. Inputs and output fields are closed and bounded; audit
    /// query validation failures answer the fixed management <c>400</c>, while stored-data and
    /// internal query failures answer a fixed <c>503</c> without exposing internal classifications.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The management API v1 capability or audit query service is not registered, the endpoint is
    /// mapped more than once, or it is not a direct child of the management API v1 group.
    /// </exception>
    public static IEndpointRouteBuilder MapServiceMantleAuditQueries(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var state = services.GetService<ServiceMantleManagementApiState>() ??
            throw ServiceMantleAuditQueryMapping.Failure();
        string root;
        try
        {
            root = state.GetRootPath();
        }
        catch (InvalidOperationException)
        {
            throw ServiceMantleAuditQueryMapping.Failure();
        }

        if (!ServiceMantleAuditQueryMapping.HasQueryService(services))
        {
            throw ServiceMantleAuditQueryMapping.MissingQueryService();
        }

        ServiceMantleAuditQueryMapping.RecordMap(services);
        var expectedPath = root + ServiceMantleAuditQueryMapping.Path;
        endpoints.MapGet(
                ServiceMantleAuditQueryMapping.Path,
                (Func<HttpContext, Task<IResult>>)ServiceMantleAuditQueryHandlers.QueryAsync)
            .Finally(builder => ServiceMantleAuditQueryMapping.Validate(builder, expectedPath));
        return endpoints;
    }
}
