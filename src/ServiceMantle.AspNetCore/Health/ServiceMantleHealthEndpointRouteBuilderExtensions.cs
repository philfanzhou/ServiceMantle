using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.AspNetCore.PhaseGate;
using ServiceMantle.Health;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps ServiceMantle's fixed live and readiness endpoints.</summary>
public static class ServiceMantleHealthEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>/health/live</c>, <c>/health/ready</c>, and <c>/health</c>.
    /// </summary>
    /// <remarks>
    /// The readiness routes project one <see cref="ServiceReadinessDecision"/> obtained from the
    /// registered <see cref="IServiceReadinessDecisionSource"/>; the live route never resolves it.
    /// The default decision source reads one snapshot per request through a token linked to the
    /// request's cancellation and to the configured probe timeout. When the caller aborts the
    /// request, that source cancels the linked token before it releases it, so a cooperative
    /// snapshot source observes the cancellation on the token it received; the request itself still
    /// fails with an <see cref="OperationCanceledException"/> carrying the request's own token.
    /// Sources that ignore the token, block, or throw from a cancellation callback are not
    /// terminated, and the handler does not wait for a source to finish its own cleanup. A missing,
    /// unresolvable, failing, or null-returning decision source fails closed - unless the caller has
    /// cancelled by the time the decision would be projected, in which case the request ends with an
    /// <see cref="OperationCanceledException"/> carrying the request's own token instead of a
    /// response. That holds for every registered decision source, not only the default one, and it
    /// says nothing about a cancellation arriving after that checkpoint or about the delay between a
    /// transport-level abort and the request token.
    /// </remarks>
    public static IEndpointRouteBuilder MapServiceMantleHealthEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.ServiceProvider.GetService<HostRegistration>() is null ||
            endpoints.ServiceProvider.GetService<HealthRegistration>() is null)
        {
            throw new InvalidOperationException(
                "ServiceMantle health endpoints require AddServiceMantle and AddServiceMantleHealthEndpoints.");
        }

        endpoints.MapGet(
            "/health/live",
            static () => Results.Json(new LiveHealthResponse("live")))
            .WithMetadata(new PhaseHealthMetadata("/health/live"));
        endpoints.MapGet(
            "/health/ready",
            (Func<HttpContext, Task<IResult>>)EvaluateReadinessAsync)
            .WithMetadata(new PhaseHealthMetadata("/health/ready"));
        endpoints.MapGet(
            "/health",
            (Func<HttpContext, Task<IResult>>)EvaluateReadinessAsync)
            .WithMetadata(new PhaseHealthMetadata("/health"));
        return endpoints;
    }

    private static async Task<IResult> EvaluateReadinessAsync(HttpContext context)
    {
        var requestAborted = context.RequestAborted;
        requestAborted.ThrowIfCancellationRequested();
        IServiceReadinessDecisionSource? decisionSource;
        try
        {
            decisionSource = context.RequestServices.GetService<IServiceReadinessDecisionSource>();
        }
        catch
        {
            requestAborted.ThrowIfCancellationRequested();
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeFailed);
        }

        requestAborted.ThrowIfCancellationRequested();
        if (decisionSource is null)
        {
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeFailed);
        }

        ServiceReadinessDecision? decision;
        try
        {
            decision = await decisionSource.GetDecisionAsync(requestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw new OperationCanceledException(requestAborted);
        }
        catch
        {
            requestAborted.ThrowIfCancellationRequested();
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeFailed);
        }

        // The one checkpoint every readiness completion passes through: a caller cancellation that
        // has been requested by now outranks the decision this request had already obtained,
        // whichever decision source produced it.
        requestAborted.ThrowIfCancellationRequested();
        if (decision is null)
        {
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeFailed);
        }

        if (decision.Snapshot is null)
        {
            return NotReady(decision.ErrorCode ?? WellKnownServiceHealthErrorCodes.ProbeFailed);
        }

        return Results.Json(
            HealthResponse.FromDecision(decision),
            statusCode: decision.IsReady
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult NotReady(string errorCode) => Results.Json(
        HealthResponse.ProbeFailure(errorCode),
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private sealed record LiveHealthResponse(string Status);

    private sealed record HealthResponse(
        string Status,
        string? Phase,
        string? MigrationStatus,
        string? DatabaseStatus,
        string? ErrorCode)
    {
        internal static HealthResponse FromDecision(ServiceReadinessDecision decision)
        {
            var snapshot = decision.Snapshot!;
            return new(
                decision.IsReady ? "ready" : "not_ready",
                ToWireValue(snapshot.Phase),
                ToWireValue(snapshot.MigrationStatus),
                ToWireValue(snapshot.DatabaseStatus),
                decision.IsReady ? snapshot.ErrorCode : decision.ErrorCode);
        }

        internal static HealthResponse ProbeFailure(string errorCode) => new(
            "not_ready",
            Phase: null,
            MigrationStatus: null,
            DatabaseStatus: null,
            errorCode);

        private static string ToWireValue(ServiceMantle.Installation.ServiceStartupPhase phase) => phase switch
        {
            ServiceMantle.Installation.ServiceStartupPhase.BootstrapConfiguration => "bootstrapConfiguration",
            ServiceMantle.Installation.ServiceStartupPhase.PendingSetup => "pendingSetup",
            ServiceMantle.Installation.ServiceStartupPhase.Completed => "completed",
            _ => throw new InvalidOperationException("The service startup phase is unknown."),
        };

        private static string ToWireValue(ServiceMigrationReadinessState status) => status switch
        {
            ServiceMigrationReadinessState.NotStarted => "notStarted",
            ServiceMigrationReadinessState.Running => "running",
            ServiceMigrationReadinessState.Succeeded => "succeeded",
            ServiceMigrationReadinessState.Failed => "failed",
            _ => throw new InvalidOperationException("The migration readiness state is unknown."),
        };

        private static string ToWireValue(ServiceDatabaseReadinessState status) => status switch
        {
            ServiceDatabaseReadinessState.Reachable => "reachable",
            ServiceDatabaseReadinessState.Unreachable => "unreachable",
            _ => throw new InvalidOperationException("The database readiness state is unknown."),
        };
    }
}
