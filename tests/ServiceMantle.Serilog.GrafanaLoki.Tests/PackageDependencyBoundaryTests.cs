using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Logging;
using ServiceMantle.Serilog;
using Xunit;

namespace ServiceMantle.Serilog.GrafanaLoki.Tests;

public sealed class PackageDependencyBoundaryTests
{
    [Fact]
    public void Loki_public_surface_ships_in_the_merged_assembly_with_its_namespace_unchanged()
    {
        foreach (var type in new[]
                 {
                     typeof(GrafanaLokiOptions),
                     typeof(GrafanaLokiDefaults),
                     typeof(WellKnownGrafanaLokiErrorCodes),
                 })
        {
            Assert.Equal("ServiceMantle.Serilog", type.Assembly.GetName().Name);
            Assert.Equal("ServiceMantle.Serilog.GrafanaLoki", type.Namespace);
        }

        Assert.Equal(
            "ServiceMantle.Serilog",
            typeof(global::Microsoft.Extensions.Hosting.ServiceMantleGrafanaLokiHostApplicationBuilderExtensions)
                .Assembly.GetName().Name);
    }

    // The remote log delivery contracts are provider-neutral: they live in the core package's
    // ServiceMantle.Logging namespace so a consumer can implement the authorization resolver and
    // read the delivery counters without any provider-named using.
    [Fact]
    public void Remote_log_delivery_contracts_ship_in_the_core_assembly()
    {
        foreach (var type in new[]
                 {
                     typeof(IRemoteLogAuthorizationResolver),
                     typeof(RemoteLogDeliveryDiagnostics),
                 })
        {
            Assert.Equal("ServiceMantle", type.Assembly.GetName().Name);
            Assert.Equal("ServiceMantle.Logging", type.Namespace);
        }
    }

    // The provider-agnostic core is the boundary that survives the merge: it must reach neither the
    // Serilog integration nor the Loki driver, in the project file or in the restored graph.
    [Fact]
    public void Core_package_reaches_neither_serilog_nor_the_loki_driver()
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "ServiceMantle",
            "ServiceMantle.csproj"));
        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
            IsSerilogOrRemoteDriver((string?)reference.Attribute("Include")));
        Assert.DoesNotContain(project.Descendants("ProjectReference"), reference =>
            IsSerilogOrRemoteDriver((string?)reference.Attribute("Include")));

        var assetsPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "obj",
            "ServiceMantle",
            "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"Missing restored dependency graph: {assetsPath}");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        Assert.DoesNotContain(
            assets.RootElement.GetProperty("libraries").EnumerateObject(),
            library => IsSerilogOrRemoteDriver(library.Name.Split('/')[0]));
    }

    // Merging the sink into ServiceMantle.Serilog removes the dependency isolation that used to keep
    // the Loki driver out of a host that never asked for it. Referencing the package, and even
    // enabling the console pipeline, must therefore still register nothing Loki-owned and leave the
    // sink factory on the local console implementation. RemoteLogDeliveryDiagnostics now ships in
    // the core Logging namespace, so the namespace-based ownership check below cannot reach it and
    // it is asserted by service type instead.
    [Fact]
    public void Enabling_only_the_console_pipeline_registers_no_loki_service()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog();

        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(GrafanaLokiRegistration) ||
            descriptor.ServiceType == typeof(RemoteLogDeliveryDiagnostics) ||
            IsLokiOwned(descriptor.ImplementationType));

        var sinkFactory = builder.Services.Single(descriptor =>
            descriptor.ServiceType == typeof(ISerilogSinkFactory));
        Assert.Equal(typeof(ConsoleSinkFactory), sinkFactory.ImplementationType);
    }

    private static bool IsLokiOwned(Type? type) =>
        type?.Namespace?.StartsWith("ServiceMantle.Serilog.GrafanaLoki", StringComparison.Ordinal) == true;

    private static bool IsSerilogOrRemoteDriver(string? value) =>
        value?.Contains("Serilog", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("Grafana", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("Loki", StringComparison.OrdinalIgnoreCase) == true;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eng", "packages.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
