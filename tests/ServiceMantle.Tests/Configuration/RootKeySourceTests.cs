using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class RootKeySourceTests
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string FailureMessage = "The root key file could not be safely resolved.";

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("relative-secret.key")]
    [InlineData("secret\0.key")] [InlineData("//secret/share/key")]
    public void Injection_is_returned_unchanged_without_validating_unrelated_paths(string? path)
    {
        const string value = " \t injected-secret \r\n";
        var source = new RootKeySource(value, path);
        Assert.Same(value, source.Resolve());
        Assert.Same(value, source.Resolve());
        Assert.Equal("RootKeySource(Lazy=True)", source.ToString());
        Assert.Empty(typeof(RootKeySource).GetProperties());
    }

    [Fact]
    public void Injection_does_not_create_parent_or_touch_an_inaccessible_or_invalid_file()
    {
        using var fixture = new Fixture();
        var source = new RootKeySource("injected-secret", fixture.Path);
        Assert.False(Directory.Exists(fixture.Parent));
        Assert.Equal("injected-secret", source.Resolve());
        Assert.False(Directory.Exists(fixture.Parent));
        fixture.Write("damaged-secret");
        var bytes = File.ReadAllBytes(fixture.Path);
        var stamp = File.GetLastWriteTimeUtc(fixture.Path);
        using (var held = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("injected-secret", source.Resolve());
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(fixture.Path));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fixture.Parent, 0);
            try { Assert.Equal("injected-secret", source.Resolve()); }
            finally { File.SetUnixFileMode(fixture.Parent, Fixture.DirectoryMode); }
        }
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("\t\r\n")]
    [InlineData("\u0085\u00a0\u2003\u2028\u3000")]
    public void Missing_injection_defers_creation_and_reuses_the_file_winner(string? value)
    {
        using var fixture = new Fixture();
        var source = new RootKeySource(value, fixture.Path);
        Assert.False(Directory.Exists(fixture.Parent));
        var key = source.Resolve();
        Assert.Equal(32, Convert.FromBase64String(key).Length);
        Assert.Equal(key, source.Resolve());
        Assert.Equal(key, new RootKeySource(value, fixture.Path).Resolve());
        Assert.Equal(key, File.ReadAllText(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Parent));
        using var exclusive = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("relative-secret.key")]
    [InlineData("secret\0.key")]
    public void Invalid_fallback_path_is_rejected_only_on_resolution_with_original_safe_failure(string? path)
    {
        var source = new RootKeySource(null, path);
        Assert.Equal("RootKeySource(Lazy=True)", source.ToString());
        var first = SafeFailure(source, "secret");
        var second = SafeFailure(source, "secret");
        Assert.NotSame(first, second);
    }

    [Fact]
    public void File_results_and_failures_are_not_cached_or_rewritten()
    {
        using var fixture = new Fixture();
        fixture.Write(Key + "\r\n");
        var source = new RootKeySource(null, fixture.Path);
        var stamp = File.GetLastWriteTimeUtc(fixture.Path);
        Assert.Equal(Key, source.Resolve());
        Assert.Equal(Key + "\r\n", File.ReadAllText(fixture.Path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(fixture.Path));
        fixture.Write("damaged-secret");
        SafeFailure(source, fixture.Path);
        Assert.Equal("damaged-secret", File.ReadAllText(fixture.Path));
        // Only the test caller repairs its fixture; external replacement is not a production guarantee.
        fixture.Write(Key + "\r\n");
        Assert.Equal(Key, source.Resolve());
        Assert.Single(Directory.GetFiles(fixture.Parent));
        using var exclusive = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void File_permission_and_link_refusal_are_forwarded_without_repair()
    {
        using var fixture = new Fixture();
        fixture.Write(Key);
        var source = new RootKeySource(null, fixture.Path);
        if (!OperatingSystem.IsWindows())
        {
            var broad = Fixture.FileMode | UnixFileMode.GroupRead;
            File.SetUnixFileMode(fixture.Path, broad);
            SafeFailure(source, fixture.Path);
            Assert.Equal(broad, File.GetUnixFileMode(fixture.Path));
        }
        var target = fixture.Path + ".target";
        File.Move(fixture.Path, target);
        File.CreateSymbolicLink(fixture.Path, target);
        SafeFailure(source, fixture.Path);
        Assert.Equal(Key, File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(fixture.Parent, ".root-key-*.tmp"));
    }

    [Fact]
    public async Task Concurrent_calls_on_shared_and_separate_sources_return_the_same_winner()
    {
        using var fixture = new Fixture();
        var sources = new[] { new RootKeySource(null, fixture.Path), new RootKeySource(" ", fixture.Path) };
        var keys = await Task.WhenAll(Enumerable.Range(0, 24).Select(index =>
            Task.Run(() => sources[index % 2].Resolve(), TestContext.Current.CancellationToken)));
        Assert.Single(keys.Distinct());
        Assert.Equal(keys[0], File.ReadAllText(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Parent));
    }

    [Fact]
    public void Injection_validation_remains_the_protectors_responsibility()
    {
        const string malformed = "injected-secret\ud800";
        var source = new RootKeySource(malformed, "secret\0.key");
        Assert.Same(malformed, source.Resolve());
        var protector = new SensitiveValueProtector(ServiceId.Parse("source-tests"), "source");
        Assert.Throws<ArgumentException>(() => protector.Protect("payload", source.Resolve(), TestContext.Current.CancellationToken));
    }

    private static InvalidOperationException SafeFailure(RootKeySource source, string sentinel)
    {
        var error = Assert.Throws<InvalidOperationException>(() => source.Resolve());
        Assert.Equal(FailureMessage, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(sentinel, error.ToString());
        Assert.DoesNotContain(Key, error.ToString());
        Assert.DoesNotContain("damaged-secret", error.ToString());
        Assert.Equal("RootKeySource(Lazy=True)", source.ToString());
        return error;
    }

    private sealed class Fixture : IDisposable
    {
        internal const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        internal const UnixFileMode DirectoryMode = FileMode | UnixFileMode.UserExecute;
        private readonly string root = Canonical(Directory.CreateTempSubdirectory("root-source-tests-").FullName);
        internal string Parent => System.IO.Path.Combine(root, "private");
        internal string Path => System.IO.Path.Combine(Parent, "root.key");
        internal void Write(string text)
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Parent);
            else Directory.CreateDirectory(Parent, DirectoryMode);
            File.WriteAllText(Path, text);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, FileMode);
        }
        public void Dispose() => Directory.Delete(root, true);
        private static string Canonical(string path) => OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + path : path;
    }
}
