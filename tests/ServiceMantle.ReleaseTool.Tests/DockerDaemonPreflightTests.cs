using System.Diagnostics;
using System.Runtime.Versioning;
using ServiceMantle.ReleaseTool;
using Xunit;

namespace ServiceMantle.ReleaseTool.Tests;

public sealed class DockerDaemonPreflightTests
{
    private const long MinimumMemoryBytes = 2_147_483_648;

    [Fact]
    public void Registered_sql_server_projects_declare_the_common_daemon_contract()
    {
        var root = FindRepositoryRoot();
        var registry = PackageRegistry.Load(Path.Combine(root, "eng", "packages.json"));
        var sqlServerTests = registry.Packages
            .SelectMany(package => package.Tests)
            .Where(test => test.Environment.ContainsKey("RUN_SERVICEMANTLE_SQLSERVER_TESTS"))
            .ToArray();

        Assert.Equal(2, sqlServerTests.Length);
        Assert.All(sqlServerTests, test =>
        {
            var requirement = Assert.IsType<RegisteredDockerDaemonRequirement>(test.DockerDaemon);
            Assert.Equal("linux", requirement.OsType);
            Assert.Equal(["amd64", "x86_64"], requirement.Architectures);
            Assert.Equal(MinimumMemoryBytes, requirement.MinimumMemoryBytes);
        });
    }

    [Fact]
    public async Task Supported_remote_daemon_is_inspected_once_before_two_sql_server_projects()
    {
        var events = new List<string>();
        var inspectionCount = 0;
        var runner = new RegisteredTestRunner(
            _ =>
            {
                inspectionCount++;
                events.Add("docker");
                return Task.FromResult(SupportedDaemon());
            },
            (arguments, _, _) =>
            {
                events.Add($"dotnet:{arguments[2]}:{arguments.Contains("--list-tests")}");
                return Task.CompletedTask;
            });

        await runner.RunAsync(
            Registry(SqlServerTest("first.csproj"), SqlServerTest("second.csproj")),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, inspectionCount);
        Assert.Equal(
            [
                "docker",
                "dotnet:first.csproj:True",
                "dotnet:first.csproj:False",
                "dotnet:second.csproj:True",
                "dotnet:second.csproj:False",
            ],
            events);
    }

