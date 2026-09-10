using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Tests;

public sealed class PackageDependencyBoundaryTests
{
    private static readonly string[] ExpectedDependencies =
    [
        "OpenTelemetry.Exporter.OpenTelemetryProtocol",
        "OpenTelemetry.Exporter.Prometheus.AspNetCore",
        "OpenTelemetry.Extensions.Hosting",
        "OpenTelemetry.Instrumentation.AspNetCore",
        "OpenTelemetry.Instrumentation.Http",
        "OpenTelemetry.Instrumentation.Runtime",
        "ServiceMantle.AspNetCore",
    ];

    [Fact]
    public void Observability_package_declares_exactly_the_registered_dependencies()
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "ServiceMantle.OpenTelemetry",
            "ServiceMantle.OpenTelemetry.csproj"));
        var dependencies = project
            .Descendants("PackageReference")
            .Select(Include)
            .Concat(project.Descendants("ProjectReference").Select(reference =>
                Path.GetFileNameWithoutExtension(
                    Include(reference).Replace('\\', Path.DirectorySeparatorChar))));

        Assert.Equal(
            ExpectedDependencies.Order(StringComparer.OrdinalIgnoreCase),
            dependencies.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(project.Descendants("FrameworkReference"));
    }

    [Fact]
    public void Registry_lists_one_observability_package_that_owns_the_three_test_projects()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var registry = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "eng", "packages.json")));
        var packages = registry.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        var identifiers = packages
            .Select(package => package.GetProperty("id").GetString()!)
            .ToArray();

        Assert.DoesNotContain("ServiceMantle.OpenTelemetry.Otlp", identifiers);
        Assert.DoesNotContain("ServiceMantle.OpenTelemetry.Prometheus", identifiers);

        var observability = packages.Single(package =>
            package.GetProperty("id").GetString() == "ServiceMantle.OpenTelemetry");
        Assert.True(observability.GetProperty("optional").GetBoolean());
        Assert.Equal(
            ExpectedDependencies.Order(StringComparer.OrdinalIgnoreCase),
            observability
                .GetProperty("dependencies")
                .EnumerateArray()
                .Select(element => element.GetString()!)
                .Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            new[]
            {
                "tests/ServiceMantle.OpenTelemetry.Tests/ServiceMantle.OpenTelemetry.Tests.csproj",
                "tests/ServiceMantle.OpenTelemetry.Otlp.Tests/ServiceMantle.OpenTelemetry.Otlp.Tests.csproj",
                "tests/ServiceMantle.OpenTelemetry.Prometheus.Tests/ServiceMantle.OpenTelemetry.Prometheus.Tests.csproj",
            },
            observability
                .GetProperty("tests")
                .EnumerateArray()
                .Select(test => test.GetProperty("project").GetString()!));
    }

    // The merged package deliberately gives up exporter isolation between OTLP, Prometheus, and the
    // base instrumentation. The boundary that still has to hold is that neither the provider-agnostic
    // core nor the ASP.NET Core integration package can drag an exporter into a consuming service.
    [Theory]
    [InlineData("ServiceMantle", "ServiceMantle.csproj")]
    [InlineData("ServiceMantle.AspNetCore", "ServiceMantle.AspNetCore.csproj")]
    public void Core_and_aspnetcore_packages_have_no_exporter_dependency(
        string directory,
        string projectFile)
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(repositoryRoot, "src", directory, projectFile));

        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
            IsExporter((string?)reference.Attribute("Include")));
        Assert.DoesNotContain(project.Descendants("ProjectReference"), reference =>
            IsExporter((string?)reference.Attribute("Include")));

        var assetsPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "obj",
            directory,
            "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"Missing restored dependency graph: {assetsPath}");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));

        Assert.DoesNotContain(
            assets.RootElement.GetProperty("libraries").EnumerateObject(),
            library => IsExporter(library.Name.Split('/')[0]));
    }

    private static bool IsExporter(string? packageId) =>
        packageId?.StartsWith("OpenTelemetry.Exporter.", StringComparison.OrdinalIgnoreCase) == true ||
        packageId?.Contains("Prometheus", StringComparison.OrdinalIgnoreCase) == true;

    private static string Include(XElement element) =>
        (string)element.Attribute("Include")!;

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
