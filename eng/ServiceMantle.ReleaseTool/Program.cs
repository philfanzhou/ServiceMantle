using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace ServiceMantle.ReleaseTool;

internal static class Program
{
    private const string ManifestPath = "eng/packages.json";
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly DotnetProcessRunner Dotnet = new();

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length >= 3 && args[0] == TestProcessHost.Command)
            {
                return await TestProcessHost.RunHostAsync(args);
            }

            var root = FindRepositoryRoot();
            var registry = PackageRegistry.Load(Path.Combine(root, ManifestPath));
            RegistryValidator.Validate(root, registry);

            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            switch (args[0])
            {
                case "validate":
                    Console.WriteLine($"Validated {registry.Packages.Count} registered packages.");
                    return 0;
                case "restore":
                    await RestoreAsync(root, registry, cancellation.Token);
                    return 0;
                case "build":
                    await BuildAsync(
                        root,
                        registry,
                        RequiredOption(args, "--version"),
                        RequiredOption(args, "--commit"),
                        cancellation.Token);
                    return 0;
                case "test":
                    await TestAsync(root, registry, cancellation.Token);
                    return 0;
                case "pack":
                    await PackAsync(
                        root,
                        registry,
                        RequiredOption(args, "--version"),
                        RequiredOption(args, "--commit"),
                        RequiredOption(args, "--output"),
                        cancellation.Token);
                    return 0;
                case "verify":
                    ArtifactVerifier.Verify(
                        root,
                        registry,
                        RequiredOption(args, "--version"),
                        RequiredOption(args, "--commit"),
                        RequiredOption(args, "--input"));
                    return 0;
                default:
                    throw new ReleaseToolException("The requested release-tool command is unknown.");
            }
        }
        catch (OperationCanceledException exception)
        {
            return ReportFailure(exception, Console.Error);
        }
        catch (ReleaseToolException exception)
        {
            return ReportFailure(exception, Console.Error);
        }
    }

    private static async Task RestoreAsync(
        string root,
        PackageRegistry registry,
        CancellationToken cancellationToken)
    {
        foreach (var project in AllProjects(registry))
        {
            await RunDotnetAsync(root, ["restore", project], null, cancellationToken);
        }
    }

    private static async Task BuildAsync(
        string root,
        PackageRegistry registry,
        string version,
        string commit,
        CancellationToken cancellationToken)
    {
        ValidatePipelineValue(version, "version");
        ValidatePipelineValue(commit, "commit");

        foreach (var project in AllProjects(registry))
        {
            await RunDotnetAsync(
                root,
                [
                    "build",
                    project,
                    "--configuration",
                    "Release",
                    "--no-restore",
                    $"-p:Version={version}",
                    $"-p:RepositoryCommit={commit}",
                ],
                null,
                cancellationToken);
        }
    }

    private static async Task TestAsync(
        string root,
        PackageRegistry registry,
        CancellationToken cancellationToken)
    {
        var dockerDaemonInspector = new DockerDaemonInspector();
        var testProcess = new DotnetProcessRunner(isolateProcessTree: true);
        var runner = new RegisteredTestRunner(
            cancellation => dockerDaemonInspector.InspectAsync(root, cancellation),
            (arguments, environment, cancellation) =>
                testProcess.RunAsync(root, arguments, environment, cancellation));
        await runner.RunAsync(registry, cancellationToken);
    }

    private static async Task PackAsync(
        string root,
        PackageRegistry registry,
        string version,
        string commit,
        string output,
        CancellationToken cancellationToken)
    {
        ValidatePipelineValue(version, "version");
        ValidatePipelineValue(commit, "commit");
        var outputPath = ResolvePath(root, output, "package output");
        Directory.CreateDirectory(outputPath);

        foreach (var package in registry.Packages)
        {
            await RunDotnetAsync(
                root,
                [
                    "pack",
                    package.Project,
                    "--configuration",
                    "Release",
                    "--no-build",
                    "--no-restore",
                    "--output",
                    outputPath,
                    $"-p:Version={version}",
                    $"-p:RepositoryCommit={commit}",
                ],
                null,
                cancellationToken);
        }
    }

    private static IEnumerable<string> AllProjects(PackageRegistry registry) =>
        registry.Packages
            .Select(package => package.Project)
            .Concat(registry.Packages.SelectMany(package => package.Tests).Select(test => test.Project))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static Task RunDotnetAsync(
        string root,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken) =>
        Dotnet.RunAsync(root, arguments, environment, cancellationToken);

    internal static int ReportFailure(Exception exception, TextWriter error)
    {
        switch (exception)
        {
            case OperationCanceledException:
                error.WriteLine("Package pipeline cancelled.");
                return 130;
            case ReleaseToolException releaseToolException:
                error.WriteLine(releaseToolException.Message);
                return 1;
            default:
                throw new ArgumentException("The package pipeline failure type is unsupported.", nameof(exception));
        }
    }

    private static string RequiredOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index == args.Length - 1 || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ReleaseToolException($"Required option {name} is missing.");
        }

        return args[index + 1];
    }

    private static void ValidatePipelineValue(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new ReleaseToolException($"The pipeline {description} is invalid.");
        }
    }

    internal static string ResolvePath(string root, string relativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ReleaseToolException($"The registered {description} path must be repository-relative.");
        }

        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            throw new ReleaseToolException($"The registered {description} path escapes the repository.");
        }

        return fullPath;
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, ManifestPath)))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new ReleaseToolException("Could not locate the package registry.");
    }

    private static void PrintUsage() => Console.WriteLine(
        "Commands: validate | restore | build --version V --commit SHA | test | " +
        "pack --version V --commit SHA --output PATH | " +
        "verify --version V --commit SHA --input PATH");

    internal static bool SetEquals(IEnumerable<string> left, IEnumerable<string> right) =>
        new HashSet<string>(left, IdComparer).SetEquals(right);
}

