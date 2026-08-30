using System.Text.Json;
using System.Xml.Linq;
using ServiceMantle.Database.Sqlite;
using Xunit;

namespace ServiceMantle.Database.Sqlite.Tests;

public sealed class PackageDependencyBoundaryTests
{
    private static readonly string[] ExpectedDependencies =
    [
        "Microsoft.Data.Sqlite",
        "ServiceMantle",
    ];

    [Fact]
    public void Package_can_be_referenced()
    {
        Assert.Equal(
            "ServiceMantle.Database.Sqlite",
            typeof(ServiceMantleSqlitePackage).Assembly.GetName().Name);
    }

    [Fact]
    public void Package_references_only_core_and_sqlite_driver()
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "ServiceMantle.Database.Sqlite",
            "ServiceMantle.Database.Sqlite.csproj"));
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
    public void Core_package_does_not_reference_sqlite_or_the_optional_package()
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
            dependency => dependency.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            dependencies,
            dependency => dependency.Contains(
                "ServiceMantle.Database.Sqlite",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Registry_marks_package_optional_and_assigns_default_tests()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot,
            "eng",
            "packages.json")));
        var package = registry.RootElement
            .GetProperty("packages")
            .EnumerateArray()
            .Single(element =>
                element.GetProperty("id").GetString() == "ServiceMantle.Database.Sqlite");
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
            "tests/ServiceMantle.Database.Sqlite.Tests/ServiceMantle.Database.Sqlite.Tests.csproj",
            test.GetProperty("project").GetString());
        Assert.Empty(test.GetProperty("environment").EnumerateObject());
        Assert.False(test.TryGetProperty("realDatabase", out _));
    }

    [Fact]
    public void Solution_and_ci_include_the_registry_driven_package_pipeline()
    {
        var repositoryRoot = FindRepositoryRoot();
        var solution = File.ReadAllText(Path.Combine(repositoryRoot, "ServiceMantle.slnx"));
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));

        Assert.Contains("src/ServiceMantle.Database.Sqlite/ServiceMantle.Database.Sqlite.csproj", solution);
        Assert.Contains(
            "tests/ServiceMantle.Database.Sqlite.Tests/ServiceMantle.Database.Sqlite.Tests.csproj",
            solution);
        Assert.Contains("eng/ServiceMantle.ReleaseTool/ServiceMantle.ReleaseTool.csproj", workflow);
        Assert.Contains("-- build", workflow);
        Assert.Contains("-- test", workflow);
        Assert.Contains("-- pack", workflow);
        Assert.Contains("-- verify", workflow);
        Assert.DoesNotContain(
            "dotnet pack src/ServiceMantle.Database.Sqlite",
            workflow,
            StringComparison.Ordinal);
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
