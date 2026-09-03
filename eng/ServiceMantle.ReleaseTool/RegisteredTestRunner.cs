namespace ServiceMantle.ReleaseTool;

internal sealed class RegisteredTestRunner
{
    internal const string DiagnosticRoot = "artifacts/test-diagnostics";
    internal const string ProjectTestTimeout = "10m";
    internal const string RealDatabaseDiscoveryTimeout = "2m";

    private readonly Func<CancellationToken, Task<DockerDaemonInfo>> inspectDockerDaemon;
    private readonly Func<
        IReadOnlyList<string>,
        IReadOnlyDictionary<string, string>?,
        CancellationToken,
        Task> runDotnet;

    internal RegisteredTestRunner(
        Func<CancellationToken, Task<DockerDaemonInfo>> inspectDockerDaemon,
        Func<
            IReadOnlyList<string>,
            IReadOnlyDictionary<string, string>?,
            CancellationToken,
            Task> runDotnet)
    {
        this.inspectDockerDaemon = inspectDockerDaemon;
        this.runDotnet = runDotnet;
    }

    internal async Task RunAsync(
        PackageRegistry registry,
        CancellationToken cancellationToken)
    {
        DockerDaemonInfo? dockerDaemon = null;
        foreach (var test in registry.Packages.SelectMany(package => package.Tests))
        {
            if (test.DockerDaemon is not null)
            {
                dockerDaemon ??= await inspectDockerDaemon(cancellationToken);
                DockerDaemonInspector.Validate(test.DockerDaemon, dockerDaemon);
            }

            if (test.RealDatabase)
            {
                await runDotnet(
                    Arguments(test, RegisteredTestExecution.RealDatabaseDiscovery),
                    null,
                    cancellationToken);
            }

            await runDotnet(
                Arguments(test, RegisteredTestExecution.Test),
                test.Environment,
                cancellationToken);
        }
    }

    private static IReadOnlyList<string> Arguments(
        RegisteredTest test,
        RegisteredTestExecution execution)
    {
        var arguments = new List<string>
        {
            "test",
            "--project",
            test.Project,
            "--configuration",
            "Release",
            "--no-build",
            "--no-restore",
        };
        if (execution == RegisteredTestExecution.RealDatabaseDiscovery)
        {
            arguments.AddRange(
            [
                "--list-tests",
                "--filter-trait",
                "Category=RealDatabase",
            ]);
        }

        arguments.AddRange(
        [
            "--minimum-expected-tests",
            "1",
            "--timeout",
            execution switch
            {
                RegisteredTestExecution.Test => ProjectTestTimeout,
                RegisteredTestExecution.RealDatabaseDiscovery => RealDatabaseDiscoveryTimeout,
                _ => throw new ArgumentOutOfRangeException(nameof(execution)),
            },
            "--diagnostic",
            "--diagnostic-synchronous-write",
            "--diagnostic-output-directory",
            DiagnosticDirectory(test.Project, execution),
        ]);
        if (execution == RegisteredTestExecution.Test && test.RealDatabase)
        {
            arguments.AddRange(["--zero-tests-policy", "strict", "--fail-skips", "on"]);
        }

        return arguments;
    }

    internal static string DiagnosticDirectory(
        string project,
        RegisteredTestExecution execution)
    {
        var normalized = project.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith('/') ||
            (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw InvalidDiagnosticPath();
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Any(character => char.IsControl(character) || character == ':')))
        {
            throw InvalidDiagnosticPath();
        }

        var executionDirectory = execution switch
        {
            RegisteredTestExecution.Test => "test",
            RegisteredTestExecution.RealDatabaseDiscovery => "list-tests",
            _ => throw new ArgumentOutOfRangeException(nameof(execution)),
        };
        return Path.Combine([.. DiagnosticRoot.Split('/'), .. segments, executionDirectory]);
    }

    private static ReleaseToolException InvalidDiagnosticPath() =>
        new("A registered test project path cannot be mapped to a diagnostic directory.");
}

internal enum RegisteredTestExecution
{
    Test,
    RealDatabaseDiscovery,
}
