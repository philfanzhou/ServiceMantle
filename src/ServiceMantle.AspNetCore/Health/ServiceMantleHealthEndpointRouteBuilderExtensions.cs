using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps ServiceMantle's fixed live and readiness endpoints.</summary>
public static class ServiceMantleHealthEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>/health/live</c>, <c>/health/ready</c>, and <c>/health</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapServiceMantleHealthEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.ServiceProvider.GetService<ServiceMantleRegistration>() is null ||
            endpoints.ServiceProvider.GetService<ServiceMantleHealthRegistration>() is null)
        {
            throw new InvalidOperationException(
                "ServiceMantle health endpoints require AddServiceMantle and AddServiceMantleHealthEndpoints.");
        }

        endpoints.MapGet(
            "/health/live",
            static () => Results.Json(new LiveHealthResponse("live")))
            .WithMetadata(new ServiceMantlePhaseHealthMetadata("/health/live"));
        endpoints.MapGet(
            "/health/ready",
            (Func<HttpContext, Task<IResult>>)EvaluateReadinessAsync)
            .WithMetadata(new ServiceMantlePhaseHealthMetadata("/health/ready"));
        endpoints.MapGet(
            "/health",
            (Func<HttpContext, Task<IResult>>)EvaluateReadinessAsync)
            .WithMetadata(new ServiceMantlePhaseHealthMetadata("/health"));
        return endpoints;
    }

    private static async Task<IResult> EvaluateReadinessAsync(HttpContext context)
    {
        var requestAborted = context.RequestAborted;
        requestAborted.ThrowIfCancellationRequested();
        var registration = context.RequestServices
            .GetRequiredService<ServiceMantleHealthRegistration>();
        IServiceHealthSnapshotSource? source;
        try
        {
            source = context.RequestServices.GetService<IServiceHealthSnapshotSource>();
        }
        catch
        {
            requestAborted.ThrowIfCancellationRequested();
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeFailed);
        }

        requestAborted.ThrowIfCancellationRequested();
        if (source is null)
        {
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeFailed);
        }

        using var timeout = new CancellationTokenSource(registration.ProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            requestAborted,
            timeout.Token);

        ServiceHealthSnapshot? snapshot;
        try
        {
            snapshot = await source.GetSnapshotAsync(linked.Token)
                .AsTask()
                .WaitAsync(registration.ProbeTimeout, requestAborted)
                .ConfigureAwait(false);
            requestAborted.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw new OperationCanceledException(requestAborted);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeTimeout);
        }
        catch (TimeoutException)
        {
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeTimeout);
        }
        catch
        {
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeFailed);
        }

        if (snapshot is null)
        {
            return NotReady(WellKnownServiceHealthErrorCodes.ProbeFailed);
        }

        var evaluation = ServiceHealthEvaluator.Evaluate(snapshot);
        if (!evaluation.IsReady)
        {
            return Results.Json(
                HealthResponse.FromSnapshot(isReady: false, snapshot),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        ServiceReadinessContributorCombiner combiner;
        try
        {
            combiner = context.RequestServices
                .GetRequiredService<ServiceReadinessContributorCombiner>();
        }
        catch
        {
            requestAborted.ThrowIfCancellationRequested();
            return ContributorNotReady(
                snapshot,
                WellKnownServiceHealthErrorCodes.ContributorFailed);
        }

        ServiceReadinessContributorResult contribution;
        try
        {
            contribution = await combiner
                .EvaluateAsync(snapshot, registration.ContributorTimeout, requestAborted)
                .ConfigureAwait(false);
            requestAborted.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw new OperationCanceledException(requestAborted);
        }
        catch
        {
            requestAborted.ThrowIfCancellationRequested();
            return ContributorNotReady(
                snapshot,
                WellKnownServiceHealthErrorCodes.ContributorFailed);
        }

        if (!contribution.IsReady)
        {
            return ContributorNotReady(snapshot, contribution.ErrorCode!);
        }

        return Results.Json(
            HealthResponse.FromSnapshot(isReady: true, snapshot),
            statusCode: StatusCodes.Status200OK);
    }

    private static IResult NotReady(string errorCode) => Results.Json(
        HealthResponse.ProbeFailure(errorCode),
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult ContributorNotReady(
        ServiceHealthSnapshot snapshot,
        string errorCode) => Results.Json(
            HealthResponse.FromContributorFailure(snapshot, errorCode),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private sealed record LiveHealthResponse(string Status);

    private sealed record HealthResponse(
        string Status,
        string? Phase,
        string? MigrationStatus,
        string? DatabaseStatus,
        string? ErrorCode)
    {
        internal static HealthResponse FromSnapshot(
            bool isReady,
            ServiceHealthSnapshot snapshot) => new(
                isReady ? "ready" : "not_ready",
                ToWireValue(snapshot.Phase),
                ToWireValue(snapshot.MigrationStatus),
                ToWireValue(snapshot.DatabaseStatus),
                snapshot.ErrorCode);

        internal static HealthResponse ProbeFailure(string errorCode) => new(
            "not_ready",
            Phase: null,
            MigrationStatus: null,
            DatabaseStatus: null,
            errorCode);

        internal static HealthResponse FromContributorFailure(
            ServiceHealthSnapshot snapshot,
            string errorCode) => new(
                "not_ready",
                ToWireValue(snapshot.Phase),
                ToWireValue(snapshot.MigrationStatus),
                ToWireValue(snapshot.DatabaseStatus),
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
