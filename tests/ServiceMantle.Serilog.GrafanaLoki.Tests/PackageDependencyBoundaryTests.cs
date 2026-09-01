using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ServiceMantle.Serilog.GrafanaLoki.Tests;

public sealed class PackageDependencyBoundaryTests
{
    [Theory]
    [InlineData("ServiceMantle", "ServiceMantle.csproj")]
    [InlineData("ServiceMantle.AspNetCore", "ServiceMantle.AspNetCore.csproj")]
    [InlineData("ServiceMantle.Serilog", "ServiceMantle.Serilog.csproj")]
    public void Existing_packages_have_no_Grafana_Loki_driver_dependency(
        string directory,
        string projectFile)
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(repositoryRoot, "src", directory, projectFile));
        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
            IsRemoteDriver((string?)reference.Attribute("Include")));
        Assert.DoesNotContain(project.Descendants("ProjectReference"), reference =>
            IsRemoteDriver((string?)reference.Attribute("Include")));

        var assetsPath = Path.Combine(repositoryRoot, "artifacts", "obj", directory, "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"Missing restored dependency graph: {assetsPath}");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        Assert.DoesNotContain(
            assets.RootElement.GetProperty("libraries").EnumerateObject(),
            library => IsRemoteDriver(library.Name.Split('/')[0]));
    }

    [Fact]
    public void New_package_has_only_the_base_integration_and_one_remote_driver()
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "ServiceMantle.Serilog.GrafanaLoki",
            "ServiceMantle.Serilog.GrafanaLoki.csproj"));
        var dependencies = project.Descendants("PackageReference")
            .Select(reference => (string)reference.Attribute("Include")!)
            .Concat(project.Descendants("ProjectReference").Select(reference =>
                Path.GetFileNameWithoutExtension(
                    ((string)reference.Attribute("Include")!).Replace('\\', Path.DirectorySeparatorChar))))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            new[] { "Serilog.Sinks.Grafana.Loki", "ServiceMantle.Serilog" },
            dependencies,
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(project.Descendants("FrameworkReference"));
    }

    private static bool IsRemoteDriver(string? value) =>
        value?.Contains("GrafanaLoki", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("Grafana.Loki", StringComparison.OrdinalIgnoreCase) == true;

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
