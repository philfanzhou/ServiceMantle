using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Otlp.Tests;

public sealed class PackageDependencyBoundaryTests
{
    [Fact]
    public void Otlp_package_is_isolated_and_base_package_has_no_exporter_dependency()
    {
        var root = FindRepositoryRoot();
        var otlpProject = XDocument.Load(Path.Combine(
            root,
            "src",
            "ServiceMantle.OpenTelemetry.Otlp",
            "ServiceMantle.OpenTelemetry.Otlp.csproj"));
        var dependencies = otlpProject.Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Concat(otlpProject.Descendants("ProjectReference").Select(element =>
                Path.GetFileNameWithoutExtension(
                    ((string?)element.Attribute("Include"))!
                        .Replace('\\', Path.DirectorySeparatorChar))))
            .Order(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "OpenTelemetry.Exporter.OpenTelemetryProtocol", "ServiceMantle.OpenTelemetry" }
                .Order(StringComparer.OrdinalIgnoreCase),
            dependencies,
            StringComparer.OrdinalIgnoreCase);

        foreach (var package in new[]
                 {
                     "ServiceMantle",
                     "ServiceMantle.AspNetCore",
                     "ServiceMantle.OpenTelemetry",
                 })
        {
            var assetsPath = Path.Combine(
                root,
                "artifacts",
                "obj",
                package,
                "project.assets.json");
            Assert.True(File.Exists(assetsPath));
            using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
            Assert.DoesNotContain(
                assets.RootElement.GetProperty("libraries").EnumerateObject(),
                library => library.Name.StartsWith(
                    "OpenTelemetry.Exporter.OpenTelemetryProtocol/",
                    StringComparison.OrdinalIgnoreCase));
        }
    }

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
