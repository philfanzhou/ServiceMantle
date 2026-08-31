using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Tests;

public sealed class PackageDependencyBoundaryTests
{
    [Fact]
    public void Core_instrumentation_package_has_no_exporter_dependency()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "ServiceMantle.OpenTelemetry",
            "ServiceMantle.OpenTelemetry.csproj");
        var project = XDocument.Load(projectPath);

        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
            IsExporter((string?)reference.Attribute("Include")));

        var assetsPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "obj",
            "ServiceMantle.OpenTelemetry",
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
