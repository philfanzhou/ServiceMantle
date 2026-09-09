using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Runs the reference service's own build output in a separate operating-system process for the
/// end-to-end deployment acceptance, and owns that process, its captured output and its shutdown.
/// </summary>
/// <remarks>
/// <para>
/// The point of this helper is that nothing about the host is substituted: the real entry point
/// starts a real Kestrel on a loopback dynamic port, so the evidence is an HTTP response, a process
/// exit code, the host's own console output, and the files the process left behind.
/// </para>
/// <para>
/// The budgets below are this test's cleanup budgets. They bound how long a test waits before it
/// reports a failure and reclaims the process; they are not a statement about how long the
/// reference service is allowed to take to start or stop in a deployment.
/// </para>
/// </remarks>
internal sealed partial class ReferenceServiceProcess : IAsyncDisposable
{
    /// <summary>The loopback binding every end-to-end start uses. Port 0 asks Kestrel to choose.</summary>
    internal const string DynamicLoopbackUrl = "http://127.0.0.1:0";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly Process process;
    private readonly List<string> lines = [];

    private ReferenceServiceProcess(Process process) => this.process = process;

    /// <summary>
    /// Gets whether this platform can ask the host to shut down the way an operator does. A POSIX
    /// signal reaches the host's own shutdown path; Windows offers no equivalent to a child process
    /// started this way, so there the process is reclaimed forcibly and no exit code is implied.
    /// </summary>
    internal static bool SupportsGracefulShutdownSignal => !OperatingSystem.IsWindows();

    /// <summary>Gets the console output captured so far, standard output and error interleaved.</summary>
    internal string Output
    {
        get
        {
            lock (lines)
            {
                return string.Join(Environment.NewLine, lines);
            }
        }
    }

    /// <summary>Gets whether the process has exited.</summary>
    internal bool HasExited => process.HasExited;

    /// <summary>
    /// Starts the reference service from its own build output.
    /// </summary>
    /// <param name="workingDirectory">
    /// The directory the process runs in. It becomes the host's content root, so a file the sample
    /// would create from a relative default lands under the directory the test inspects.
    /// </param>
    /// <param name="arguments">The command-line arguments after the loopback binding.</param>
    internal static ReferenceServiceProcess Start(string workingDirectory, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        ReferenceServiceBuildOutput.ConfigureEntryPoint(startInfo);
        startInfo.ArgumentList.Add("--urls=" + DynamicLoopbackUrl);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // A fixed, non-development environment: the acceptance is about the deployment path, and
        // the inherited environment of the test run must not decide it.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";

        var started = new ReferenceServiceProcess(
            Process.Start(startInfo) ??
            throw new InvalidOperationException("The reference service process could not be started."));
        started.process.OutputDataReceived += started.Capture;
        started.process.ErrorDataReceived += started.Capture;
        started.process.BeginOutputReadLine();
        started.process.BeginErrorReadLine();
        return started;
    }

