// Proves the Consul registration entry works from the packed artifact: the class lives in a
// framework namespace that carries no product information, so its name keeps the ServiceMantle
// prefix, and this file names it unqualified next to the framework namespaces the entry itself
// touches - Microsoft.Extensions.DependencyInjection for the collection and
// Microsoft.Extensions.Hosting for the hosted lifecycle it registers.
//
// Registration creates no client, timer, sampler, or network request, and no host is started
// here; the collection is only inspected for the descriptor shape the entry is contracted to
// produce. The four calls agree on the timing, so even a later host build would see one
// consistent lifecycle configuration rather than a conflicting one.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Consul;

var services = new ServiceCollection();

// The two registration forms, called statically through the class itself.
_ = ServiceMantleConsulServiceCollectionExtensions.AddServiceMantleConsul(services);
_ = ServiceMantleConsulServiceCollectionExtensions.AddServiceMantleConsul(
    services,
    options => options.ReadinessPollInterval = TimeSpan.FromSeconds(2));

// And the extension forms consumers actually write, including a repeat: registration is
// idempotent, so the repeated call must not duplicate the hosted lifecycle.
_ = services.AddServiceMantleConsul();
_ = services.AddServiceMantleConsul(
    options => options.ReadinessPollInterval = TimeSpan.FromSeconds(2));

if (services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)) != 1)
{
    throw new InvalidOperationException("Repeated Consul registrations duplicated the hosted lifecycle.");
}

Console.WriteLine(
    "Consul registration entry resolved: ServiceMantleConsulServiceCollectionExtensions with "
    + $"{services.Count} descriptors and one hosted lifecycle.");
