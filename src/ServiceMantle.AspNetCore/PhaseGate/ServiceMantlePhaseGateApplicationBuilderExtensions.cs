using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps the management namespace and installs the phase gate.</summary>
public static class ServiceMantlePhaseGateApplicationBuilderExtensions
{
    /// <summary>Adds the phase gate after routing and before endpoint execution.</summary>
    /// <remarks>
    /// This is independent of authentication and authorization. The caller owns ordering relative
    /// to other middleware and must not put protected short-circuit handlers before the gate.
    /// </remarks>
    public static WebApplication UseServiceMantlePhaseGate(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var state = app.Services.GetService<ServiceMantlePhaseGateState>() ?? throw ServiceMantlePhaseGateState.Failure();
        ServiceMantlePipelineComposition.RecordUse(app);
        state.RecordUse(app);
        app.UseMiddleware<ServiceMantlePhaseGateMiddleware>();
        return app;
    }

    /// <summary>Creates the configured management route group without applying authorization.</summary>
    public static RouteGroupBuilder MapServiceMantleManagementGroup(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var state = endpoints.ServiceProvider.GetService<ServiceMantlePhaseGateState>() ?? throw ServiceMantlePhaseGateState.Failure();
        return endpoints.MapGroup(state.GetConfiguration().Prefix);
    }

    /// <summary>Classifies an endpoint or subgroup within the fixed management surface.</summary>
    /// <remarks>Bootstrap, setup and status routes must use their matching fixed path branches.</remarks>
    public static TBuilder WithServiceMantleManagementSurface<TBuilder>(this TBuilder builder, ServiceMantleManagementSurface surface)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint =>
        {
            if (!endpoint.Metadata.OfType<ServiceMantleManagementSurfaceMetadata>().Any(marker => marker.Surface == surface))
                endpoint.Metadata.Add(new ServiceMantleManagementSurfaceMetadata(surface));
        });
        return builder;
    }
}