internal sealed class PackageRegistry
{
    public int SchemaVersion { get; init; }

    public List<RegisteredPackage> Packages { get; init; } = [];

    internal static PackageRegistry Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<PackageRegistry>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new ReleaseToolException("The package registry is empty.");
        }
        catch (JsonException)
        {
            throw new ReleaseToolException("The package registry is not valid JSON.");
        }
        catch (IOException)
        {
            throw new ReleaseToolException("The package registry could not be read.");
        }
    }
}

internal sealed class RegisteredPackage
{
    public string Id { get; init; } = string.Empty;

    public string Project { get; init; } = string.Empty;

    public bool Optional { get; init; }

    public List<string> Dependencies { get; init; } = [];

    public List<string> FrameworkReferences { get; init; } = [];

    public List<RegisteredTest> Tests { get; init; } = [];
}

internal sealed class RegisteredTest
{
    public string Project { get; init; } = string.Empty;

    public bool RealDatabase { get; init; }

    public Dictionary<string, string> Environment { get; init; } = [];

    public RegisteredDockerDaemonRequirement? DockerDaemon { get; init; }
}

internal sealed class RegisteredDockerDaemonRequirement
{
    public string OsType { get; init; } = string.Empty;

    public List<string> Architectures { get; init; } = [];

    public long MinimumMemoryBytes { get; init; }
}

internal static class RegistryValidator
{
    internal static void Validate(string root, PackageRegistry registry)
    {
        if (registry.SchemaVersion != 1 || registry.Packages.Count == 0)
        {
            throw new ReleaseToolException("The package registry schema is unsupported or contains no packages.");
        }

        EnsureUnique(registry.Packages.Select(package => package.Id), "package id");
        EnsureUnique(registry.Packages.Select(package => package.Project), "package project");
        EnsureUnique(
            registry.Packages.SelectMany(package => package.Tests).Select(test => test.Project),
            "test project");

        var packageIdsByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in registry.Packages)
        {
            ValidateIdentifier(package.Id, "package id");
            var projectPath = ExistingProject(root, package.Project, "package project");
            packageIdsByProject.Add(projectPath, package.Id);
        }

