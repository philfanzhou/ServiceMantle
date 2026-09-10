using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.PhaseGate;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers management path validation and finite startup-phase gating.</summary>
public static class ServiceMantlePhaseGateBuilderExtensions
{
    /// <summary>Adds the opt-in phase gate without requiring optional providers or persistence.</summary>
    /// <remarks>Call UseServiceMantlePhaseGate once after routing and before endpoint execution.</remarks>
    public static ServiceMantleBuilder AddServiceMantlePhaseGate(this ServiceMantleBuilder builder,
        Action<PhaseGateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new PhaseGateOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(new PhaseGateRegistration(options.ManagementPathPrefix, options.SnapshotTimeout));
        builder.Services.TryAddSingleton<PhaseGateState>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PhaseGateStartupValidator>());
        return builder;
    }
}