    /// <summary>
    /// Waits until the real Kestrel reports the loopback address it bound, and returns it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The process exited before it listened, or the budget elapsed. The captured output is
    /// included so the failure is diagnosable.
    /// </exception>
    internal async Task<Uri> WaitUntilListeningAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ReferenceServiceBudgets.Start;
        while (DateTime.UtcNow < deadline)
        {
            if (TryReadListeningAddress() is { } address)
            {
                return address;
            }

            if (process.HasExited)
            {
                // Flush the readers before the output is reported.
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(Describe("exited before it listened"));
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(Describe("did not report a listening address in time"));
    }

    /// <summary>Waits for the process to exit on its own and returns its exit code.</summary>
    /// <exception cref="InvalidOperationException">The budget elapsed while it was still running.</exception>
    internal async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(ReferenceServiceBudgets.Exit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            budget.Token,
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(Describe("was still running"));
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Asks the running host to shut down the way an operator does and waits for the process to go
    /// away, returning its exit code where the platform can deliver that request.
    /// </summary>
    /// <returns>
    /// The exit code, or <see langword="null"/> on a platform without a graceful shutdown signal,
    /// where the process was reclaimed forcibly instead.
    /// </returns>
    internal async Task<int?> ShutDownAsync(CancellationToken cancellationToken)
    {
        if (!SupportsGracefulShutdownSignal)
        {
            Reclaim();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (NativeMethods.kill(process.Id, NativeMethods.Sigterm) != 0)
        {
            throw new InvalidOperationException(Describe(
                "could not be signalled: " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)));
        }

        return await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Reclaim();
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The process is gone; there is nothing left to reclaim.
        }

        process.OutputDataReceived -= Capture;
        process.ErrorDataReceived -= Capture;
        process.Dispose();
    }

    [GeneratedRegex(@"Now listening on: (?<address>http://127\.0\.0\.1:\d+)")]
    private static partial Regex ListeningAddress();

    private void Reclaim()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process won the race to exit.
        }
    }

    private Uri? TryReadListeningAddress()
    {
        lock (lines)
        {
            foreach (var line in lines)
            {
                if (ListeningAddress().Match(line) is { Success: true } match)
                {
                    return new Uri(match.Groups["address"].Value, UriKind.Absolute);
                }
            }
        }

        return null;
    }

    private void Capture(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        lock (lines)
        {
            lines.Add(e.Data);
        }
    }

    private string Describe(string what) => new StringBuilder()
        .Append("The reference service process ")
        .Append(what)
        .AppendLine(". Captured output:")
        .Append(Output)
        .ToString();

    private static class NativeMethods
    {
        internal const int Sigterm = 15;

        [DllImport("libc", SetLastError = true)]
        internal static extern int kill(int pid, int sig);
    }
}

/// <summary>The cleanup budgets of the end-to-end acceptance, not a startup or shutdown guarantee.</summary>
internal static class ReferenceServiceBudgets
{
    /// <summary>How long a test waits for a real start before it reports a failure.</summary>
    internal static TimeSpan Start => TimeSpan.FromSeconds(90);

    /// <summary>How long a test waits for a process to go away before it reports a failure.</summary>
    internal static TimeSpan Exit => TimeSpan.FromSeconds(60);

    /// <summary>How long a test waits for one loopback response before it reports a failure.</summary>
    internal static TimeSpan Request => TimeSpan.FromSeconds(30);
}

/// <summary>
/// Resolves the reference service's own build output beside this test assembly's own output.
/// </summary>
/// <remarks>
/// The two projects share one artifacts layout, so the sample's directory is the sibling of this
/// assembly's directory under the same configuration. Nothing is rebuilt or republished here: the
/// acceptance runs whatever the build produced.
/// </remarks>
internal static class ReferenceServiceBuildOutput
{
    private const string SampleAssemblyName = "ServiceMantle.ReferenceService";

    /// <summary>Gets the sample's build output directory.</summary>
    internal static string Directory { get; } = Resolve();

    /// <summary>Gets the managed entry assembly inside <see cref="Directory"/>.</summary>
    internal static string AssemblyPath { get; } =
        Path.Combine(Directory, SampleAssemblyName + ".dll");

    /// <summary>
    /// Points <paramref name="startInfo"/> at the sample's entry point: its own native host when
    /// the build produced one, and otherwise the shared framework host over its assembly.
    /// </summary>
    internal static void ConfigureEntryPoint(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var nativeHost = Path.Combine(
            Directory,
            SampleAssemblyName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        if (File.Exists(nativeHost))
        {
            startInfo.FileName = nativeHost;
            return;
        }

        startInfo.FileName = ResolveSharedFrameworkHost();
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(AssemblyPath);
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

    private static string Resolve()
    {
        // artifacts/bin/<this test project>/<configuration>/ -> artifacts/bin/<sample>/<configuration>/
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var configuration = testOutput.Name;
        var candidate = Path.Combine(
            testOutput.Parent?.Parent?.FullName ??
                throw new InvalidOperationException(
                    "The test output directory has no artifacts root: " + testOutput.FullName),
            SampleAssemblyName,
            configuration);
        if (!File.Exists(Path.Combine(candidate, SampleAssemblyName + ".dll")))
        {
            throw new InvalidOperationException(
                $"The reference service build output was not found at '{candidate}'. " +
                "Build the solution before running the end-to-end acceptance.");
        }

        return candidate;
    }
}
