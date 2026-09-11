using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.ManagementApi;
using ServiceMantle.AspNetCore.ManagementApi.SettingQueries;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the read-only setting queries of the ServiceMantle management API v1 surface.
/// </summary>
public static class ServiceMantleSettingQueryEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET /settings/definitions</c> and <c>GET /settings</c> into the group returned by
    /// <c>MapServiceMantleManagementApiV1</c>.
    /// </summary>
    /// <param name="endpoints">The protected management API v1 route group.</param>
    /// <returns>The same route group.</returns>
    /// <remarks>
    /// The endpoints are opt-in: the management API v1 baseline never adds them, so a host that does
    /// not call this method exposes nothing here. They are a thin HTTP adaptation of the existing
    /// <c>ServiceSettingQueryService</c>, which the consuming service registers with
    /// <c>AddServiceMantleSettingSnapshots</c> together with its own store, definition catalog and,
    /// where sensitive settings exist, root-key source. This method registers no database, root key,
    /// or persistence adapter of its own, and adds no update endpoint.
    /// <para>
    /// The definitions response never refreshes. The current-values response refreshes exactly once
    /// per request and projects only a complete successful snapshot; any refresh failure answers one
    /// fixed <c>503</c> body without a version, a key, or a partial or previous value. Values of
    /// settings marked sensitive are always projected as <c>null</c>. Both endpoints accept one
    /// optional <c>group</c> query value and reject every other input with the fixed management
    /// <c>400</c> result without echoing it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The management API v1 capability or the setting query service is not registered, or the
    /// endpoints are mapped more than once. A mapping onto a route group other than the management
    /// API v1 group fails the same way before the host starts.
    /// </exception>
    public static IEndpointRouteBuilder MapServiceMantleSettingQueries(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var state = services.GetService<ManagementApiState>() ??
            throw SettingQueryMapping.Failure();
        string root;
        try
        {
            root = state.GetRootPath();
        }
        catch (InvalidOperationException)
        {
            // The baseline owns the root's own diagnostics; these endpoints never repeat a
            // configured value of their own.
            throw SettingQueryMapping.Failure();
        }

        if (!SettingQueryMapping.HasQueryService(services))
        {
            throw SettingQueryMapping.MissingQueryService();
        }

        SettingQueryMapping.RecordMap(services);
        Map(endpoints, root, SettingQueryMapping.DefinitionsPath, SettingQueryHandlers.Definitions);
        Map(endpoints, root, SettingQueryMapping.CurrentValuesPath, SettingQueryHandlers.CurrentValuesAsync);
        return endpoints;
    }

    private static void Map(
        IEndpointRouteBuilder endpoints,
        string root,
        string path,
        Func<HttpContext, Task<IResult>> handler)
    {
        var expectedPath = root + path;
        endpoints.MapGet(path, handler)
            .Finally(builder => SettingQueryMapping.Validate(builder, expectedPath));
    }
}
