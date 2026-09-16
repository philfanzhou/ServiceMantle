using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.PhaseGate;
using ServiceMantle.Health;
using ServiceMantle.Installation;

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

    /// <summary>
    /// Declares the startup phases in which an endpoint outside the management prefix is admitted
    /// by the phase gate.
    /// </summary>
    /// <param name="builder">The endpoint or group convention builder being marked.</param>
    /// <param name="phases">The non-empty set of startup phases that admit the endpoint.</param>
    /// <remarks>
    /// A marked endpoint is admitted if and only if the observed snapshot's phase belongs to the
    /// declared set and its migration state is neither <see cref="ServiceMigrationReadinessState.Running"/>
    /// nor <see cref="ServiceMigrationReadinessState.Failed"/>. Readiness is not consulted: an
    /// unready database does not keep a marked endpoint out of its declared phases. Endpoints
    /// without this marker keep the existing behaviour of being admitted only once the service is
    /// ready. Admission stays a per-request observation through the same single snapshot read,
    /// cancellation and timeout exits as every other gate decision: a missing, failing, null,
    /// internally cancelled or timing-out snapshot source is rejected with
    /// <c>503 {"errorCode":"service.phase.unavailable"}</c>, and a request the caller aborted fails
    /// with an <see cref="OperationCanceledException"/> carrying the caller's own token before the
    /// endpoint runs. The marker carries no authentication, authorization, rate limiting, or CSRF
    /// semantics; those stay exactly what the endpoint itself declares. The host fails to start
    /// when the marker is placed under the management prefix, when an endpoint carries the marker
    /// more than once, or when the declared set is empty or contains undefined phase values.
    /// Admitting an endpoint in an early phase does not make it safe: anonymous reachability,
    /// input validation, rate limiting, auditing, and credential gates for such endpoints remain
    /// the caller's responsibility, and an admitted request is not recalled when the phase moves
    /// on while it executes.
    /// </remarks>
    public static TBuilder WithServiceMantlePhaseAdmission<TBuilder>(this TBuilder builder,
        params ServiceStartupPhase[] phases)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(phases);
        builder.Add(endpoint => endpoint.Metadata.Add(new PhaseAdmissionMetadata(phases.ToHashSet())));
        return builder;
    }
}
