// Proves the Consul registration entry works from the packed artifact: the class lives in a
// framework namespace that carries no product information, so its name keeps the ServiceMantle
// prefix, and this file names it unqualified next to the framework namespaces the entry itself
// touches - Microsoft.Extensions.DependencyInjection for the collection and
// Microsoft.Extensions.Hosting for the hosted lifecycle it registers. The Web SDK keeps the
// ASP.NET Core framework reference and its implicit usings in scope, so a same-name collision
// between the neutral options type and any framework type would surface here as a compile error.
//
// The lifecycle timing options are the core package's provider-neutral
// ServiceRegistrationLifecycleOptions in ServiceMantle.Discovery: configuring all six values
// requires no provider-named using at all. Registration creates no client, timer, sampler, or
// network request, and no host is started here; the collection is only inspected for the
// descriptor shape the entry is contracted to produce. The calls agree on the timing, so even a
// later host build would see one consistent lifecycle configuration rather than a conflicting one.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Configuration;
using ServiceMantle.Discovery;

var services = new ServiceCollection();

void Configure(ServiceRegistrationLifecycleOptions options)
{
    options.ReadinessPollInterval = TimeSpan.FromSeconds(1);    // 100 ms - 30 s
    options.ReadinessCallBudget = TimeSpan.FromSeconds(10);     // 100 ms - 60 s
    options.OperationBudget = TimeSpan.FromSeconds(10);         // 100 ms - 30 s
    options.InitialRetryDelay = TimeSpan.FromMilliseconds(250); // 50 ms - 5 s
    options.MaximumRetryDelay = TimeSpan.FromSeconds(5);        // initial - 30 s
    options.ShutdownBudget = TimeSpan.FromSeconds(15);          // 1 s - 60 s
}

// The two registration forms, called statically through the class itself: the parameterless
// default and an explicitly named Action<ServiceRegistrationLifecycleOptions>.
_ = ServiceMantleConsulServiceCollectionExtensions.AddServiceMantleConsul(services);
_ = ServiceMantleConsulServiceCollectionExtensions.AddServiceMantleConsul(services, Configure);

// And the extension forms consumers actually write, including a repeat with the same timing:
// registration is idempotent, so the repeated call must not duplicate the hosted lifecycle.
_ = services.AddServiceMantleConsul();
_ = services.AddServiceMantleConsul(Configure);

if (services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)) != 1)
{
    throw new InvalidOperationException("Repeated Consul registrations duplicated the hosted lifecycle.");
}

// The typed snapshot-source entry: a dedicated consumer-owned accessor type replaces the
// process-global snapshot source. It lives in its own collection so the two forms never mix
// (a mix is a conflicting configuration by contract), and it needs only the core package's
// ServiceMantle.Configuration namespace for the accessor contract - no Consul namespace using,
// no provider-named type burden. Repeating the call with agreeing timing stays idempotent.
var typedServices = new ServiceCollection();
typedServices.AddSingleton(new DiscoverySnapshotAccessor());
_ = typedServices.AddServiceMantleConsul<DiscoverySnapshotAccessor>();
_ = typedServices.AddServiceMantleConsul<DiscoverySnapshotAccessor>(Configure);

if (typedServices.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)) != 1)
{
    throw new InvalidOperationException("Repeated typed Consul registrations duplicated the hosted lifecycle.");
}

if (typedServices.Any(descriptor =>
        descriptor.ServiceType == typeof(IServiceSettingDefinitionProvider) ||
        descriptor.ServiceType == typeof(IServiceSettingCompositeValidator)))
{
    throw new InvalidOperationException("The typed Consul entry contributed to the global setting catalog.");
}

Console.WriteLine(
    "Consul registration entry resolved with ServiceRegistrationLifecycleOptions: "
    + $"{services.Count} descriptors and one hosted lifecycle; the typed entry added "
    + $"{typedServices.Count} more without touching the global catalog.");

/// <summary>
/// A consumer-owned discovery snapshot accessor: the consumer activates its projection of the
/// authoritative product snapshot on this singleton before the host starts.
/// </summary>
internal sealed class DiscoverySnapshotAccessor : IServiceSettingCurrentSnapshotAccessor
{
    public bool TryGetCurrent(out ServiceSettingSnapshot? snapshot)
    {
        // Placeholder for the consumer's activation; registration itself never reads it.
        snapshot = null;
        return false;
    }
}
