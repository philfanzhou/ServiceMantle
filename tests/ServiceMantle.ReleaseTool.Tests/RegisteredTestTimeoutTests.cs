using System.Diagnostics;
using System.Globalization;
using ServiceMantle.ReleaseTool;
using Xunit;

namespace ServiceMantle.ReleaseTool.Tests;

public sealed class RegisteredTestTimeoutTests
{
    private const string HangingProcessEnvironmentVariable =
        "SERVICEMANTLE_RELEASETOOL_HANG_AFTER_TESTS";

    [Fact]
    public async Task Registered_test_uses_the_fixed_timeout_and_project_diagnostic_directory()
    {
        const string project = "tests/Example.Tests/Example.Tests.csproj";
        const string sensitiveEnvironmentValue = "database-password-in-environment";
        var calls = new List<DotnetCall>();
        var runner = Runner(calls);

        await runner.RunAsync(
            Registry(new RegisteredTest
            {
                Project = project,
                Environment = new Dictionary<string, string>
                {
                    ["EXAMPLE_TEST_SETTING"] = sensitiveEnvironmentValue,
                },
            }),
            TestContext.Current.CancellationToken);

        var call = Assert.Single(calls);
        Assert.Equal(RegisteredTestRunner.ProjectTestTimeout, Option(call.Arguments, "--timeout"));
        Assert.Contains("--diagnostic", call.Arguments);
        Assert.Contains("--diagnostic-synchronous-write", call.Arguments);
        Assert.Equal(
            Path.Combine(
                "artifacts",
                "test-diagnostics",
                "tests",
                "Example.Tests",
                "Example.Tests.csproj",
                "test"),
            Option(call.Arguments, "--diagnostic-output-directory"));
        Assert.Equal(sensitiveEnvironmentValue, call.Environment!["EXAMPLE_TEST_SETTING"]);
        Assert.DoesNotContain(
            sensitiveEnvironmentValue,
            string.Join('\n', call.Arguments),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_database_discovery_has_its_own_timeout_and_diagnostic_directory()
    {
        const string project = "tests/Example.Database.Tests/Example.Database.Tests.csproj";
        var calls = new List<DotnetCall>();
        var runner = Runner(calls);

        await runner.RunAsync(
            Registry(new RegisteredTest
            {
                Project = project,
                RealDatabase = true,
                Environment = new Dictionary<string, string>
                {
                    ["RUN_SERVICEMANTLE_EXAMPLE_TESTS"] = "true",
                },
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, calls.Count);
        var discovery = calls[0];
        Assert.Contains("--list-tests", discovery.Arguments);
        Assert.Equal(
            RegisteredTestRunner.RealDatabaseDiscoveryTimeout,
            Option(discovery.Arguments, "--timeout"));
        Assert.Equal(
            Path.Combine(
                "artifacts",
                "test-diagnostics",
                "tests",
                "Example.Database.Tests",
                "Example.Database.Tests.csproj",
                "list-tests"),
            Option(discovery.Arguments, "--diagnostic-output-directory"));
        Assert.Null(discovery.Environment);

        var test = calls[1];
        Assert.DoesNotContain("--list-tests", test.Arguments);
        Assert.Equal(RegisteredTestRunner.ProjectTestTimeout, Option(test.Arguments, "--timeout"));
        Assert.NotEqual(
            Option(discovery.Arguments, "--diagnostic-output-directory"),
            Option(test.Arguments, "--diagnostic-output-directory"));
    }

    [Fact]
    public void Diagnostic_directory_normalizes_repository_separators()
    {
        var expected = RegisteredTestRunner.DiagnosticDirectory(
            "tests/Example.Tests/Example.Tests.csproj",
            RegisteredTestExecution.Test);

        var actual = RegisteredTestRunner.DiagnosticDirectory(
            "tests\\Example.Tests\\Example.Tests.csproj",
            RegisteredTestExecution.Test);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/tmp/sensitive-project.csproj")]
    [InlineData("../sensitive-project.csproj")]
    [InlineData("tests//sensitive-project.csproj")]
    [InlineData("C:\\sensitive-project.csproj")]
    public void Invalid_diagnostic_project_path_is_rejected_without_echoing_it(string project)
    {
        var exception = Assert.Throws<ReleaseToolException>(() =>
            RegisteredTestRunner.DiagnosticDirectory(project, RegisteredTestExecution.Test));

        Assert.Equal(
            "A registered test project path cannot be mapped to a diagnostic directory.",
            exception.Message);
        Assert.DoesNotContain(project, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_timeout_is_a_normal_pipeline_failure_and_preserves_mtp_diagnostics()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(HangingProcessEnvironmentVariable),
                "true",
                StringComparison.Ordinal))
        {
            Assert.Skip("The nested timeout fixture cannot recursively launch itself.");
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var diagnosticDirectory = Path.Combine(temporaryDirectory.Path, "diagnostics");
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(
            root,
            "tests",
            "ServiceMantle.ReleaseTool.Tests",
            "ServiceMantle.ReleaseTool.Tests.csproj");
        var runner = new DotnetProcessRunner();
        var environment = new Dictionary<string, string>
        {
            [HangingProcessEnvironmentVariable] = "true",
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => runner.RunAsync(
            root,
            [
                "test",
                "--project",
                projectPath,
                "--configuration",
                "Release",
                "--no-build",
                "--no-restore",
                "--filter-method",
                $"{typeof(HangingProcessFixtureTests).FullName}.Completed_test_can_leave_a_foreground_thread",
                "--minimum-expected-tests",
                "1",
                "--timeout",
                "1s",
                "--diagnostic",
                "--diagnostic-synchronous-write",
                "--diagnostic-output-directory",
                diagnosticDirectory,
                "--no-ansi",
                "--progress",
                "off",
            ],
            environment,
            deadline.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Contains("exit code", exception.Message, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.EnumerateFiles(diagnosticDirectory, "*.diag", SearchOption.AllDirectories));
        Assert.All(
            Directory.EnumerateFiles(diagnosticDirectory, "*.diag", SearchOption.AllDirectories),
            path => Assert.True(new FileInfo(path).Length > 0, $"Diagnostic file {path} was empty."));
    }

    [Fact]
    public async Task Caller_cancellation_kills_the_dotnet_process_tree_and_remains_cancellation()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This process-tree fixture requires a POSIX shell.");
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var childPidPath = Path.Combine(temporaryDirectory.Path, "child.pid");
        var scriptPath = Path.Combine(temporaryDirectory.Path, "dotnet-fixture.sh");
        await File.WriteAllTextAsync(
            scriptPath,
            "#!/bin/sh\nsleep 300 &\nchild_pid=$!\nprintf '%s' \"$child_pid\" > \"$1\"\nwait \"$child_pid\"\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var runner = new DotnetProcessRunner(scriptPath);
        using var cancellation = new CancellationTokenSource();
        var operation = runner.RunAsync(
            temporaryDirectory.Path,
            [childPidPath],
            null,
            cancellation.Token);

        await WaitForFileAsync(childPidPath, TestContext.Current.CancellationToken);
        var childPid = int.Parse(
            await File.ReadAllTextAsync(childPidPath, TestContext.Current.CancellationToken),
            CultureInfo.InvariantCulture);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(await WaitForExitAsync(childPid), $"Child process {childPid} was not cleaned up.");
    }

    [Fact]
    public async Task Nonzero_dotnet_exit_does_not_expose_test_environment_values()
    {
        const string sensitiveEnvironmentValue = "release-tool-environment-secret";
        var runner = new DotnetProcessRunner();

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => runner.RunAsync(
            FindRepositoryRoot(),
            ["--servicemantle-invalid-command"],
            new Dictionary<string, string>
            {
                ["SERVICEMANTLE_SENSITIVE_TEST_VALUE"] = sensitiveEnvironmentValue,
            },
            TestContext.Current.CancellationToken));

        Assert.Contains("exit code", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveEnvironmentValue, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pipeline_classifies_caller_cancellation_separately_from_runner_failure()
    {
        using var cancellationError = new StringWriter(CultureInfo.InvariantCulture);
        using var runnerError = new StringWriter(CultureInfo.InvariantCulture);

        var cancellationExitCode = Program.ReportFailure(
            new OperationCanceledException(),
            cancellationError);
        var runnerExitCode = Program.ReportFailure(
            new ReleaseToolException("Registered test runner timed out."),
            runnerError);

        Assert.Equal(130, cancellationExitCode);
        Assert.Equal(1, runnerExitCode);
        Assert.Equal("Package pipeline cancelled.", cancellationError.ToString().Trim());
        Assert.Equal("Registered test runner timed out.", runnerError.ToString().Trim());
    }

    [Fact]
    public void Ci_uploads_only_the_fixed_diagnostic_root_after_failure_or_cancellation()
    {
        var workflow = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains("if: ${{ failure() || cancelled() }}", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "name: test-diagnostics-${{ github.run_id }}-${{ github.run_attempt }}",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("path: artifacts/test-diagnostics", workflow, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: ignore", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "name: test-diagnostics-${{ env.",
            workflow,
            StringComparison.Ordinal);
    }

    private static RegisteredTestRunner Runner(List<DotnetCall> calls) => new(
        _ => throw new InvalidOperationException("Docker inspection was not expected."),
        (arguments, environment, _) =>
        {
            calls.Add(new DotnetCall([.. arguments], environment));
            return Task.CompletedTask;
        });

    private static PackageRegistry Registry(RegisteredTest test) => new()
    {
        Packages =
        [
            new RegisteredPackage
            {
                Tests = [test],
            },
        ],
    };

    private static string Option(IReadOnlyList<string> arguments, string name)
    {
        var index = -1;
        for (var argumentIndex = 0; argumentIndex < arguments.Count; argumentIndex++)
        {
            if (string.Equals(arguments[argumentIndex], name, StringComparison.Ordinal))
            {
                index = argumentIndex;
                break;
            }
        }

        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }

    private static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        Assert.Fail("The child process fixture did not publish its PID.");
    }

    private static async Task<bool> WaitForExitAsync(int processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        return false;
    }

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

    private sealed record DotnetCall(
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string>? Environment);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"servicemantle-release-tool-timeout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

public sealed class HangingProcessFixtureTests
{
    [Fact]
    public void Completed_test_can_leave_a_foreground_thread()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SERVICEMANTLE_RELEASETOOL_HANG_AFTER_TESTS"),
                "true",
                StringComparison.Ordinal))
        {
            return;
        }

        var thread = new Thread(() => Thread.Sleep(Timeout.InfiniteTimeSpan))
        {
            IsBackground = false,
        };
        thread.Start();
    }
}
