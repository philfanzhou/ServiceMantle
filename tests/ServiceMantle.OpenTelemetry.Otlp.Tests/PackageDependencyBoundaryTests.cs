using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Diagnostics;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Otlp.Tests;

public sealed class PackageDependencyBoundaryTests
{
    [Fact]
    public void Otlp_public_surface_ships_in_the_merged_assembly_with_its_namespace_unchanged()
    {
        foreach (var type in new[]
                 {
                     typeof(OtlpOptions),
                     typeof(OtlpProtocol),
                     typeof(OtlpConfigurationException),
                     typeof(WellKnownOtlpErrorCodes),
                 })
        {
            Assert.Equal("ServiceMantle.OpenTelemetry", type.Assembly.GetName().Name);
            Assert.Equal("ServiceMantle.OpenTelemetry.Otlp", type.Namespace);
        }

        Assert.Equal(
            "ServiceMantle.OpenTelemetry",
            typeof(ServiceMantleOtlpBuilderExtensions).Assembly.GetName().Name);
    }

    // The remote telemetry authentication contracts are provider-neutral (ADR 0007 class A): they
    // live in the core package's ServiceMantle.Diagnostics namespace so a consumer can implement
    // the resolver without any provider-named using. Exporting still requires this package.
    [Fact]
    public void Remote_telemetry_authentication_contracts_ship_in_the_core_assembly()
    {
        foreach (var type in new[]
                 {
                     typeof(IRemoteTelemetryAuthenticationResolver),
                     typeof(RemoteTelemetryAuthenticationHeader),
                 })
        {
            Assert.Equal("ServiceMantle", type.Assembly.GetName().Name);
            Assert.Equal("ServiceMantle.Diagnostics", type.Namespace);
        }
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
            descriptor.ServiceType == typeof(OtlpRuntime) ||
            descriptor.ImplementationType == typeof(OtlpRuntime) ||
            descriptor.ImplementationType == typeof(OtlpOptionsConfigurator) ||
            descriptor.ImplementationType == typeof(OtlpStartupValidator));
    }
}
