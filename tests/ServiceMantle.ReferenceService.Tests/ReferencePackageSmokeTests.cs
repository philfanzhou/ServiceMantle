using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Npgsql;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Database.Sqlite;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Runs the reference service as a consumer of the actual packed artifacts: the sample source is
/// copied out of the repository, restored and built purely from a local ReleaseTool pack feed,
/// and then run as a real process on both declared deployment paths.
/// </summary>
/// <remarks>
/// <para>
/// The whole class is opt-in through <c>RUN_SERVICEMANTLE_PACKAGING_TESTS=true</c>, on the same
/// require-rather-than-assume policy as the real-database gates: when the variable is set, every
/// step below must succeed or the test fails; when it is not set, every test skips. The fixture
/// reuses the repository's Release build outputs and only re-packs them, so the feed holds the
/// same physical assemblies the pipeline would publish.
/// </para>
/// <para>
/// The installation main path (setup code, completion, login, readiness) is wired by the sample
/// only behind the PostgreSQL gate, so that path is smoked against a real PostgreSQL server while
/// the SQLite path owns the dependency-isolation and disabled-P1 observations that need no
/// server. Nothing here verifies what the in-process suites already cover.
/// </para>
/// </remarks>
public sealed class ReferencePackageSmokeTests : IClassFixture<ReferencePackageSmokeFixture>
{
    private const string SetupPath = "/management/v1/setup";
    private const string LoginPath = "/management/v1/session/login";
    private const string ReadyPath = "/health/ready";
    private const string CodeBannerAnchor = "one-time setup code:";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly ReferencePackageSmokeFixture fixture;

