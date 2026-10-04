using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ServiceMantle.Bootstrap;
using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class RootKeyFileSourceTests
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirectoryModeBits = FileModeBits | UnixFileMode.UserExecute;

    [Fact]
    public void Construction_is_lazy_and_first_repeat_and_two_instances_return_one_private_key()
    {
        using var fixture = new Fixture();
        var source = new RootKeyFileSource(fixture.Path);
        Assert.False(Directory.Exists(fixture.Parent));
        Assert.Empty(typeof(RootKeyFileSource).GetProperties());
        Assert.DoesNotContain(fixture.Path, source.ToString());
        var key = source.Resolve();
        Assert.Equal(32, Convert.FromBase64String(key).Length);
        Assert.Equal(key, source.Resolve()); Assert.Equal(key, new RootKeyFileSource(fixture.Path).Resolve());
        Assert.Equal(key, File.ReadAllText(fixture.Path)); Assert.Single(Directory.GetFiles(fixture.Parent));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(DirectoryModeBits, File.GetUnixFileMode(fixture.Parent));
            Assert.Equal(FileModeBits, File.GetUnixFileMode(fixture.Path));
        }
        using var exclusive = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Existing_canonical_private_key_is_not_rewritten_and_accepts_only_one_line_ending(string ending)
    {
        using var fixture = new Fixture(); fixture.Write(Key + ending);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixture.Path, UnixFileMode.UserRead);
        var before = File.ReadAllBytes(fixture.Path); var stamp = File.GetLastWriteTimeUtc(fixture.Path);
        Assert.Equal(Key, new RootKeyFileSource(fixture.Path).Resolve());
        Assert.Equal(before, File.ReadAllBytes(fixture.Path)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Parent));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(fixture.Path));
    }

    public static TheoryData<string> BadFormats => new()
    {
        "", "short-secret", Key[..43], Key + "\n\n", Key + " ", " " + Key,
        "\uFEFF" + Key, new string('x', 100000), new string('!', 44), new string('A', 43) + "B",
        new string('A', 42) + "B=", Convert.ToBase64String(new byte[31]), Convert.ToBase64String(new byte[33])
    };
    [Theory]
    [MemberData(nameof(BadFormats))]
    public void Invalid_or_overlong_target_fails_without_replacement_or_diagnostic_material(string text)
    {
        using var fixture = new Fixture(); fixture.Write(text); var bytes = File.ReadAllBytes(fixture.Path);
        SafeFailure(() => new RootKeyFileSource(fixture.Path).Resolve(), fixture.Path);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path)); Assert.Single(Directory.GetFiles(fixture.Parent));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("relative.key")] [InlineData("https://private-secret.example/key")]
    [InlineData("\\\\private-secret\\share\\key")] [InlineData("//private-secret/share/key")]
    public void Invalid_path_is_rejected_without_I_O_or_input_echo(string? path)
        => SafeFailure(() => new RootKeyFileSource(path!), "private-secret");

    [Fact]
    public void Nul_ads_device_dot_and_missing_ancestor_paths_are_refused()
    {
        using var fixture = new Fixture();
        foreach (var path in new[] { fixture.Path + "\0", fixture.Path + ":secret", Path.Combine(fixture.Parent, "..", "private-secret"),
            "\\\\?\\C:\\private-secret", Path.Combine(fixture.Parent, "nested", "key") })
            SafeFailure(() => new RootKeyFileSource(path).Resolve(), fixture.Path);
        Assert.False(Directory.Exists(fixture.Parent));
    }

    [Theory]
    [InlineData("file")] [InlineData("parent")] [InlineData("ancestor")] [InlineData("dangling")]
    public void Symlinks_and_Windows_reparse_components_are_refused_before_read_or_create(string kind)
    {
        using var fixture = new Fixture(); fixture.EnsureParent();
        var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        var destination = Path.Combine(outside, "key"); File.WriteAllText(destination, Key);
        if (kind is "file" or "dangling") File.CreateSymbolicLink(fixture.Path, kind == "file" ? destination : destination + ".absent");
        else
        {
            Directory.Delete(fixture.Parent);
            if (kind == "ancestor")
            {
                var linked = Path.Combine(fixture.Root, "linked"); Directory.CreateSymbolicLink(linked, outside);
                SafeFailure(() => new RootKeyFileSource(Path.Combine(linked, "child", "key")).Resolve(), fixture.Path);
                Assert.False(Directory.Exists(Path.Combine(outside, "child"))); return;
            }
            Directory.CreateSymbolicLink(fixture.Parent, outside);
        }
        SafeFailure(() => new RootKeyFileSource(fixture.Path).Resolve(), fixture.Path);
        Assert.Equal(Key, File.ReadAllText(destination)); Assert.Single(Directory.GetFiles(outside));
    }

    [Theory]
    [InlineData("directory")] [InlineData("fifo")] [InlineData("socket")]
    public void Non_regular_objects_are_refused_with_no_blocking_read(string kind)
    {
        if (OperatingSystem.IsWindows() && kind != "directory") return;
        using var fixture = new Fixture(); fixture.EnsureParent();
        using var socket = kind == "socket" ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) : null;
        if (kind == "directory") Directory.CreateDirectory(fixture.Path);
        else if (kind == "fifo") Assert.Equal(0, MkFifo(fixture.Path, 384));
        else socket!.Bind(new UnixDomainSocketEndPoint(fixture.Path));
        SafeFailure(() => new RootKeyFileSource(fixture.Path).Resolve(), fixture.Path);
        Assert.Empty(Directory.GetFiles(fixture.Parent, ".root-key-*.tmp"));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Broad_existing_permissions_are_not_repaired(bool file)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(); fixture.Write(Key);
        var path = file ? fixture.Path : fixture.Parent;
        var mode = File.GetUnixFileMode(path) | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, mode);
        SafeFailure(() => new RootKeyFileSource(fixture.Path).Resolve(), fixture.Path);
        Assert.Equal(mode, File.GetUnixFileMode(path)); Assert.Equal(Key, File.ReadAllText(fixture.Path));
    }

    [Theory]
    [InlineData(0)] [InlineData(128)] [InlineData(448)] [InlineData(388)] [InlineData(416)] [InlineData(896)]
    public void Unsupported_unix_modes_fail_closed_without_chmod(int rawMode)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(); fixture.Write(Key);
        File.SetUnixFileMode(fixture.Path, (UnixFileMode)rawMode);
        SafeFailure(() => new RootKeyFileSource(fixture.Path).Resolve(), fixture.Path);
        Assert.Equal((UnixFileMode)rawMode, File.GetUnixFileMode(fixture.Path));
    }

    [Fact]
    public void Existing_non_directory_parent_and_native_publication_failure_are_fixed()
    {
        using var fixture = new Fixture(); File.WriteAllText(fixture.Parent, "private-secret");
        SafeFailure(() => new RootKeyFileSource(fixture.Path).Resolve(), fixture.Path);
        Assert.Equal("private-secret", File.ReadAllText(fixture.Parent)); File.Delete(fixture.Parent);
        SafeFailure(() => new RootKeyFileSource(fixture.Path, (_, _) => throw new IOException("private-secret OS error"), null).Resolve(), fixture.Path);
        Assert.False(File.Exists(fixture.Path)); Assert.Empty(Directory.GetFiles(fixture.Parent));
    }

    [Fact]
    public void Unknown_metadata_capability_fails_closed_and_creates_nothing()
    {
        using var fixture = new Fixture();
        SafeFailure(() => new RootKeyFileSource(fixture.Path, null, null, metadataAvailable: false).Resolve(), fixture.Path);
        Assert.False(Directory.Exists(fixture.Parent));
    }

    [Fact]
    public void Temporary_permissions_are_private_at_creation_before_any_secret_write()
    {
        using var fixture = new Fixture(); var inspected = false;
        var source = new RootKeyFileSource(fixture.Path, null, (point, temporary) =>
        {
            if (point != RootKeyFileSource.Checkpoint.Created) return;
            inspected = true; Assert.Equal(0, new FileInfo(temporary).Length);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(FileModeBits, File.GetUnixFileMode(temporary));
                Assert.Equal(DirectoryModeBits, File.GetUnixFileMode(fixture.Parent));
            }
        });
        Assert.Equal(32, Convert.FromBase64String(source.Resolve()).Length); Assert.True(inspected);
    }

    [Theory]
    [InlineData((int)RootKeyFileSource.Checkpoint.Created)] [InlineData((int)RootKeyFileSource.Checkpoint.Written)]
    [InlineData((int)RootKeyFileSource.Checkpoint.BeforeFlush)] [InlineData((int)RootKeyFileSource.Checkpoint.Flushed)]
    [InlineData((int)RootKeyFileSource.Checkpoint.BeforeDispose)] [InlineData((int)RootKeyFileSource.Checkpoint.ResourcesReleased)]
    [InlineData((int)RootKeyFileSource.Checkpoint.BeforePublish)]
    public void Prepublish_write_flush_release_failures_never_publish_and_release_owned_handle(int fault)
    {
        using var fixture = new Fixture();
        SafeFailure(() => new RootKeyFileSource(fixture.Path, null, (point, temporary) =>
        {
            if (point == RootKeyFileSource.Checkpoint.ResourcesReleased)
            { using var exclusive = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            if ((int)point == fault) throw new IOException("private-secret OS " + fixture.Path);
        }).Resolve(), fixture.Path);
        Assert.False(File.Exists(fixture.Path)); Assert.Empty(Directory.GetFiles(fixture.Parent));
    }

    [Theory]
    [InlineData((int)HardLinkResult.Refused)] [InlineData((int)HardLinkResult.TargetAlreadyExists)]
    public void Refused_or_false_competition_cannot_return_own_candidate_or_fallback(int result)
    {
        using var fixture = new Fixture();
        SafeFailure(() => new RootKeyFileSource(fixture.Path, (_, _) => (HardLinkResult)result, null).Resolve(), fixture.Path);
        Assert.False(File.Exists(fixture.Path)); Assert.Empty(Directory.GetFiles(fixture.Parent));
    }

    [Fact]
    public void Competing_winner_is_read_without_overwrite_and_temporary_link_count_is_accepted()
    {
        using var fixture = new Fixture();
        var source = new RootKeyFileSource(fixture.Path, null, (point, _) =>
        { if (point == RootKeyFileSource.Checkpoint.BeforePublish) fixture.Write(Key); });
        Assert.Equal(Key, source.Resolve()); Assert.Equal(Key, File.ReadAllText(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Parent));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Postpublish_failure_or_cleanup_fault_never_removes_committed_target(bool cleanupOnly)
    {
        using var fixture = new Fixture();
        var source = new RootKeyFileSource(fixture.Path, null, (point, _) =>
        { if (point == (cleanupOnly ? RootKeyFileSource.Checkpoint.Cleanup : RootKeyFileSource.Checkpoint.Published))
            throw new IOException("private-secret OS " + fixture.Path); });
        if (cleanupOnly) Assert.Equal(source.Resolve(), new RootKeyFileSource(fixture.Path).Resolve());
        else SafeFailure(() => source.Resolve(), fixture.Path);
        Assert.Equal(32, Convert.FromBase64String(new RootKeyFileSource(fixture.Path).Resolve()).Length);
        Assert.Equal(cleanupOnly ? 2 : 1, Directory.GetFiles(fixture.Parent).Length);
    }

    [Fact]
    public void Existing_exclusive_handle_failure_is_fixed_and_does_not_replace_target()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(); fixture.Write(Key);
        using (var held = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            SafeFailure(() => new RootKeyFileSource(fixture.Path).Resolve(), fixture.Path);
        Assert.Equal(Key, new RootKeyFileSource(fixture.Path).Resolve()); Assert.Single(Directory.GetFiles(fixture.Parent));
    }

    [Fact]
    public async Task Eight_independent_processes_publish_one_complete_winner()
    {
        using var fixture = new Fixture(); fixture.EnsureParent();
        var processes = new List<Process>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("-explicit"); start.ArgumentList.Add("on"); start.ArgumentList.Add("-method");
                start.ArgumentList.Add("ServiceMantle.Tests.Configuration.RootKeyFileSourceTests.Creator_process_fixture");
                start.Environment["SERVICEMANTLE_ROOTKEY_FIXTURE"] = fixture.Path;
                start.Environment["SERVICEMANTLE_ROOTKEY_INDEX"] = index.ToString();
                processes.Add(Process.Start(start)!);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (Enumerable.Range(0, 8).Any(index => !File.Exists(Path.Combine(fixture.Root, "ready-" + index))))
            {
                Assert.DoesNotContain(processes, process => process.HasExited);
                await Task.Delay(10, timeout.Token);
            }
            File.WriteAllText(Path.Combine(fixture.Root, "go"), "go");
            foreach (var process in processes)
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode); Assert.DoesNotContain(Key, await output); Assert.DoesNotContain(fixture.Path, await error);
            }
            var hashes = Enumerable.Range(0, 8).Select(index => File.ReadAllText(Path.Combine(fixture.Root, "result-" + index))).ToArray();
            Assert.Single(hashes.Distinct());
            Assert.Equal(hashes[0], Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(new RootKeyFileSource(fixture.Path).Resolve()))));
            Assert.Single(Directory.GetFiles(fixture.Parent));
        }
        finally { foreach (var process in processes) { if (!process.HasExited) process.Kill(true); process.Dispose(); } }
    }

    [Fact(Explicit = true)]
    public void Creator_process_fixture()
    {
        var path = Environment.GetEnvironmentVariable("SERVICEMANTLE_ROOTKEY_FIXTURE")!;
        var index = Environment.GetEnvironmentVariable("SERVICEMANTLE_ROOTKEY_INDEX")!;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        try
        {
            var source = new RootKeyFileSource(path, null, (point, _) =>
            {
                if (point != RootKeyFileSource.Checkpoint.BeforePublish) return;
                File.WriteAllText(Path.Combine(root, "ready-" + index), "ready");
                var timer = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, "go")))
                { if (timer.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException(); Thread.Sleep(5); }
            });
            var key = source.Resolve();
            File.WriteAllText(Path.Combine(root, "result-" + index), Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key))));
            Environment.Exit(0);
        }
        catch { Environment.Exit(12); }
    }

    private static void SafeFailure(Action action, string path)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Null(error.InnerException); Assert.Equal("The root key file could not be safely resolved.", error.Message);
        Assert.DoesNotContain(path, error.ToString()); Assert.DoesNotContain(Key, error.ToString());
        Assert.DoesNotContain("private-secret", error.ToString());
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)] private static extern int MkFifo(string path, uint mode);
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Canonical(Directory.CreateTempSubdirectory("root-key-tests-").FullName);
        internal string Parent => System.IO.Path.Combine(Root, "private");
        internal string Path => System.IO.Path.Combine(Parent, "root.key");
        internal void EnsureParent()
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Parent);
            else Directory.CreateDirectory(Parent, DirectoryModeBits);
        }
        internal void Write(string text)
        {
            EnsureParent(); File.WriteAllText(Path, text);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, FileModeBits);
        }
        public void Dispose() => Directory.Delete(Root, true);
        private static string Canonical(string path) => OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
    }
}
