using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ServiceMantle.ReleaseTool;

// Keep a live group leader until the exit code has been received and the whole scope is killed.
// Looking up descendants after MTP exits cannot find children that have already been reparented.
internal static class TestProcessHost
{
    internal const string Command = "--test-process-host";

    internal static async Task<int> RunAsync(ProcessStartInfo target, CancellationToken cancellationToken)
    {
        try
        {
            return await RunCoreAsync(target, cancellationToken);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw HostFailure();
        }
    }

    private static async Task<int> RunCoreAsync(ProcessStartInfo target, CancellationToken cancellationToken)
    {
        var pipeName = $"sm-{Guid.NewGuid():N}";
        if (!OperatingSystem.IsWindows())
        {
            // A fixed short path also keeps the protocol independent of test TMPDIR values.
            pipeName = Path.Combine("/tmp", pipeName);
        }

        using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = target.WorkingDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(TestProcessHost).Assembly.Location);
        startInfo.ArgumentList.Add(Command);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(target.FileName);
        foreach (var argument in target.ArgumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in target.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var host = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        host.Exited += (_, _) => lifetime.Cancel();
        SafeFileHandle? job = null;
        var started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            started = host.Start();
            if (!started)
            {
                throw HostFailure();
            }

            // The host cannot start the target until we release the pipe gate below.
            if (OperatingSystem.IsWindows())
            {
                job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
                if (job.IsInvalid || !AssignProcessToJobObject(job, host.SafeHandle))
                {
                    throw HostFailure();
                }
            }

            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(30));
                await pipe.WaitForConnectionAsync(startup.Token);
            }

            await pipe.WriteAsync(new byte[] { 1 }, lifetime.Token);
            await pipe.FlushAsync(lifetime.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var result = await reader.ReadLineAsync(lifetime.Token);
            if (!int.TryParse(result, CultureInfo.InvariantCulture, out var exitCode))
            {
                throw HostFailure();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return exitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw HostFailure();
        }
        finally
        {
            try
            {
                if (started)
                {
                    // Success, runner failure, and caller cancellation share this cleanup boundary.
                    var scopeTerminated = true;
                    if (OperatingSystem.IsWindows())
                    {
                        scopeTerminated = job is null || job.IsInvalid || TerminateJobObject(job, 1);
                    }
                    else
                    {
                        scopeTerminated = Kill(-host.Id, 9) == 0 || Marshal.GetLastPInvokeError() == 3; // ESRCH
                    }

                    // Also covers cancellation before the host establishes its POSIX session/job.
                    if (!host.HasExited)
                    {
                        try
                        {
                            host.Kill(entireProcessTree: true);
                        }
                        catch (InvalidOperationException)
                        {
                            // The scope termination above won the race.
                        }
                    }

                    await host.WaitForExitAsync(CancellationToken.None);
                    if (!scopeTerminated)
                    {
                        throw new ReleaseToolException("The registered test process scope could not be terminated.");
                    }
                }
            }
            finally
            {
                job?.Dispose();
            }
        }
    }

    internal static async Task<int> RunHostAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() && Setsid() == -1)
        {
            throw HostFailure();
        }

        using var pipe = new NamedPipeClientStream(
            ".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30_000);
        var gate = new byte[1];
        if (await pipe.ReadAsync(gate) != 1 || gate[0] != 1)
        {
            throw HostFailure();
        }

        try
        {
            var startInfo = new ProcessStartInfo(args[2]) { UseShellExecute = false };
            foreach (var argument in args.Skip(3))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var target = Process.Start(startInfo) ?? throw HostFailure();
            await target.WaitForExitAsync();
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(target.ExitCode.ToString(CultureInfo.InvariantCulture));

            // Preserve the group identity even when the target has exited, until parent cleanup.
            _ = await pipe.ReadAsync(gate);
            return 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            throw HostFailure();
        }
    }

    private static ReleaseToolException HostFailure() =>
        new("The registered test process host could not start or complete its protocol.");

    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int Setsid();

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
