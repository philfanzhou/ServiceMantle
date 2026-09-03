using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;

namespace ServiceMantle.AspNetCore;

internal sealed class ServiceMantlePhaseGateMiddleware(RequestDelegate next, ServiceMantlePhaseGateState state)
{
    private readonly ServiceMantlePhaseGateRegistration configuration = state.GetConfiguration();

    public async Task InvokeAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var path = context.Request.Path.Value ?? "";
        var health = endpoint.Metadata.GetMetadata<ServiceMantlePhaseHealthMetadata>();
        if (health is not null && string.Equals(path.TrimEnd('/'), health.Path, StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        var markers = endpoint.Metadata.GetOrderedMetadata<ServiceMantleManagementSurfaceMetadata>();
        var surface = markers.Count == 1 ? markers[0].Surface : (ServiceMantleManagementSurface?)null;
        if (markers.Count > 1 || surface is not null && !ServiceMantlePhaseGateState.Matches(path, configuration.Prefix, surface.Value) ||
            surface is null && ServiceMantlePhaseGateState.Under(path, configuration.Prefix))
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }
        if (surface == ServiceMantleManagementSurface.Status)
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                await next(context).ConfigureAwait(false);
            else await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        ServiceHealthSnapshot? snapshot;
        using var timeout = new CancellationTokenSource(configuration.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var source = context.RequestServices.GetService<IServiceHealthSnapshotSource>();
            snapshot = source is null ? null : await source.GetSnapshotAsync(linked.Token).AsTask()
                .WaitAsync(configuration.Timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The phase observation was cancelled by the caller.", cancellationToken);
        }
        catch
        {
            snapshot = null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null || !Allows(surface, snapshot))
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }
        await next(context).ConfigureAwait(false);
    }

    private static bool Allows(ServiceMantleManagementSurface? surface, ServiceHealthSnapshot snapshot)
    {
        if (snapshot.MigrationStatus is ServiceMigrationReadinessState.Running or ServiceMigrationReadinessState.Failed) return false;
        return surface switch
        {
            ServiceMantleManagementSurface.Bootstrap => snapshot.Phase == ServiceStartupPhase.BootstrapConfiguration,
            ServiceMantleManagementSurface.Setup => snapshot.Phase == ServiceStartupPhase.PendingSetup &&
                snapshot.MigrationStatus == ServiceMigrationReadinessState.Succeeded &&
                snapshot.DatabaseStatus == ServiceDatabaseReadinessState.Reachable,
            null or ServiceMantleManagementSurface.Management => ServiceHealthEvaluator.Evaluate(snapshot).IsReady,
            _ => false
        };
    }

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new { errorCode = "service.phase.unavailable" }, context.RequestAborted);
    }
}