    public ReferencePackageSmokeTests(ReferencePackageSmokeFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task The_packaged_restore_carries_exactly_the_declared_dependency_set()
    {
        fixture.RequireGate();
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(fixture.AssetsFilePath, Token));

        // The direct set is the sample's own declaration: the provider-neutral core and the P0
        // capabilities it runs, plus the two EF providers of its own, plus the P1 packages the
        // sample itself opts into as explicit compile-time references.
        var direct = document.RootElement.GetProperty("project")
            .GetProperty("frameworks")
            .EnumerateObject()
            .SelectMany(framework => framework.Value.GetProperty("dependencies").EnumerateObject())
            .Select(dependency => dependency.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ReferencePackageSmokeFixture.CorePackageIds
                .Concat(ReferencePackageSmokeFixture.OptionalPackageIds)
                .Concat(["Microsoft.EntityFrameworkCore.Sqlite", "Npgsql.EntityFrameworkCore.PostgreSQL"])
                .OrderBy(name => name, StringComparer.Ordinal),
            direct);

        // Every ServiceMantle package in the transitive closure is one the sample declared: no
        // undeclared provider or capability rides along through a dependency edge.
        var closure = document.RootElement.GetProperty("libraries")
            .EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "package")
            .Select(library => library.Name.Split('/')[0])
            .Where(name => name.StartsWith("ServiceMantle", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            ReferencePackageSmokeFixture.CorePackageIds.Concat(ReferencePackageSmokeFixture.OptionalPackageIds)
                .OrderBy(name => name, StringComparer.Ordinal),
            closure);

        // And every declared ServiceMantle package resolved to exactly the packed version.
        var resolvedVersions = document.RootElement.GetProperty("libraries")
            .EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "package")
            .Select(library => library.Name.Split('/', 2))
            .ToDictionary(parts => parts[0], parts => parts[1]);
        foreach (var package in ReferencePackageSmokeFixture.CorePackageIds
            .Concat(ReferencePackageSmokeFixture.OptionalPackageIds))
        {
            Assert.Equal(fixture.PackageVersion, resolvedVersions[package]);
        }
    }

    [Fact]
    public async Task The_sqlite_deployment_path_runs_from_the_packed_artifacts()
    {
        fixture.RequireGate();
        using var working = TemporaryDirectory.Create();
        await using var service = fixture.StartPackagedProcess(working.Path,
            "--" + ReferenceSqliteStartupOptions.DatabasePathKey, working.DatabasePath,
            "--" + ReferenceSqliteStartupOptions.EnabledKey, "true",
            "--" + ReferenceSqliteStartupOptions.DeploymentModeKey, "SingleInstance",
            "--" + ReferenceSqliteStartupOptions.PrepareIfMissingKey, "true");

        var address = await service.WaitUntilListeningAsync(Token);
        using var client = new HttpClient { BaseAddress = address, Timeout = ReferenceServiceBudgets.Request };
        using var response = await client.GetAsync("/", Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("skeleton", body, StringComparison.Ordinal);
        // The packaged first start migrated its own target before it served.
        Assert.True(File.Exists(working.DatabasePath));
        await StopAndAssertItWentAwayAsync(service);

        AssertOnlyLoopbackListening(service);
        AssertNoOptionalCapabilityActivity(service);
        // No external database driver initializes on this path and nothing connects outward:
        // the only store the process touched is the SQLite file beside its own working directory.
        Assert.DoesNotContain("Npgsql", service.Output, StringComparison.OrdinalIgnoreCase);
        foreach (var text in new[] { service.Output, body })
        {
            Assert.DoesNotContain(working.DatabasePath, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Data Source", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_postgresql_installation_path_completes_from_the_packed_artifacts()
    {
        fixture.RequireGate();
        var container = new PostgreSqlBuilder(fixture.GetPostgresImage())
            .WithDatabase("reference_package_smoke")
            .WithUsername("reference_package_owner")
            .WithPassword("synthetic-package-smoke-postgres-secret")
            .Build();
        try
        {
            await RunPostgresqlInstallationPathAsync(container);
        }
        finally
        {
            await container.StopAsync(Token);
            await container.DisposeAsync();
        }
    }

    private async Task RunPostgresqlInstallationPathAsync(PostgreSqlContainer container)
    {
        await container.StartAsync(Token);

        // The target names a database the server does not hold yet, so the packaged first start
        // exercises the authorized prepare-then-migrate-then-initialize sequence on its own.
        var administrative = container.GetConnectionString();
        var target = new NpgsqlConnectionStringBuilder(administrative)
        {
            Database = "reference_package_smoke_target",
        }.ConnectionString;
        using var working = TemporaryDirectory.Create();
        await using var service = fixture.StartPackagedProcess(working.Path,
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, target,
            "--" + ReferencePostgreSqlStartupOptions.AdministrativeConnectionStringKey, administrative,
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "true",
            "--ReferenceService:Management:RootKey", ReferencePackageSmokeFixture.RootKey,
            "--ReferenceService:Management:Operators:0:Id", "ops-admin",
            "--ReferenceService:Management:Operators:0:DisplayName", "Ops Admin",
            "--ReferenceService:Management:Operators:0:Permissions", "management.read,management.admin",
            "--ReferenceService:Management:Operators:0:Credential",
            ReferencePackageSmokeFixture.AdminCredential);

        var address = await service.WaitUntilListeningAsync(Token);
        var code = await WaitForSetupCodeAsync(service);
        Assert.Equal(32, code.Length);

        var responses = new List<string>();
        using var client = new HttpClient { BaseAddress = address, Timeout = ReferenceServiceBudgets.Request };

        // The one-shot installation completes over the packaged HTTP surface.
        using (var completion = await client.SendAsync(PostJson(
            SetupPath,
            "{\"code\":\"" + code + "\"}"),
            Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, completion.StatusCode);
            responses.Add(await completion.Content.ReadAsStringAsync(Token));
        }

        using (var login = await client.SendAsync(PostJson(
            LoginPath,
            $$"""{"username":"ops-admin","secret":"{{ReferencePackageSmokeFixture.AdminCredential}}"}"""),
            Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
            Assert.Single(login.Headers.GetValues("Set-Cookie"));
            responses.Add(await login.Content.ReadAsStringAsync(Token));
        }

        using (var ready = await client.GetAsync(ReadyPath, Token))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            responses.Add(await ready.Content.ReadAsStringAsync(Token));
        }

        await StopAndAssertItWentAwayAsync(service);

        AssertOnlyLoopbackListening(service);
        AssertNoOptionalCapabilityActivity(service);
        // The plaintext code lives in the console banner and nowhere else; the same holds for the
        // deployment root key, the operator credential, and the database secrets.
        var banner = string.Join(
            Environment.NewLine,
            service.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(line => !line.Equals(CodeBannerAnchor, StringComparison.Ordinal))
                .Take(2));
        Assert.Contains(code, banner, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(service.Output, code));
        foreach (var text in responses)
        {
            Assert.DoesNotContain(code, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ReferencePackageSmokeFixture.RootKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ReferencePackageSmokeFixture.AdminCredential, text, StringComparison.Ordinal);
        }

        foreach (var text in responses.Append(service.Output))
        {
            Assert.DoesNotContain("synthetic-package-smoke-postgres-secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain(target, text, StringComparison.Ordinal);
            Assert.DoesNotContain(administrative, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Builds the JSON POST the management surface requires: the request marker that keeps the
    /// credential-bearing body out of the composed logging, and nothing else.
    /// </summary>
    private static HttpRequestMessage PostJson(string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-ServiceMantle-Request", "1");
        return request;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static async Task StopAndAssertItWentAwayAsync(ReferenceServiceProcess service)
    {
        var exitCode = await service.ShutDownAsync(Token);

        Assert.True(service.HasExited);
        if (ReferenceServiceProcess.SupportsGracefulShutdownSignal)
        {
            // Where an operator's stop request can be delivered, the packaged host owns its own
            // shutdown exactly like the repository build does.
            Assert.Equal(0, exitCode);
        }
    }

    private static void AssertOnlyLoopbackListening(ReferenceServiceProcess service)
    {
        foreach (var line in service.Output.Split('\n'))
        {
            if (line.Contains("Now listening on:", StringComparison.Ordinal))
            {
                Assert.Contains("http://127.0.0.1:", line, StringComparison.Ordinal);
            }
        }
    }

    private static void AssertNoOptionalCapabilityActivity(ReferenceServiceProcess service)
    {
        Assert.DoesNotContain("consul", service.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("opentelemetry", service.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("otlp", service.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> WaitForSetupCodeAsync(ReferenceServiceProcess service)
    {
        var deadline = DateTime.UtcNow + ReferenceServiceBudgets.Start;
        while (DateTime.UtcNow < deadline)
        {
            var lines = service.Output.Split(
                '\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var index = Array.IndexOf(lines, CodeBannerAnchor);
            if (index >= 0 && index + 1 < lines.Length)
            {
                return lines[index + 1];
            }

            Assert.False(
                service.HasExited,
                "the packaged host exited before printing the setup code:" + Environment.NewLine + service.Output);
            await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
        }

        throw new InvalidOperationException("the packaged host did not print the setup code in time");
    }
}

/// <summary>
/// Packs the repository's Release outputs once and turns them into the only NuGet feed a copied,
/// package-only sample restores from. Shared by every test of
/// <see cref="ReferencePackageSmokeTests"/>.
/// </summary>
public sealed class ReferencePackageSmokeFixture : IAsyncLifetime
{
    internal const string RootKey = "synthetic-package-smoke-root-key-0123456789";
    internal const string AdminCredential = "synthetic-package-smoke-admin-secret";

    private const string GateVariable = "RUN_SERVICEMANTLE_PACKAGING_TESTS";
    private const string SampleProjectName = "ServiceMantle.ReferenceService";
    private const string FeedPath = "artifacts/package-smoke-feed";

    /// <summary>The provider-neutral core and P0 capabilities the sample always consumes.</summary>
    internal static readonly string[] CorePackageIds =
    [
        "ServiceMantle",
        "ServiceMantle.AspNetCore",
        "ServiceMantle.Database.Sqlite",
        "ServiceMantle.Database.PostgreSql",
        "ServiceMantle.Persistence.EntityFrameworkCore",
    ];

    /// <summary>The P1 packages the sample opts into as explicit compile-time references.</summary>
    internal static readonly string[] OptionalPackageIds =
    [
        "ServiceMantle.Consul",
        "ServiceMantle.OpenTelemetry",
        "ServiceMantle.Serilog",
    ];

    private static readonly string[] AllPackageIds = [.. CorePackageIds, .. OptionalPackageIds];

    private TemporaryDirectory? workspace;
    private string? repositoryRoot;
    private string? appDirectory;

    internal string PackageVersion { get; private set; } = string.Empty;

    internal string AssetsFilePath { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!IsRequired())
        {
            return;
        }

        repositoryRoot = ResolveRepositoryRoot();
        PackageVersion = "0.0.0-local." + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var dotnet = ResolveSharedFrameworkHost();

        // Re-pack the repository's own Release outputs under one fresh version: the feed then
        // holds the physical assemblies a release would publish, without rebuilding anything. The
        // configuration directory beside this assembly's output carries its actual casing.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)).Name;
        var releaseTool = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "ServiceMantle.ReleaseTool",
            configuration,
            "ServiceMantle.ReleaseTool.dll");
        if (!File.Exists(releaseTool))
        {
            throw new InvalidOperationException(
                $"The ReleaseTool build output was not found at '{releaseTool}'. " +
                "Build the solution in Release before running the packaging smoke test.");
        }

        var feed = Path.Combine(repositoryRoot, FeedPath);
        if (Directory.Exists(feed))
        {
            Directory.Delete(feed, recursive: true);
        }

        await RunDotnetAsync(
            repositoryRoot,
            dotnet,
            ["exec", releaseTool, "pack", "--version", PackageVersion, "--commit", "local", "--output", FeedPath]);

        workspace = TemporaryDirectory.Create();
        var source = Path.Combine(workspace.Path, "src");
        CopyDirectory(Path.Combine(repositoryRoot, "samples", SampleProjectName), source);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "NuGet.config"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="ServiceMantleLocalFeed" value="{feed}" />
              </packageSources>
            </configuration>
            """,
            CancellationToken.None);

        // The copy stands outside the repository, so it gets the build properties and the pinned
        // external versions the repository's props would otherwise contribute, as explicit values.
        var properties = ReadDirectoryBuildProperties(repositoryRoot);
        var versions = ReadPinnedPackageVersions(repositoryRoot);
        await File.WriteAllTextAsync(
            Path.Combine(source, SampleProjectName + ".csproj"),
            BuildPackageOnlyProject(properties, versions),
            CancellationToken.None);

        await RunDotnetAsync(source, dotnet, ["restore"]);
        await RunDotnetAsync(source, dotnet, ["build", "--configuration", "Release", "--no-restore"]);

        appDirectory = Directory
            .EnumerateFiles(Path.Combine(source, "bin"), SampleProjectName + ".dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"The packaged sample build produced no {SampleProjectName}.dll under '{source}/bin'.");
        AssetsFilePath = Path.Combine(source, "obj", "project.assets.json");
        if (!File.Exists(AssetsFilePath))
        {
            throw new InvalidOperationException($"The packaged restore wrote no assets file at '{AssetsFilePath}'.");
        }
    }

    public ValueTask DisposeAsync()
    {
        workspace?.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static bool IsRequired() =>
        string.Equals(Environment.GetEnvironmentVariable(GateVariable), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fails the test unless the packaging gate was requested; otherwise skips it.</summary>
    internal void RequireGate()
    {
        if (IsRequired())
        {
            return;
        }

        Assert.Skip($"The packaging smoke test environment is opt-in: set {GateVariable}=true to run it.");
    }

    internal ReferenceServiceProcess StartPackagedProcess(string workingDirectory, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        var directory = appDirectory ?? throw new InvalidOperationException(
            "The packaged sample build is not available: the packaging gate did not run.");
        return ReferenceServiceProcess.Start(
            startInfo => ConfigurePackagedEntryPoint(directory, startInfo),
            workingDirectory,
            arguments);
    }

    internal string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    private static void ConfigurePackagedEntryPoint(string appDirectory, ProcessStartInfo startInfo)
    {
        var nativeHost = Path.Combine(
            appDirectory,
            SampleProjectName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        if (File.Exists(nativeHost))
        {
            startInfo.FileName = nativeHost;
            return;
        }

        startInfo.FileName = ResolveSharedFrameworkHost();
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.Combine(appDirectory, SampleProjectName + ".dll"));
    }

    private static string ResolveSharedFrameworkHost()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null &&
            Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    private static string ResolveRepositoryRoot()
    {
        // artifacts/bin/<this test project>/<configuration>/ -> the repository root.
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var candidate = testOutput.Parent?.Parent?.Parent?.Parent?.FullName;
        if (candidate is null ||
            !File.Exists(Path.Combine(candidate, "eng", "packages.json")))
        {
            throw new InvalidOperationException(
                "The repository root was not found above the test output directory: " + testOutput.FullName);
        }

        return candidate;
    }

    private static Dictionary<string, string> ReadDirectoryBuildProperties(string repositoryRoot)
    {
        var document = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props"));
        return document.Descendants("PropertyGroup").SelectMany(group => group.Elements())
            .ToDictionary(element => element.Name.LocalName, element => element.Value);
    }

    private static Dictionary<string, string> ReadPinnedPackageVersions(string repositoryRoot)
    {
        var document = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"));
        return document.Descendants("PackageVersion")
            .ToDictionary(
                element => (string?)element.Attribute("Include") ?? string.Empty,
                element => (string?)element.Attribute("Version") ?? string.Empty);
    }

    private string BuildPackageOnlyProject(
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, string> versions)
    {
        var builder = new StringBuilder()
            .AppendLine("<Project Sdk=\"Microsoft.NET.Sdk.Web\">")
            .AppendLine("  <PropertyGroup>")
            .AppendLine($"    <TargetFramework>{properties["TargetFramework"]}</TargetFramework>")
            .AppendLine($"    <LangVersion>{properties["LangVersion"]}</LangVersion>")
            .AppendLine($"    <Nullable>{properties["Nullable"]}</Nullable>")
            .AppendLine($"    <ImplicitUsings>{properties["ImplicitUsings"]}</ImplicitUsings>")
            .AppendLine("    <IsPackable>false</IsPackable>")
            .AppendLine("  </PropertyGroup>")
            .AppendLine("  <ItemGroup>");
        foreach (var external in new[]
        {
            "Microsoft.EntityFrameworkCore.Sqlite",
            "Npgsql.EntityFrameworkCore.PostgreSQL",
        })
        {
            builder.AppendLine(
                $"    <PackageReference Include=\"{external}\" Version=\"{versions[external]}\" />");
        }

        foreach (var package in AllPackageIds)
        {
            builder.AppendLine(
                $"    <PackageReference Include=\"{package}\" Version=\"{PackageVersion}\" />");
        }

        return builder
            .AppendLine("  </ItemGroup>")
            .AppendLine("</Project>")
            .ToString();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static async Task RunDotnetAsync(string workingDirectory, string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"'{fileName}' could not be started.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(eventArgs.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(eventArgs.Data);
                }
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"'{fileName} {string.Join(' ', arguments)}' did not finish in time:{Environment.NewLine}{output}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}:" +
                Environment.NewLine + output);
        }
    }
}
