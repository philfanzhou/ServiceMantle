using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceMantle.ReleaseTool;

internal sealed class DockerDaemonInspector
{
    private const string DockerInfoFormat =
        "{\"OSType\":{{json .OSType}},\"Architecture\":{{json .Architecture}},\"MemTotal\":{{json .MemTotal}}}";

    private readonly Func<ProcessStartInfo, CancellationToken, Task<CapturedProcessResult>> runProcess;

    internal DockerDaemonInspector()
        : this(CapturedProcessRunner.RunAsync)
    {
    }

    internal DockerDaemonInspector(
        Func<ProcessStartInfo, CancellationToken, Task<CapturedProcessResult>> runProcess)
    {
        this.runProcess = runProcess;
    }

    internal async Task<DockerDaemonInfo> InspectAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add(DockerInfoFormat);

        CapturedProcessResult result;
        try
        {
            result = await runProcess(startInfo, cancellationToken);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new ReleaseToolException(
                "Docker daemon preflight failed because docker info could not be started.");
        }

        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException(
                "Docker daemon preflight failed because docker info could not query the daemon.");
        }

        return Parse(result.StandardOutput);
    }

    internal static DockerDaemonInfo Parse(string output)
    {
        DockerDaemonInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<DockerDaemonInfo>(output);
        }
        catch (JsonException)
        {
            throw InvalidOutput();
        }

        if (info is null ||
            !IsSafeIdentifier(info.OsType) ||
            !IsSafeIdentifier(info.Architecture) ||
            info.MemTotal <= 0)
        {
            throw InvalidOutput();
        }

        return info;
    }

    internal static void Validate(
        RegisteredDockerDaemonRequirement requirement,
        DockerDaemonInfo info)
    {
        var observed = ObservedSummary(info);
        if (!string.Equals(info.OsType, requirement.OsType, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseToolException(
                $"Docker daemon preflight failed: requires OSType={requirement.OsType}; observed {observed}.");
        }

        if (!requirement.Architectures.Contains(
                info.Architecture,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new ReleaseToolException(
                $"Docker daemon preflight failed: requires Architecture={string.Join('/', requirement.Architectures)}; observed {observed}.");
        }

        if (info.MemTotal < requirement.MinimumMemoryBytes)
        {
            throw new ReleaseToolException(
                $"Docker daemon preflight failed: requires MemTotal>={requirement.MinimumMemoryBytes} bytes; observed {observed}.");
        }
    }

    private static bool IsSafeIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string ObservedSummary(DockerDaemonInfo info) =>
        $"OSType={info.OsType}, Architecture={info.Architecture}, MemTotal={info.MemTotal} bytes";

    private static ReleaseToolException InvalidOutput() => new(
        "Docker daemon preflight failed because docker info returned invalid OS, architecture, or memory data.");
}

internal sealed class DockerDaemonInfo
{
    [JsonPropertyName("OSType")]
    public string OsType { get; init; } = string.Empty;

    public string Architecture { get; init; } = string.Empty;

    public long MemTotal { get; init; }
}
