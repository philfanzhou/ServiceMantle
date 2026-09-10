using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Otlp.Tests;

public sealed class PackageDependencyBoundaryTests
{
    [Fact]
    public void Otlp_public_surface_ships_in_the_merged_assembly_with_its_namespace_unchanged()
    {
        foreach (var type in new[]
                 {
                     typeof(ServiceMantleOtlpOptions),
                     typeof(ServiceMantleOtlpProtocol),
                     typeof(ServiceMantleOtlpConfigurationException),
                     typeof(WellKnownServiceMantleOtlpErrorCodes),
                     typeof(IServiceMantleOtlpAuthenticationHeaderResolver),
                     typeof(ServiceMantleOtlpAuthenticationHeader),
                 })
        {
            Assert.Equal("ServiceMantle.OpenTelemetry", type.Assembly.GetName().Name);
            Assert.Equal("ServiceMantle.OpenTelemetry.Otlp", type.Namespace);
        }

        Assert.Equal(
            "ServiceMantle.OpenTelemetry",
            typeof(ServiceMantleOtlpBuilderExtensions).Assembly.GetName().Name);
    }

    // Merging the exporter into the instrumentation package removes the dependency isolation that
    // used to keep the OTLP driver out of a host that never asked for it. Referencing the package,
    // and even enabling the base instrumentation, must therefore still activate nothing OTLP-owned.
    [Fact]
    public void Referencing_the_merged_package_registers_no_otlp_service()
    {
        var services = new ServiceCollection();
        services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.2.3")
            .AddOpenTelemetryInstrumentation();

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(ServiceMantleOtlpRuntime) ||
            descriptor.ImplementationType == typeof(ServiceMantleOtlpRuntime) ||
            descriptor.ImplementationType == typeof(ServiceMantleOtlpOptionsConfigurator) ||
            descriptor.ImplementationType == typeof(ServiceMantleOtlpStartupValidator));
    }
}
