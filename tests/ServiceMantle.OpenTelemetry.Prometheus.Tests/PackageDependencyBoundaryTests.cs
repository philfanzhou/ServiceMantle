using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Prometheus.Tests;

public sealed class PackageDependencyBoundaryTests
{
    [Theory]
    [InlineData("ServiceMantle", "ServiceMantle.csproj")]
    [InlineData("ServiceMantle.AspNetCore", "ServiceMantle.AspNetCore.csproj")]
    [InlineData("ServiceMantle.OpenTelemetry", "ServiceMantle.OpenTelemetry.csproj")]
    public void Existing_packages_have_no_Prometheus_dependency(string directory, string projectFile)
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", directory, projectFile);
        var project = XDocument.Load(projectPath);

        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
            IsPrometheus((string?)reference.Attribute("Include")));
        Assert.DoesNotContain(project.Descendants("ProjectReference"), reference =>
            IsPrometheus((string?)reference.Attribute("Include")));

        var assetsPath = Path.Combine(repositoryRoot, "artifacts", "obj", directory, "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"Missing restored dependency graph: {assetsPath}");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        Assert.DoesNotContain(
            assets.RootElement.GetProperty("libraries").EnumerateObject(),
            library => IsPrometheus(library.Name.Split('/')[0]));
    }

    private static bool IsPrometheus(string? value) =>
        value?.Contains("Prometheus", StringComparison.OrdinalIgnoreCase) == true;

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
