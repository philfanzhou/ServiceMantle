namespace ServiceMantle.ReleaseTool;

internal sealed class RegisteredTestRunner
{
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
                    [
                        "test",
                        "--project",
                        test.Project,
                        "--configuration",
                        "Release",
                        "--no-build",
                        "--no-restore",
                        "--list-tests",
                        "--filter-trait",
                        "Category=RealDatabase",
                        "--minimum-expected-tests",
                        "1",
                    ],
                    null,
                    cancellationToken);
            }

            var arguments = new List<string>
            {
                "test",
                "--project",
                test.Project,
                "--configuration",
                "Release",
                "--no-build",
                "--no-restore",
                "--minimum-expected-tests",
                "1",
            };
            if (test.RealDatabase)
            {
                arguments.AddRange(["--zero-tests-policy", "strict", "--fail-skips", "on"]);
            }

            await runDotnet(arguments, test.Environment, cancellationToken);
        }
    }
}
