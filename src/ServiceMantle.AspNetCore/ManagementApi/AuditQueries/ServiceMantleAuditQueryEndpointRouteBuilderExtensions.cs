using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.ManagementApi.AuditQueries;

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
    /// Once an aborted request has been observed while a query dependency settles, the endpoint
    /// answers the caller's cancellation instead of any fixed response or the dependency's own
    /// cancellation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The management API v1 capability or audit query service is not registered, the endpoint is
    /// mapped more than once, or it is not a direct child of the management API v1 group.
    /// </exception>
    public static IEndpointRouteBuilder MapServiceMantleAuditQueries(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var state = services.GetService<ManagementApiState>() ??
            throw AuditQueryMapping.Failure();
        string root;
        try
        {
            root = state.GetRootPath();
        }
        catch (InvalidOperationException)
        {
            throw AuditQueryMapping.Failure();
        }

        if (!AuditQueryMapping.HasQueryService(services))
        {
            throw AuditQueryMapping.MissingQueryService();
        }

        AuditQueryMapping.RecordMap(services);
        var expectedPath = root + AuditQueryMapping.Path;
        endpoints.MapGet(
                AuditQueryMapping.Path,
                (Func<HttpContext, Task<IResult>>)AuditQueryHandlers.QueryAsync)
            .Finally(builder => AuditQueryMapping.Validate(builder, expectedPath));
        return endpoints;
    }
}
