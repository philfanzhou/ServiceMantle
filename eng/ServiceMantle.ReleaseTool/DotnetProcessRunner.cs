using System.ComponentModel;
using System.Diagnostics;

namespace ServiceMantle.ReleaseTool;

internal sealed class DotnetProcessRunner
{
    private readonly string executable;

    internal DotnetProcessRunner(string executable = "dotnet")
    {
        this.executable = executable;
    }

    internal async Task RunAsync(
        string root,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var variable in environment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ??
                throw new ReleaseToolException("The dotnet process could not be started.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new ReleaseToolException("The dotnet process could not be started.");
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process won the race to exit after cancellation.
                }

                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }

            if (process.ExitCode != 0)
            {
                var operation = arguments.Count > 0 ? arguments[0] : "command";
                throw new ReleaseToolException(
                    $"dotnet {operation} failed for a registered project with exit code {process.ExitCode}.");
            }
        }
    }
}
