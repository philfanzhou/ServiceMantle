using System.Text.Json;
using System.Xml.Linq;
using ServiceMantle.Serilog;
using Xunit;

namespace ServiceMantle.Serilog.Tests;

public sealed class PackageDependencyBoundaryTests
{
    private static readonly string[] ExpectedDependencies =
    [
        "Serilog",
        "Serilog.Extensions.Hosting",
        "Serilog.Sinks.Console",
        "ServiceMantle",
    ];

    [Fact]
    public void Package_can_be_referenced()
    {
        Assert.Equal("ServiceMantle.Serilog", typeof(ServiceMantleSerilogPackage).Assembly.GetName().Name);
    }

    [Fact]
    public void Package_references_only_core_and_serilog_host_console_dependencies()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "ServiceMantle.Serilog",
            "ServiceMantle.Serilog.csproj");
        var project = XDocument.Load(projectPath);
        var actualDependencies = project
            .Descendants("PackageReference")
            .Select(Include)
            .Concat(project.Descendants("ProjectReference").Select(reference =>
                Path.GetFileNameWithoutExtension(
                    Include(reference).Replace('\\', Path.DirectorySeparatorChar))));

        Assert.Equal(
            ExpectedDependencies.Order(StringComparer.OrdinalIgnoreCase),
            actualDependencies.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(project.Descendants("FrameworkReference"));
    }

    [Fact]
    public void Core_package_does_not_reference_serilog_or_the_optional_package()
    {
        var repositoryRoot = FindRepositoryRoot();
        var coreProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "ServiceMantle",
            "ServiceMantle.csproj"));
        var dependencies = coreProject
            .Descendants("PackageReference")
            .Concat(coreProject.Descendants("ProjectReference"))
            .Select(Include)
            .ToArray();

        Assert.DoesNotContain(
            dependencies,
            dependency => dependency.Contains("Serilog", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            dependencies,
            dependency => dependency.Contains("ServiceMantle.Serilog", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Registry_marks_package_optional_and_assigns_its_test_without_environment_variables()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot,
            "eng",
            "packages.json")));
        var package = registry.RootElement
            .GetProperty("packages")
            .EnumerateArray()
            .Single(element => element.GetProperty("id").GetString() == "ServiceMantle.Serilog");
        var dependencies = package
            .GetProperty("dependencies")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        var test = package.GetProperty("tests").EnumerateArray().Single();

        Assert.True(package.GetProperty("optional").GetBoolean());
        Assert.Equal(
            ExpectedDependencies.Order(StringComparer.OrdinalIgnoreCase),
            dependencies.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            "tests/ServiceMantle.Serilog.Tests/ServiceMantle.Serilog.Tests.csproj",
            test.GetProperty("project").GetString());
        Assert.Empty(test.GetProperty("environment").EnumerateObject());
    }

    [Fact]
    public void Ci_runs_the_registry_driven_build_test_pack_and_verify_pipeline()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));

        Assert.Contains("eng/ServiceMantle.ReleaseTool/ServiceMantle.ReleaseTool.csproj", workflow);
        Assert.Contains("-- build", workflow);
        Assert.Contains("-- test", workflow);
        Assert.Contains("-- pack", workflow);
        Assert.Contains("-- verify", workflow);
        Assert.DoesNotContain("dotnet pack src/ServiceMantle.Serilog", workflow, StringComparison.Ordinal);
    }

    private static string Include(XElement element) =>
        (string)element.Attribute("Include")!;

    private static string FindRepositoryRoot()
    {
        foreach (var startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "eng", "packages.json")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the package registry.");
    }
}