    [Theory]
    [InlineData("arm64")]
    [InlineData("aarch64")]
    public async Task Arm64_daemon_fails_before_any_sql_server_dotnet_process(string architecture)
    {
        var dotnetCalls = 0;
        var runner = new RegisteredTestRunner(
            _ => Task.FromResult(new DockerDaemonInfo
            {
                OsType = "linux",
                Architecture = architecture,
                MemTotal = MinimumMemoryBytes,
            }),
            (_, _, _) =>
            {
                dotnetCalls++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => runner.RunAsync(
            Registry(SqlServerTest("first.csproj"), SqlServerTest("second.csproj")),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, dotnetCalls);
        Assert.Contains("Architecture=amd64/x86_64", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"Architecture={architecture}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insufficient_memory_is_rejected_with_a_non_sensitive_summary()
    {
        var exception = Assert.Throws<ReleaseToolException>(() => DockerDaemonInspector.Validate(
            Requirement(),
            new DockerDaemonInfo
            {
                OsType = "linux",
                Architecture = "amd64",
                MemTotal = MinimumMemoryBytes - 1,
            }));

        Assert.Contains($"MemTotal>={MinimumMemoryBytes} bytes", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"MemTotal={MinimumMemoryBytes - 1} bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_linux_daemon_is_rejected()
    {
        var exception = Assert.Throws<ReleaseToolException>(() => DockerDaemonInspector.Validate(
            Requirement(),
            new DockerDaemonInfo
            {
                OsType = "windows",
                Architecture = "amd64",
                MemTotal = MinimumMemoryBytes,
            }));

        Assert.Contains("requires OSType=linux", exception.Message, StringComparison.Ordinal);
        Assert.Contains("observed OSType=windows", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_daemon_does_not_expose_docker_output()
    {
        const string sensitiveOutput = "DOCKER_HOST=tcp://sensitive.example:2375";
        var inspector = new DockerDaemonInspector((_, _) =>
            Task.FromResult(new CapturedProcessResult(1, sensitiveOutput)));

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => inspector.InspectAsync(
            Directory.GetCurrentDirectory(),
            TestContext.Current.CancellationToken));

        Assert.Equal(
            "Docker daemon preflight failed because docker info could not query the daemon.",
            exception.Message);
        Assert.DoesNotContain(sensitiveOutput, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Docker_info_shape_is_parsed_from_the_minimal_template()
    {
        var info = DockerDaemonInspector.Parse(
            "{\"OSType\":\"linux\",\"Architecture\":\"x86_64\",\"MemTotal\":2147483648}\n");

        Assert.Equal("linux", info.OsType);
        Assert.Equal("x86_64", info.Architecture);
        Assert.Equal(MinimumMemoryBytes, info.MemTotal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"MemTotal\":0}")]
    [InlineData("{\"OSType\":\"linux secret\",\"Architecture\":\"amd64\",\"MemTotal\":2147483648}")]
    public void Invalid_docker_info_is_rejected_without_echoing_output(string output)
    {
        var exception = Assert.Throws<ReleaseToolException>(() => DockerDaemonInspector.Parse(output));

        Assert.Equal(
            "Docker daemon preflight failed because docker info returned invalid OS, architecture, or memory data.",
            exception.Message);
        if (output.Length > 0)
        {
            Assert.DoesNotContain(output, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_by_docker_inspection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var inspector = new DockerDaemonInspector((_, token) =>
            Task.FromCanceled<CapturedProcessResult>(token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspector.InspectAsync(
            Directory.GetCurrentDirectory(),
            cancellation.Token));
    }

    [Fact]
    public async Task Captured_process_cancellation_kills_the_child_process_tree()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This process-tree fixture requires a POSIX shell.");
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var childPidPath = Path.Combine(temporaryDirectory.Path, "child.pid");
        var startInfo = await CreateProcessFixtureAsync(temporaryDirectory.Path, childPidPath);

        await RunProcessFixtureAsync(startInfo, async (cancellation, operation) =>
        {
            await WaitForFileAsync(
                childPidPath,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var childPid = int.Parse(
                await File.ReadAllTextAsync(childPidPath, TestContext.Current.CancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.True(
                await WaitForExitAsync(childPid, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                $"Child process {childPid} was not cleaned up.");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_pid_publication_cleans_up_the_process_on_timeout_or_wait_cancellation(bool cancelWait)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This process-tree fixture requires a POSIX shell.");
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var childPidPath = Path.Combine(temporaryDirectory.Path, "child.pid");
        // Publish only to an observation channel so the expected PID file never appears.
        var observedPidPath = Path.Combine(temporaryDirectory.Path, "observed.pid");
        var startInfo = await CreateProcessFixtureAsync(temporaryDirectory.Path, observedPidPath);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task<CapturedProcessResult>? observedOperation = null;
        var childPid = 0;

        var exception = await Record.ExceptionAsync(() => RunProcessFixtureAsync(
            startInfo,
            async (_, operation) =>
            {
                observedOperation = operation;
                await WaitForFileAsync(
                    observedPidPath,
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken);
                childPid = int.Parse(
                    await File.ReadAllTextAsync(observedPidPath, TestContext.Current.CancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                var pidWait = WaitForFileAsync(
                    childPidPath,
                    cancelWait ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100),
                    waitCancellation.Token);
                if (cancelWait)
                {
                    waitCancellation.Cancel();
                }

                await pidWait;
            }));

        if (cancelWait)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
        else
        {
            Assert.IsType<TimeoutException>(exception);
        }

        Assert.NotNull(observedOperation);
        Assert.True(observedOperation.IsCanceled);
        Assert.False(File.Exists(childPidPath));
        Assert.True(
            await WaitForExitAsync(childPid, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            $"Child process {childPid} was not cleaned up after the PID wait failed.");
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<ProcessStartInfo> CreateProcessFixtureAsync(string directory, string childPidPath)
    {
        var scriptPath = Path.Combine(directory, "docker-info-fixture.sh");
        await File.WriteAllTextAsync(
            scriptPath,
            "#!/bin/sh\nsleep 300 &\nchild_pid=$!\nprintf '%s' \"$child_pid\" > \"$1.tmp\"\nmv \"$1.tmp\" \"$1\"\nwait \"$child_pid\"\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var startInfo = new ProcessStartInfo(scriptPath);
        startInfo.ArgumentList.Add(childPidPath);
        return startInfo;
    }

    private static async Task RunProcessFixtureAsync(
        ProcessStartInfo startInfo,
        Func<CancellationTokenSource, Task<CapturedProcessResult>, Task> inspect)
    {
        using var cancellation = new CancellationTokenSource();
        var operation = CapturedProcessRunner.RunAsync(startInfo, cancellation.Token);

        try
        {
            await inspect(cancellation, operation);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await operation;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Observe cancellation only after the process and redirected streams have exited.
            }
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        throw new TimeoutException("The child process fixture did not publish its PID.");
    }

    private static async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        return false;
    }

    private static PackageRegistry Registry(params RegisteredTest[] tests) => new()
    {
        Packages =
        [
            new RegisteredPackage
            {
                Tests = [.. tests],
            },
        ],
    };

    private static RegisteredTest SqlServerTest(string project) => new()
    {
        Project = project,
        RealDatabase = true,
        DockerDaemon = Requirement(),
        Environment = new Dictionary<string, string>
        {
            ["RUN_SERVICEMANTLE_SQLSERVER_TESTS"] = "true",
        },
    };

    private static RegisteredDockerDaemonRequirement Requirement() => new()
    {
        OsType = "linux",
        Architectures = ["amd64", "x86_64"],
        MinimumMemoryBytes = MinimumMemoryBytes,
    };

    private static DockerDaemonInfo SupportedDaemon() => new()
    {
        OsType = "linux",
        Architecture = "amd64",
        MemTotal = MinimumMemoryBytes,
    };

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

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"servicemantle-release-tool-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
