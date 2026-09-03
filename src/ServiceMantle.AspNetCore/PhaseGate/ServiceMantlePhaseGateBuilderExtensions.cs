using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle.AspNetCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers management path validation and finite startup-phase gating.</summary>
public static class ServiceMantlePhaseGateBuilderExtensions
{
    /// <summary>Adds the opt-in phase gate without requiring optional providers or persistence.</summary>
    /// <remarks>Call UseServiceMantlePhaseGate once after routing and before endpoint execution.</remarks>
    public static ServiceMantleBuilder AddServiceMantlePhaseGate(this ServiceMantleBuilder builder,
        Action<ServiceMantlePhaseGateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new ServiceMantlePhaseGateOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(new ServiceMantlePhaseGateRegistration(options.ManagementPathPrefix, options.SnapshotTimeout));
        builder.Services.TryAddSingleton<ServiceMantlePhaseGateState>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ServiceMantlePhaseGateStartupValidator>());
        return builder;
    }
}
