using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.ManagementApi;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the protected, versioned ServiceMantle management API v1 baseline.
/// </summary>
public static class ServiceMantleManagementApiBuilderExtensions
{
    /// <summary>
    /// Adds the opt-in management API v1 group by combining the existing phase gate and management
    /// authorization on one fixed versioned root.
    /// </summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="configure">Optionally configures the versioned root and the gate's observation timeout.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The registration adds no endpoint of its own: call <c>MapServiceMantleManagementApiV1</c> to
    /// obtain the protected route group and map the consuming service's own children into it. It
    /// registers the phase gate on the same root and the management authorization policy, and it
    /// deliberately does not add cookie authentication, an identity provider, persistence, health
    /// endpoints, or any other optional capability. The consuming service still registers
    /// <c>AddSecurityResponseHeaders</c>, <c>AddSensitiveHeaders</c> and <c>AddRateLimiting</c>,
    /// provides default authenticate, challenge and forbid schemes, and composes the request
    /// pipeline with <c>UseServiceMantlePipeline</c>. Equivalent repeated registrations are
    /// idempotent; a root that does not normalize to exactly one value ending in an independent
    /// <c>/v1</c> segment, a conflicting explicit phase gate registration, and a missing required
    /// capability all fail before the host starts.
    /// </remarks>
    public static ServiceMantleBuilder AddServiceMantleManagementApiV1(
        this ServiceMantleBuilder builder,
        Action<ManagementApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ManagementApiOptions();
        configure?.Invoke(options);
        builder.AddServiceMantlePhaseGate(gate =>
        {
            gate.ManagementPathPrefix = options.RootPath;
            gate.SnapshotTimeout = options.SnapshotTimeout;
        });
        builder.Services.AddServiceMantleManagementAuthorization();
        builder.Services.AddSingleton(new ManagementApiRegistration(
            options.RootPath,
            options.SnapshotTimeout));
        builder.Services.TryAddSingleton<ManagementApiState>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ManagementApiStartupValidator>());
        return builder;
    }
}
