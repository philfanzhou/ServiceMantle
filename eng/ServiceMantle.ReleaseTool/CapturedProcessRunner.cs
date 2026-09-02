using System.Diagnostics;

namespace ServiceMantle.ReleaseTool;

internal sealed record CapturedProcessResult(int ExitCode, string StandardOutput);

internal static class CapturedProcessRunner
{
    internal static async Task<CapturedProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("The child process could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

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
            await Task.WhenAll(standardOutput, standardError);
            throw;
        }

        await Task.WhenAll(standardOutput, standardError);
        return new CapturedProcessResult(process.ExitCode, await standardOutput);
    }
}
