using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.AspNetCore.ManagementApi.Status;
using ServiceMantle.AspNetCore.PhaseGate;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the anonymous installation status entry of the ServiceMantle management surface.
/// </summary>
public static class ServiceMantleInstallationStatusEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET</c> and <c>HEAD {versionedRoot}/status</c> with the shared management entry
    /// baseline and the ServiceMantle-owned status handler.
    /// </summary>
    /// <param name="endpoints">The application the ServiceMantle pipeline was composed on.</param>
    /// <returns>The mapped endpoint, so the host may add stricter conventions of its own.</returns>
    /// <remarks>
    /// The entry is opt-in and is mapped beside the protected group returned by
    /// <c>MapServiceMantleManagementApiV1</c>, never inside it. With the default root the full path
    /// is <c>/management/v1/status</c>; a custom versioned root moves it. The entry is anonymous in
    /// every phase, so the Phase Gate admits it without reading a snapshot, and it stays bound to
    /// the management rate-limit policy with an anonymous client partition, the security response
    /// headers, and the correlation contract of the shared baseline.
    /// <para>
    /// The handler declares no route value, query parameter, or body. It reads the consuming
    /// service's <c>IServiceHealthSnapshotSource</c> once, the local
    /// <c>BootstrapConfigurationManager</c> status once, and the process-local restart latch once,
    /// and answers <c>200 application/json</c> with exactly <c>phase</c>, <c>migrationStatus</c>,
    /// <c>databaseStatus</c>, <c>bootstrapConfigured</c>, and <c>restartRequired</c>. A missing
    /// source, a damaged or unreadable Bootstrap file, a combination the two sources contradict, an
    /// internal failure, and an internal timeout all answer
    /// <c>503 {"errorCode":"management.status.unavailable"}</c>. Caller cancellation propagates its
    /// original token. <c>HEAD</c> answers the same status and headers with no body.
    /// </para>
    /// <para>
    /// The projection is one coherent observation assembled during one call. It does not lock the
    /// phase, the Bootstrap file, migration, or the database after it returns, and the latch never
    /// claims that another process restarted or that configuration was activated elsewhere.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The installation status capability, the management API v1 capability, or the shared
    /// management entry capability is not registered, or the entry is mapped more than once.
    /// </exception>
    public static RouteHandlerBuilder MapServiceMantleInstallationStatus(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var latch = services.GetService<BootstrapRestartLatch>();
        var bootstrap = services.GetService<IBootstrapStatusReader>();
        var gate = services.GetService<PhaseGateState>();
        if (latch is null || bootstrap is null || gate is null)
        {
            throw InstallationStatusMapping.MissingCapability();
        }

        TimeSpan budget;
        try
        {
            // The internal budget is the management API's own SnapshotTimeout, which
            // AddServiceMantleManagementApiV1 already forwarded to the phase gate. The entry adds no
            // second timeout option of its own.
            budget = gate.GetConfiguration().Timeout;
        }
        catch (InvalidOperationException)
        {
            // The gate owns the diagnostics of its own configured values; this entry point never
            // repeats a configured value.
            throw InstallationStatusMapping.MissingCapability();
        }

        InstallationStatusMapping.RecordMap(services);
        return endpoints.MapServiceMantleManagementEntry(
            ManagementEntryKind.InstallationStatus,
            (HttpContext context) =>
                InstallationStatusHandler.HandleAsync(context, bootstrap, latch, budget));
    }
}
