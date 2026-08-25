using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ServiceMantle.AspNetCore;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class PackageDependencyBoundaryTests
{
    private static readonly string[] ForbiddenDependencyPrefixes =
    [
        "Consul",
        "Microsoft.Data.SqlClient",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "OpenTelemetry",
        "Serilog"
    ];

    [Fact]
    public void Core_assembly_does_not_reference_AspNetCore_or_optional_drivers()
    {
        var references = typeof(ServiceId).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, IsForbiddenDependency);
    }

    [Fact]
    public void AspNetCore_assembly_does_not_reference_optional_drivers()
    {
        var references = typeof(ServiceMantleBuilder).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("ServiceMantle", references);
        Assert.DoesNotContain(references, IsForbiddenDependency);
    }

    [Fact]
    public void AspNetCore_package_project_has_only_core_project_and_shared_framework_dependencies()
    {
        var sourceDirectory = GetSourceDirectory();
        var repositoryRoot = Path.GetFullPath(Path.Combine(sourceDirectory, "..", ".."));
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "ServiceMantle.AspNetCore",
            "ServiceMantle.AspNetCore.csproj");
        var project = XDocument.Load(projectPath);

        Assert.Empty(project.Descendants("PackageReference"));
        var projectReference = Assert.Single(project.Descendants("ProjectReference"));
        Assert.EndsWith(
            Path.Combine("ServiceMantle", "ServiceMantle.csproj"),
            ((string?)projectReference.Attribute("Include"))?.Replace('\\', Path.DirectorySeparatorChar),
            StringComparison.Ordinal);
        var frameworkReference = Assert.Single(project.Descendants("FrameworkReference"));
        Assert.Equal("Microsoft.AspNetCore.App", (string?)frameworkReference.Attribute("Include"));
    }

    private static bool IsForbiddenDependency(string assemblyName) =>
        ForbiddenDependencyPrefixes.Any(prefix =>
            assemblyName.StartsWith(prefix, StringComparison.Ordinal));

    private static string GetSourceDirectory([CallerFilePath] string sourceFilePath = "") =>
        Path.GetDirectoryName(sourceFilePath)!;
}
