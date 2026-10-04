using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Configuration;

/// <summary>Lazily resolves one canonical 32-byte Base64 root key from a private local file.</summary>
/// <remarks>The caller owns a trusted dedicated directory and Windows ACLs. Existing valid files
/// are reused unchanged; concurrent creators publish one complete winner using an atomic hard link.
/// External replacement, malicious hard links, network filesystems, directory fsync, crash cleanup,
/// rotation and in-memory string erasure are not guaranteed. Never log the returned secret.</remarks>
public sealed class RootKeyFileSource
{
    private readonly string path;
    private readonly bool metadataAvailable;
    private readonly Func<string, string, HardLinkResult>? publish;
    private readonly Action<Checkpoint, string>? checkpoint;

    /// <summary>Captures a fully qualified ordinary local path without filesystem I/O.</summary>
    public RootKeyFileSource(string filePath) : this(filePath, null, null) { }

    internal RootKeyFileSource(string filePath, Func<string, string, HardLinkResult>? publish,
        Action<Checkpoint, string>? checkpoint, bool metadataAvailable = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath) ||
                filePath.Contains('\0') || filePath.StartsWith("//", StringComparison.Ordinal) ||
                filePath.StartsWith(@"\\", StringComparison.Ordinal) ||
                filePath[(OperatingSystem.IsWindows() ? 2 : 0)..].Contains(':') ||
                filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "." or "..") ||
                string.IsNullOrEmpty(Path.GetFileName(filePath))) throw Failure();
            path = Path.GetFullPath(filePath);
            this.metadataAvailable = metadataAvailable;
            this.publish = publish;
            this.checkpoint = checkpoint;
        }
        catch (Exception) { throw Failure(); }
    }

    /// <summary>Reads a valid private root key, or atomically publishes and rereads the shared winner.</summary>
    /// <exception cref="InvalidOperationException">The file cannot safely be resolved. Diagnostics contain no path or secret.</exception>
    public string Resolve()
    {
        string? temporary = null;
        try
        {
            EnsureParent();
            var target = Inspect(path);
            if (target.Status == MetadataStatus.Success) return ReadTarget();
            if (target.Status != MetadataStatus.Missing) throw Failure();
            temporary = Path.Combine(Path.GetDirectoryName(path)!, ".root-key-" + Guid.NewGuid().ToString("N") + ".tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.SequentialScan };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                checkpoint?.Invoke(Checkpoint.Created, temporary);
                stream.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
                checkpoint?.Invoke(Checkpoint.Written, temporary);
                checkpoint?.Invoke(Checkpoint.BeforeFlush, temporary);
                stream.Flush(flushToDisk: true);
                checkpoint?.Invoke(Checkpoint.Flushed, temporary);
                checkpoint?.Invoke(Checkpoint.BeforeDispose, temporary);
            }
            checkpoint?.Invoke(Checkpoint.ResourcesReleased, temporary);
            checkpoint?.Invoke(Checkpoint.BeforePublish, temporary);
            var linked = publish?.Invoke(temporary, path) ?? HardLinkPublisher.TryPublish(temporary, path, out _);
            if (linked is not (HardLinkResult.Published or HardLinkResult.TargetAlreadyExists)) throw Failure();
            checkpoint?.Invoke(Checkpoint.Published, temporary);
            return ReadTarget();
        }
        catch (Exception) { throw Failure(); }
        finally
        {
            if (temporary is not null)
            {
                try { checkpoint?.Invoke(Checkpoint.Cleanup, temporary); File.Delete(temporary); }
                catch (Exception) { /* Only this call's temporary name is eligible for best-effort cleanup. */ }
            }
        }
    }

    /// <summary>Returns fixed metadata without the captured path or root key.</summary>
    public override string ToString() => "RootKeyFileSource(Lazy=True)";

    internal enum Checkpoint { Created, Written, BeforeFlush, Flushed, BeforeDispose, ResourcesReleased, BeforePublish, Published, Cleanup }

    private static InvalidOperationException Failure() => new("The root key file could not be safely resolved.");

    private void EnsureParent()
    {
        var parent = Path.GetDirectoryName(path)!;
        var root = Path.GetPathRoot(path)!;
        var rootInfo = Inspect(root);
        if (rootInfo.Status != MetadataStatus.Success || !rootInfo.IsDirectory || rootInfo.IsSymbolicLink) throw Failure();
        var current = root;
        var components = Path.GetRelativePath(root, parent).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = 0; index < components.Length; index++)
        {
            if (components[index] == ".") continue;
            current = Path.Combine(current, components[index]);
            var info = Inspect(current);
            if (info.Status == MetadataStatus.Missing && index == components.Length - 1)
            {
                // Only the direct parent leaf may be created; its ancestors were positively inspected.
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(current);
                else Directory.CreateDirectory(current, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                info = Inspect(current);
            }
            if (info.Status != MetadataStatus.Success || !info.IsDirectory || info.IsSymbolicLink) throw Failure();
        }
        var parentInfo = Inspect(parent);
        if (parentInfo.Status != MetadataStatus.Success || !parentInfo.IsDirectory || parentInfo.IsSymbolicLink) throw Failure();
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(parent);
            var privateBits = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if ((mode & ~privateBits) != 0 || (mode & (UnixFileMode.UserRead | UnixFileMode.UserExecute)) !=
                (UnixFileMode.UserRead | UnixFileMode.UserExecute)) throw Failure();
        }
    }

    private string ReadTarget()
    {
        var info = Inspect(path);
        if (info.Status != MetadataStatus.Success || !info.IsRegularFile || info.IsSymbolicLink) throw Failure();
        // A concurrent publish temporarily leaves its private temp as a second hard-link name.
        // Link count therefore cannot distinguish a valid winner within the declared trust boundary.
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if (mode is not (UnixFileMode.UserRead or (UnixFileMode.UserRead | UnixFileMode.UserWrite))) throw Failure();
        }
        Span<byte> bytes = stackalloc byte[47];
        int count;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 47, FileOptions.SequentialScan))
            count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        var payload = bytes[..count];
        if (count == 45 && payload[^1] == 10) payload = payload[..^1];
        else if (count == 46 && payload[^2] == 13 && payload[^1] == 10) payload = payload[..^2];
        if (payload.Length != 44 || payload.ContainsAnyExceptInRange((byte)0, (byte)127)) throw Failure();
        var key = Encoding.ASCII.GetString(payload);
        Span<byte> decoded = stackalloc byte[32];
        if (!Convert.TryFromBase64String(key, decoded, out var written) || written != 32 ||
            Convert.ToBase64String(decoded) != key) throw Failure();
        return key;
    }

    private Metadata Inspect(string path) => metadataAvailable ? Metadata.Inspect(path) :
        new(MetadataStatus.Unknown, false, false, false, 0);

    private enum MetadataStatus { Success, Missing, Unknown }

    private readonly record struct Metadata(
        MetadataStatus Status,
        bool IsDirectory,
        bool IsRegularFile,
        bool IsSymbolicLink,
        ulong HardLinkCount)
    {
        private const uint FileTypeMask = 0xF000;
        private const uint DirectoryType = 0x4000;
        private const uint RegularFileType = 0x8000;
        private const uint SymbolicLinkType = 0xA000;
        private const int ErrorAccessDenied = 5;
        private const int ErrorSharingViolation = 32;
        private const int ErrorLockViolation = 33;
        private const uint FileTypeDisk = 1;
    
        internal static Metadata Inspect(string path)
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                    {
                        return LinuxArm64LStat(path, out var arm64Stat) == 0
                            ? FromUnix(arm64Stat.Mode, arm64Stat.HardLinkCount)
                            : FromNativeError(Marshal.GetLastPInvokeError());
                    }
    
                    if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                    {
                        return LinuxX64LStat(path, out var x64Stat) == 0
                            ? FromUnix(x64Stat.Mode, x64Stat.HardLinkCount)
                            : FromNativeError(Marshal.GetLastPInvokeError());
                    }
    
                    return new(
                        MetadataStatus.Unknown,
                        false,
                        false,
                        false,
                        0);
                }
    
                if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64)
                {
                    return MacLStat(path, out var stat) == 0
                        ? FromUnix(stat.Mode, stat.HardLinkCount)
                        : FromNativeError(Marshal.GetLastPInvokeError());
                }
    
                if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
                {
                    using var handle = CreateFile(
                        path,
                        0,
                        FileShare.ReadWrite | FileShare.Delete,
                        IntPtr.Zero,
                        FileMode.Open,
                        0x02200000,
                        IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        return FromWindowsError(Marshal.GetLastPInvokeError());
                    }
    
                    if (!GetFileInformationByHandle(handle, out var information) ||
                        GetFileType(handle) != FileTypeDisk)
                    {
                        return FromWindowsError(Marshal.GetLastPInvokeError());
                    }
    
                    var attributes = (FileAttributes)information.FileAttributes;
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var isLink = attributes.HasFlag(FileAttributes.ReparsePoint);
                    return new(
                        MetadataStatus.Success,
                        isDirectory,
                        !isDirectory && !isLink && !attributes.HasFlag(FileAttributes.Device),
                        isLink,
                        information.NumberOfLinks);
                }
    
                return new(MetadataStatus.Unknown, false, false, false, 0);
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                return new(MetadataStatus.Unknown, false, false, false, 0);
            }
        }
    
        private static Metadata FromUnix(uint mode, ulong hardLinkCount)
        {
            var type = mode & FileTypeMask;
            return new(
                MetadataStatus.Success,
                type == DirectoryType,
                type == RegularFileType,
                type == SymbolicLinkType,
                hardLinkCount);
        }
    
        private static Metadata FromNativeError(int error) =>
            new(error == 2 ? MetadataStatus.Missing : MetadataStatus.Unknown, false, false, false, 0);
    
        private static Metadata FromWindowsError(int error) =>
            new(error == 2 ? MetadataStatus.Missing : MetadataStatus.Unknown, false, false, false, 0);
    
        [DllImport("libc", EntryPoint = "lstat", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int LinuxX64LStat(string path, out LinuxX64Stat stat);
    
        [DllImport("libc", EntryPoint = "lstat", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int LinuxArm64LStat(string path, out LinuxArm64Stat stat);
    
        [DllImport("libc", EntryPoint = "lstat", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int MacLStat(string path, out MacStat stat);
    
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);
    
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(SafeFileHandle file);
    
        [StructLayout(LayoutKind.Sequential)]
        private struct LinuxX64Stat
        {
            internal ulong Device;
            internal ulong Inode;
            internal ulong HardLinkCount;
            internal uint Mode;
            internal uint UserId;
            internal uint GroupId;
            internal int Padding;
            internal ulong SpecialDevice;
            internal long Size;
            internal long BlockSize;
            internal long Blocks;
            internal long AccessTime;
            internal long AccessTimeNanoseconds;
            internal long ModificationTime;
            internal long ModificationTimeNanoseconds;
            internal long ChangeTime;
            internal long ChangeTimeNanoseconds;
            internal long Reserved0;
            internal long Reserved1;
            internal long Reserved2;
        }
    
        [StructLayout(LayoutKind.Sequential)]
        private struct LinuxArm64Stat
        {
            internal ulong Device;
            internal ulong Inode;
            internal uint Mode;
            internal uint HardLinkCount;
            internal uint UserId;
            internal uint GroupId;
            internal ulong SpecialDevice;
            internal long Size;
            internal long BlockSize;
            internal long Blocks;
            internal long AccessTime;
            internal long AccessTimeNanoseconds;
            internal long ModificationTime;
            internal long ModificationTimeNanoseconds;
            internal long ChangeTime;
            internal long ChangeTimeNanoseconds;
            internal long Reserved0;
            internal long Reserved1;
        }
    
        [StructLayout(LayoutKind.Sequential)]
        private struct MacStat
        {
            internal int Device;
            internal ushort Mode;
            internal ushort HardLinkCount;
            internal ulong Inode;
            internal uint UserId;
            internal uint GroupId;
            internal int SpecialDevice;
            internal int Padding;
            internal long AccessTime;
            internal long AccessTimeNanoseconds;
            internal long ModificationTime;
            internal long ModificationTimeNanoseconds;
            internal long ChangeTime;
            internal long ChangeTimeNanoseconds;
            internal long BirthTime;
            internal long BirthTimeNanoseconds;
            internal long Size;
            internal long Blocks;
            internal int BlockSize;
            internal uint Flags;
            internal uint Generation;
            internal int Spare;
            internal long Spare0;
            internal long Spare1;
        }
    
        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }
    }
}
