using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class PackageDependencyBoundaryTests
{
    [Fact]
    public void RegisteredPackages_DeclareExactlyTheirRegisteredDependencies()
    {
        var repositoryRoot = FindRepositoryRoot();
        var registry = LoadRegistry(repositoryRoot);
        var packageIdsByProject = registry.Packages.ToDictionary(
            package => FullPath(repositoryRoot, package.Project),
            package => package.Id,
            StringComparer.OrdinalIgnoreCase);

        foreach (var package in registry.Packages)
        {
            var projectPath = FullPath(repositoryRoot, package.Project);
            var project = XDocument.Load(projectPath);
            var actualDependencies = project
                .Descendants("PackageReference")
                .Select(Include)
                .Concat(project.Descendants("ProjectReference").Select(reference =>
                {
                    var referencedProject = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(projectPath)!,
                        Include(reference).Replace('\\', Path.DirectorySeparatorChar)));
                    return packageIdsByProject[referencedProject];
                }));
            var actualFrameworkReferences = project
                .Descendants("FrameworkReference")
                .Select(Include);

            Assert.Equal(
                package.Dependencies.Order(StringComparer.OrdinalIgnoreCase),
                actualDependencies.Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(
                package.FrameworkReferences.Order(StringComparer.OrdinalIgnoreCase),
                actualFrameworkReferences.Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void OptionalPackageArchitecture_IsDataDrivenAndEveryPackageHasTests()
    {
        var repositoryRoot = FindRepositoryRoot();
        var registry = LoadRegistry(repositoryRoot);

        Assert.Contains(registry.Packages, package => package.Optional);
        Assert.All(registry.Packages, package =>
        {
            Assert.False(string.IsNullOrWhiteSpace(package.Id));
            Assert.True(File.Exists(FullPath(repositoryRoot, package.Project)));
            Assert.NotEmpty(package.Tests);
            Assert.All(package.Tests, test =>
                Assert.True(File.Exists(FullPath(repositoryRoot, test.Project))));
        });
        Assert.Equal(
            registry.Packages.Count,
            registry.Packages.Select(package => package.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            registry.Packages.SelectMany(package => package.Tests).Count(),
            registry.Packages
                .SelectMany(package => package.Tests)
                .Select(test => test.Project)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public void PackMetadata_IsCentralizedForEveryRegisteredPackage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var registry = LoadRegistry(repositoryRoot);
        var sharedProperties = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props"));

        Assert.Equal("MIT", Property(sharedProperties, "PackageLicenseExpression"));
        Assert.Equal(
            "https://github.com/philfanzhou/ServiceMantle",
            Property(sharedProperties, "RepositoryUrl"));
        Assert.Equal("git", Property(sharedProperties, "RepositoryType"));
        Assert.Equal("true", Property(sharedProperties, "PublishRepositoryUrl"));
        Assert.Equal("true", Property(sharedProperties, "IncludeSymbols"));
        Assert.Equal("snupkg", Property(sharedProperties, "SymbolPackageFormat"));
        Assert.Equal("README.md", Property(sharedProperties, "PackageReadmeFile"));

        foreach (var package in registry.Packages)
        {
            var project = XDocument.Load(FullPath(repositoryRoot, package.Project));
            Assert.Empty(project.Descendants("PackageLicenseExpression"));
            Assert.Empty(project.Descendants("RepositoryUrl"));
            Assert.Equal(package.Id, Property(project, "PackageId"));
            Assert.Equal("true", Property(project, "IsPackable"));
        }
    }

    [Fact]
    public void CiAndReleaseWorkflows_UseTheRegistryDrivenPipelineWithoutPerPackagePackSteps()
    {
        var repositoryRoot = FindRepositoryRoot();
        foreach (var workflowName in new[] { "ci.yml", "release.yml" })
        {
            var workflow = File.ReadAllText(
                Path.Combine(repositoryRoot, ".github", "workflows", workflowName));

            Assert.Contains("eng/ServiceMantle.ReleaseTool/ServiceMantle.ReleaseTool.csproj", workflow);
            Assert.Contains("-- pack", workflow);
            Assert.Contains("-- verify", workflow);
            Assert.DoesNotContain("dotnet pack src/", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("Pack ServiceMantle", workflow, StringComparison.Ordinal);
        }
    }

    private static PackageRegistry LoadRegistry(string repositoryRoot)
    {
        using var stream = File.OpenRead(Path.Combine(repositoryRoot, "eng", "packages.json"));
        return JsonSerializer.Deserialize<PackageRegistry>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }

    private static string Include(XElement element) =>
        (string)element.Attribute("Include")!;

    private static string? Property(XDocument project, string name) =>
        project.Descendants(name).Select(element => element.Value.Trim()).FirstOrDefault();

    private static string FullPath(string repositoryRoot, string relativePath) =>
        Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));

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

    private sealed class PackageRegistry
    {
        public List<RegisteredPackage> Packages { get; init; } = [];
    }

    private sealed class RegisteredPackage
    {
        public string Id { get; init; } = string.Empty;

        public string Project { get; init; } = string.Empty;

        public bool Optional { get; init; }

        public List<string> Dependencies { get; init; } = [];

        public List<string> FrameworkReferences { get; init; } = [];

        public List<RegisteredTest> Tests { get; init; } = [];
    }

    private sealed class RegisteredTest
    {
        public string Project { get; init; } = string.Empty;
    }
}
