using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Runs one Bootstrap credential consumption in a separate operating-system process.
/// </summary>
/// <remarks>
/// Two <see cref="BootstrapCredentialFileStore"/> instances inside one process share a runtime and
/// prove nothing about a cross-process claim, so the one-time guarantee is verified by real child
/// processes contending for the same record path. The module initializer runs before the test
/// runner's entry point, so the same test executable serves as its own worker.
/// </remarks>
internal static class BootstrapCredentialWorker
{
    internal const string RecordPathVariable = "SERVICEMANTLE_BOOTSTRAP_CREDENTIAL_WORKER_RECORD";
    internal const string BootstrapPathVariable = "SERVICEMANTLE_BOOTSTRAP_CREDENTIAL_WORKER_BOOTSTRAP";
    internal const string CandidateVariable = "SERVICEMANTLE_BOOTSTRAP_CREDENTIAL_WORKER_CANDIDATE";
    internal const string BarrierPathVariable = "SERVICEMANTLE_BOOTSTRAP_CREDENTIAL_WORKER_BARRIER";

    internal const int ConsumedExitCode = 0;
    internal const int InvalidExitCode = 3;
    internal const int UnavailableExitCode = 4;

    [ModuleInitializer]
    internal static void Initialize()
    {
        var recordPath = Environment.GetEnvironmentVariable(RecordPathVariable);
        if (string.IsNullOrEmpty(recordPath))
        {
            return;
        }

        Environment.Exit(Run(recordPath));
    }

    /// <summary>Starts one worker process that waits on the barrier before consuming.</summary>
    internal static Process Start(
        string recordPath,
        string bootstrapPath,
        string barrierPath,
        string candidate)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var processPath = Environment.ProcessPath!;
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = processPath;
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(assemblyPath);
        }
        else
        {
            startInfo.FileName = processPath;
        }

        startInfo.Environment[RecordPathVariable] = recordPath;
        startInfo.Environment[BootstrapPathVariable] = bootstrapPath;
        startInfo.Environment[BarrierPathVariable] = barrierPath;
        startInfo.Environment[CandidateVariable] = candidate;
        return Process.Start(startInfo)!;
    }

    private static int Run(string recordPath)
    {
        try
        {
            var barrierPath = Environment.GetEnvironmentVariable(BarrierPathVariable)!;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(barrierPath) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1);
            }

            var store = new BootstrapCredentialFileStore(
                ServiceId.Parse("credential-worker"),
                recordPath,
                Environment.GetEnvironmentVariable(BootstrapPathVariable));
            var result = store
                .ConsumeAsync(Environment.GetEnvironmentVariable(CandidateVariable))
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (result.IsConsumed)
            {
                return ConsumedExitCode;
            }

            return result.ErrorCode == WellKnownBootstrapCredentialErrorCodes.Invalid
                ? InvalidExitCode
                : UnavailableExitCode;
        }
        catch (Exception)
        {
            return UnavailableExitCode;
        }
    }
}
