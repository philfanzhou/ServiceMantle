using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.PhaseGate;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps the management namespace and installs the phase gate.</summary>
public static class ServiceMantlePhaseGateApplicationBuilderExtensions
{
    /// <summary>Adds the phase gate after routing and before endpoint execution.</summary>
    /// <remarks>
    /// This is independent of authentication and authorization. The caller owns ordering relative
    /// to other middleware and must not put protected short-circuit handlers before the gate.
    /// The gate reads one snapshot per admitted request through a token linked to the request's
    /// cancellation and to the configured snapshot timeout. When the caller aborts the request, the
    /// gate cancels that linked token before it releases it, so a cooperative snapshot source
    /// observes the cancellation on the token it received; the request itself still fails with an
    /// <see cref="OperationCanceledException"/> carrying the caller's own token and the endpoint is
    /// never executed. Sources that ignore the token, block, or throw from a cancellation callback
    /// are not terminated, and the gate does not wait for a source to finish its own cleanup.
    /// </remarks>
    public static WebApplication UseServiceMantlePhaseGate(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var state = app.Services.GetService<PhaseGateState>() ?? throw PhaseGateState.Failure();
        PipelineComposition.RecordUse(app);
        state.RecordUse(app);
        app.UseMiddleware<PhaseGateMiddleware>();
        return app;
    }

    /// <summary>Creates the configured management route group without applying authorization.</summary>
    public static RouteGroupBuilder MapServiceMantleManagementGroup(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var state = endpoints.ServiceProvider.GetService<PhaseGateState>() ?? throw PhaseGateState.Failure();
        return endpoints.MapGroup(state.GetConfiguration().Prefix);
    }

    /// <summary>Classifies an endpoint or subgroup within the fixed management surface.</summary>
    /// <remarks>Bootstrap, setup and status routes must use their matching fixed path branches.</remarks>
    public static TBuilder WithServiceMantleManagementSurface<TBuilder>(this TBuilder builder, ManagementSurface surface)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint =>
        {
            if (!endpoint.Metadata.OfType<ManagementSurfaceMetadata>().Any(marker => marker.Surface == surface))
                endpoint.Metadata.Add(new ManagementSurfaceMetadata(surface));
        });
        return builder;
    }
}
