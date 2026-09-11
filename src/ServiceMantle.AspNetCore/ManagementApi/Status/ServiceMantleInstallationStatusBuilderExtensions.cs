using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.ManagementApi.Status;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the opt-in anonymous ServiceMantle installation status entry.
/// </summary>
public static class ServiceMantleInstallationStatusBuilderExtensions
{
    /// <summary>
    /// Adds the process-local Bootstrap restart latch the installation status entry reports.
    /// </summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The registration adds no endpoint: call <c>MapServiceMantleInstallationStatus</c> to serve
    /// it. It also adds the shared management entry convention, which the status entry consumes and
    /// does not extend; the consuming service still adds <c>AddServiceMantleManagementApiV1</c>,
    /// <c>AddSecurityResponseHeaders</c>, <c>AddSensitiveHeaders</c>, and <c>AddRateLimiting</c>,
    /// and composes the pipeline with <c>UseServiceMantlePipeline</c>. The latch is per process: it
    /// starts false, is set only by a successful local Bootstrap write in this process, is never
    /// persisted, and resets on restart. Repeated registration is idempotent.
    /// </remarks>
    public static ServiceMantleBuilder AddServiceMantleInstallationStatus(this ServiceMantleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddServiceMantleManagementEntries();
        builder.Services.TryAddSingleton<BootstrapRestartLatch>();
        builder.Services.TryAddSingleton<
            IBootstrapStatusReader,
            BootstrapStatusReader>();
        return builder;
    }
}