        foreach (var package in registry.Packages)
        {
            ValidatePackage(root, package, packageIdsByProject);
            if (package.Tests.Count == 0)
            {
                throw new ReleaseToolException("Every registered package must declare at least one test project.");
            }

            foreach (var test in package.Tests)
            {
                ValidateTest(root, test);
            }
        }
    }

    private static void ValidatePackage(
        string root,
        RegisteredPackage package,
        IReadOnlyDictionary<string, string> packageIdsByProject)
    {
        EnsureUnique(package.Dependencies, "package dependency");
        EnsureUnique(package.FrameworkReferences, "framework reference");
        var projectPath = ExistingProject(root, package.Project, "package project");
        var project = LoadProject(projectPath);
        var declaredId = Property(project, "PackageId") ?? Path.GetFileNameWithoutExtension(projectPath);
        if (!string.Equals(declaredId, package.Id, StringComparison.Ordinal))
        {
            throw new ReleaseToolException("A registered package id does not match its project metadata.");
        }

        var isPackable = Property(project, "IsPackable");
        if (!string.Equals(isPackable, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseToolException("A registered package project is not explicitly packable.");
        }

        var actualDependencies = project
            .Descendants("PackageReference")
            .Select(Include)
            .Concat(project.Descendants("ProjectReference").Select(reference =>
            {
                var referencedPath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(projectPath)!,
                    Include(reference).Replace('\\', Path.DirectorySeparatorChar)));
                return packageIdsByProject.TryGetValue(referencedPath, out var referencedId)
                    ? referencedId
                    : throw new ReleaseToolException(
                        "A package project references a project missing from the package registry.");
            }))
            .ToArray();
        if (!Program.SetEquals(actualDependencies, package.Dependencies))
        {
            throw new ReleaseToolException(
                "A package project's declared dependencies do not match the package registry.");
        }

        var actualFrameworkReferences = project
            .Descendants("FrameworkReference")
            .Select(Include)
            .ToArray();
        if (!Program.SetEquals(actualFrameworkReferences, package.FrameworkReferences))
        {
            throw new ReleaseToolException(
                "A package project's framework references do not match the package registry.");
        }
    }

    private static void ValidateTest(string root, RegisteredTest test)
    {
        var projectPath = ExistingProject(root, test.Project, "test project");
        var project = LoadProject(projectPath);
        if (!string.Equals(Property(project, "IsTestProject"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseToolException("A registered test project is not marked as a test project.");
        }

        foreach (var variable in test.Environment)
        {
            ValidateIdentifier(variable.Key, "test environment variable");
            if (string.IsNullOrWhiteSpace(variable.Value) || variable.Value.Any(char.IsControl))
            {
                throw new ReleaseToolException("A registered test environment value is invalid.");
            }
        }

        if (test.RealDatabase && !test.Environment.Any(variable =>
                variable.Key.StartsWith("RUN_SERVICEMANTLE_", StringComparison.Ordinal) &&
                variable.Key.EndsWith("_TESTS", StringComparison.Ordinal) &&
                string.Equals(variable.Value, "true", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReleaseToolException(
                "A registered real-database test must declare a required RUN_SERVICEMANTLE_*_TESTS environment variable.");
        }

        var requiresSqlServer = test.Environment.Any(variable =>
            string.Equals(variable.Key, "RUN_SERVICEMANTLE_SQLSERVER_TESTS", StringComparison.Ordinal) &&
            string.Equals(variable.Value, "true", StringComparison.OrdinalIgnoreCase));
        if (requiresSqlServer && test.DockerDaemon is null)
        {
            throw new ReleaseToolException(
                "A registered SQL Server test must declare Docker daemon requirements.");
        }

        if (test.DockerDaemon is not null)
        {
            ValidateDockerDaemonRequirement(test.DockerDaemon);
        }
    }

    private static void ValidateDockerDaemonRequirement(
        RegisteredDockerDaemonRequirement requirement)
    {
        ValidateIdentifier(requirement.OsType, "Docker daemon OS type");
        if (requirement.Architectures.Count == 0)
        {
            throw new ReleaseToolException(
                "A registered Docker daemon requirement must declare at least one architecture.");
        }

        EnsureUnique(requirement.Architectures, "Docker daemon architecture");
        foreach (var architecture in requirement.Architectures)
        {
            ValidateIdentifier(architecture, "Docker daemon architecture");
        }

        if (requirement.MinimumMemoryBytes <= 0)
        {
            throw new ReleaseToolException(
                "A registered Docker daemon minimum memory requirement is invalid.");
        }
    }

    private static string ExistingProject(string root, string relativePath, string description)
    {
        var path = Program.ResolvePath(root, relativePath, description);
        if (!File.Exists(path) || !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseToolException($"A registered {description} does not exist.");
        }

        return path;
    }

    private static XDocument LoadProject(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception exception) when (exception is IOException or System.Xml.XmlException)
        {
            throw new ReleaseToolException("A registered project could not be read.");
        }
    }

    private static string Include(XElement element) =>
        (string?)element.Attribute("Include") is { Length: > 0 } include
            ? include
            : throw new ReleaseToolException("A project dependency is missing its Include value.");

    private static string? Property(XDocument project, string name) =>
        project.Descendants(name).Select(element => element.Value.Trim()).FirstOrDefault();

    private static void EnsureUnique(IEnumerable<string> values, string description)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new ReleaseToolException($"The package registry contains a duplicate {description}.");
            }
        }
    }

    private static void ValidateIdentifier(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ReleaseToolException($"A registered {description} is invalid.");
        }
    }
}

