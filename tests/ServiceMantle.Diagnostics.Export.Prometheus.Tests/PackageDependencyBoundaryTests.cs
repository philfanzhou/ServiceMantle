using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Diagnostics.Export.Prometheus;
using Xunit;

namespace ServiceMantle.Diagnostics.Export.Prometheus.Tests;

public sealed class PackageDependencyBoundaryTests
{
    [Fact]
    public void Prometheus_public_surface_ships_in_the_merged_assembly_under_its_dedicated_namespace()
    {
        foreach (var type in new[]
                 {
                     typeof(PrometheusOptions),
                     typeof(PrometheusConfigurationException),
                     typeof(WellKnownPrometheusErrorCodes),
                 })
        {
            Assert.Equal("ServiceMantle.Diagnostics", type.Assembly.GetName().Name);
            Assert.Equal("ServiceMantle.Diagnostics.Export.Prometheus", type.Namespace);
        }

        Assert.Equal(
            "ServiceMantle.Diagnostics",
            typeof(ServiceMantlePrometheusBuilderExtensions).Assembly.GetName().Name);
        Assert.Equal(
            "ServiceMantle.Diagnostics",
            typeof(global::Microsoft.AspNetCore.Builder.ServiceMantlePrometheusEndpointRouteBuilderExtensions).Assembly.GetName().Name);
    }

    // Merging the scraping endpoint into the instrumentation package removes the dependency
    // isolation that used to keep the Prometheus exporter out of a host that never asked for it.
    // Referencing the package, and even enabling the base instrumentation, must therefore still
    // register no exporter, no endpoint state, and no scrape gate.
    [Fact]
    public void Referencing_the_merged_package_registers_no_prometheus_service()
    {
        var services = new ServiceCollection();
        services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.2.3")
            .AddOpenTelemetryInstrumentation();

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(PrometheusRegistration) ||
            descriptor.ImplementationType == typeof(PrometheusSnapshotProvider) ||
            descriptor.ImplementationType == typeof(PrometheusEndpointState) ||
            descriptor.ImplementationType == typeof(PrometheusStartupValidator) ||
            descriptor.ImplementationType == typeof(PrometheusExporterOptionsPolicy));
    }
}
