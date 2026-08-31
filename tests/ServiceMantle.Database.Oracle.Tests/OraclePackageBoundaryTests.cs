using System.Text.Json;
using System.Xml.Linq;
using ServiceMantle.Database.Oracle;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

public sealed class OraclePackageBoundaryTests
{
    private static readonly string[] ExpectedDependencies =
    [
        "Oracle.ManagedDataAccess.Core",
        "ServiceMantle"
    ];

    [Fact]
    public void Package_references_only_core_and_the_Oracle_driver()
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "ServiceMantle.Database.Oracle",
            "ServiceMantle.Database.Oracle.csproj"));
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
        Assert.Equal(
            "ServiceMantle.Database.Oracle",
            typeof(OracleBootstrapDatabaseProvider).Assembly.GetName().Name);
    }

    [Fact]
    public void Core_package_has_no_Oracle_or_optional_provider_dependency()
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
            dependency => dependency.Contains("Oracle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Registry_and_ci_fix_the_real_database_hard_fail_contract()
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
                element.GetProperty("id").GetString() == "ServiceMantle.Database.Oracle");
        var test = package.GetProperty("tests").EnumerateArray().Single();
        var workflow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            ".github",
            "workflows",
            "ci.yml"));

        Assert.True(package.GetProperty("optional").GetBoolean());
        Assert.Equal(
            ExpectedDependencies.Order(StringComparer.OrdinalIgnoreCase),
            package.GetProperty("dependencies").EnumerateArray()
                .Select(element => element.GetString()!)
                .Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.True(test.GetProperty("realDatabase").GetBoolean());
        Assert.Equal(
            "true",
            test.GetProperty("environment")
                .GetProperty("RUN_SERVICEMANTLE_ORACLE_TESTS")
                .GetString());
        Assert.Contains(
            "23.26.1.0-lite-amd64@sha256:ef1a38683b3783b80e033be6b8f2cb31299dcba5430514ec96e2e8f4f0307d15",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("--fail-skips on", workflow, StringComparison.Ordinal);
        Assert.Contains("--zero-tests-policy strict", workflow, StringComparison.Ordinal);
    }

    private static string Include(XElement element) => (string)element.Attribute("Include")!;

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
