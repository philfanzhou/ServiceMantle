namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Creates an isolated temporary directory for one test and removes it on dispose.
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
    }

    public string Path { get; }

    public static TemporaryDirectory Create() =>
        new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ServiceMantle.Tests",
            Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