internal static class ArtifactVerifier
{
    internal static void Verify(
        string root,
        PackageRegistry registry,
        string version,
        string commit,
        string input)
    {
        var inputPath = Program.ResolvePath(root, input, "package input");
        if (!Directory.Exists(inputPath))
        {
            throw new ReleaseToolException("The package input directory does not exist.");
        }

        var expectedFiles = registry.Packages
            .SelectMany(package => new[]
            {
                $"{package.Id}.{version}.nupkg",
                $"{package.Id}.{version}.snupkg",
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualFiles = Directory
            .EnumerateFiles(inputPath)
            .Select(path => Path.GetFileName(path)!)
            .Where(file => file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!Program.SetEquals(expectedFiles, actualFiles))
        {
            throw new ReleaseToolException(
                "Package artifacts do not exactly match the registered package and symbol set.");
        }

        foreach (var package in registry.Packages)
        {
            VerifyPackage(inputPath, registry, package, version, commit);
        }

        Console.WriteLine($"Verified {registry.Packages.Count} package and symbol artifact pairs.");
    }

    private static void VerifyPackage(
        string inputPath,
        PackageRegistry registry,
        RegisteredPackage package,
        string version,
        string commit)
    {
        var packagePath = Path.Combine(inputPath, $"{package.Id}.{version}.nupkg");
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = archive.Entries.SingleOrDefault(entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)) ??
            throw new ReleaseToolException("A package artifact does not contain exactly one nuspec.");
        using var stream = nuspecEntry.Open();
        var nuspec = XDocument.Load(stream);

        RequireElementValue(nuspec, "id", package.Id);
        RequireElementValue(nuspec, "version", version);
        RequireElementValue(nuspec, "license", "MIT");

        var repository = nuspec.Descendants().SingleOrDefault(element =>
            element.Name.LocalName == "repository") ??
            throw new ReleaseToolException("A package is missing repository metadata.");
        if (!string.Equals((string?)repository.Attribute("type"), "git", StringComparison.Ordinal) ||
            !string.Equals(
                (string?)repository.Attribute("url"),
                "https://github.com/philfanzhou/ServiceMantle",
                StringComparison.Ordinal) ||
            !string.Equals((string?)repository.Attribute("commit"), commit, StringComparison.Ordinal))
        {
            throw new ReleaseToolException("A package has incorrect repository metadata.");
        }

        var dependencyElements = nuspec
            .Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .ToArray();
        var actualDependencies = dependencyElements
            .Select(element => (string?)element.Attribute("id") ?? string.Empty)
            .ToArray();
        if (actualDependencies.Length != package.Dependencies.Count ||
            !Program.SetEquals(actualDependencies, package.Dependencies))
        {
            throw new ReleaseToolException("A package artifact contains undeclared or missing dependencies.");
        }

        var internalPackageIds = registry.Packages
            .Select(registeredPackage => registeredPackage.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencyElements)
        {
            var dependencyId = (string?)dependency.Attribute("id") ?? string.Empty;
            if (internalPackageIds.Contains(dependencyId) &&
                !string.Equals(
                    (string?)dependency.Attribute("version"),
                    version,
                    StringComparison.Ordinal))
            {
                throw new ReleaseToolException(
                    "An internal package dependency does not use the release version.");
            }
        }

        var actualFrameworkReferences = nuspec
            .Descendants()
            .Where(element => element.Name.LocalName == "frameworkReference")
            .Select(element => (string?)element.Attribute("name") ?? string.Empty)
            .ToArray();
        if (actualFrameworkReferences.Length != package.FrameworkReferences.Count ||
            !Program.SetEquals(actualFrameworkReferences, package.FrameworkReferences))
        {
            throw new ReleaseToolException("A package artifact contains incorrect framework references.");
        }
    }

    private static void RequireElementValue(XDocument document, string name, string expected)
    {
        var value = document.Descendants().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw new ReleaseToolException($"A package contains incorrect {name} metadata.");
        }
    }
}

internal sealed class ReleaseToolException(string message) : Exception(message);
