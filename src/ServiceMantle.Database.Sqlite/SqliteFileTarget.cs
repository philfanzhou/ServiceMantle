using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace ServiceMantle.Database.Sqlite;

internal static class SqliteFileTarget
{
    internal static bool TryParse(string connectionString, out string path)
    {
        path = string.Empty;
        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }

        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            dataSource.IndexOf('\0') >= 0 ||
            dataSource.Contains("|DataDirectory|", StringComparison.OrdinalIgnoreCase) ||
            dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(builder.Password) ||
            !string.IsNullOrEmpty(builder.Vfs) ||
            builder.Cache == SqliteCacheMode.Shared ||
            builder.Mode is SqliteOpenMode.ReadOnly or SqliteOpenMode.Memory ||
            !Path.IsPathFullyQualified(dataSource) ||
            !HasSupportedPathShape(dataSource))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(dataSource);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            path = string.Empty;
            return false;
        }
    }

    private static bool HasSupportedPathShape(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || path.Length <= root.Length)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal) ||
                root.Length != 3 ||
                root[1] != ':' ||
                !char.IsAsciiLetter(root[0]) ||
                path.IndexOf(':', 2) >= 0)
            {
                return false;
            }
        }

        var previousWasSeparator = IsDirectorySeparator(root[^1]);
        for (var index = root.Length; index < path.Length; index++)
        {
            var isSeparator = IsDirectorySeparator(path[index]);
            if (isSeparator && (previousWasSeparator || index == path.Length - 1))
            {
                return false;
            }

            previousWasSeparator = isSeparator;
        }

        var segments = path[root.Length..].Split(
            OperatingSystem.IsWindows() ? ['\\', '/'] : ['/'],
            StringSplitOptions.None);
        return segments.Length > 0 && segments.All(segment =>
            segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
}

internal enum SqlitePathInspectionStatus
{
    ExistingFile,
    MissingFile,
    ParentMissing,
    PermissionDenied,
    InvalidTarget,
    CapabilityNotSupported
}

internal sealed class SqlitePathInspection
{
    internal SqlitePathInspection(SqlitePathInspectionStatus status, string? canonicalPath = null)
    {
        Status = status;
        CanonicalPath = canonicalPath;
    }

    internal SqlitePathInspectionStatus Status { get; }
    internal string? CanonicalPath { get; }

    public override string ToString() => $"SqlitePathInspection(Status={Status})";
}

internal enum SqliteSidecarInspectionStatus
{
    None,
    Present,
    PermissionDenied,
    CapabilityNotSupported
}

internal enum SqlitePublishStatus
{
    Published,
    TargetExists,
    PermissionDenied,
    CapabilityNotSupported,
    Failed
}

internal interface ISqliteTargetFileSystem
{
    SqlitePathInspection Inspect(string path);
    SqliteSidecarInspectionStatus InspectSidecars(string canonicalPath);
    string CreateTemporaryFile(string canonicalTargetPath);
    SqlitePublishStatus Publish(string temporaryPath, string canonicalTargetPath);
    void DeleteTemporaryFile(string temporaryPath);
}

internal sealed class SqliteTargetFileSystem : ISqliteTargetFileSystem
{
    public SqlitePathInspection Inspect(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return new(SqlitePathInspectionStatus.ParentMissing);
            }

            if (OperatingSystem.IsWindows())
            {
                root = char.ToUpperInvariant(root[0]) + root[1..];
            }

            var relative = path[Path.GetPathRoot(path)!.Length..];
            var segments = relative.Split(
                OperatingSystem.IsWindows() ? ['\\', '/'] : ['/'],
                StringSplitOptions.None);
            var current = root;

            for (var index = 0; index < segments.Length; index++)
            {
                var isLeaf = index == segments.Length - 1;
                FileSystemInfo? entry;
                FileSystemInfo[] entries;
                try
                {
                    entries = new DirectoryInfo(current)
                        .EnumerateFileSystemInfos()
                        .ToArray();
                    entry = entries.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, segments[index], StringComparison.Ordinal));
                }
                catch (UnauthorizedAccessException)
                {
                    return new(SqlitePathInspectionStatus.PermissionDenied);
                }
                catch (DirectoryNotFoundException)
                {
                    return new(SqlitePathInspectionStatus.ParentMissing);
                }
                catch (IOException)
                {
                    return Directory.Exists(current)
                        ? new(SqlitePathInspectionStatus.CapabilityNotSupported)
                        : new(SqlitePathInspectionStatus.ParentMissing);
                }

                if (entry is null)
                {
                    var submittedPath = Path.Combine(current, segments[index]);
                    if (File.Exists(submittedPath) || Directory.Exists(submittedPath))
                    {
                        var platformMatches = entries.Where(candidate => string.Equals(
                            candidate.Name,
                            segments[index],
                            StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (platformMatches.Length != 1)
                        {
                            return new(SqlitePathInspectionStatus.InvalidTarget);
                        }

                        entry = platformMatches[0];
                    }
                    else
                    {
                        return isLeaf
                            ? new(SqlitePathInspectionStatus.MissingFile, Path.Combine(current, segments[index]))
                            : new(SqlitePathInspectionStatus.ParentMissing);
                    }
                }

                var metadata = SqliteNativeFileMetadata.Inspect(entry.FullName);
                if (metadata.Status != SqliteNativeFileMetadataStatus.Success)
                {
                    return new(metadata.Status switch
                    {
                        SqliteNativeFileMetadataStatus.PermissionDenied => SqlitePathInspectionStatus.PermissionDenied,
                        SqliteNativeFileMetadataStatus.InvalidTarget => SqlitePathInspectionStatus.InvalidTarget,
                        _ => SqlitePathInspectionStatus.CapabilityNotSupported
                    });
                }

                if (metadata.IsSymbolicLink)
                {
                    return new(SqlitePathInspectionStatus.InvalidTarget);
                }

                current = Path.Combine(current, entry.Name);
                if (!isLeaf && !metadata.IsDirectory)
                {
                    return new(SqlitePathInspectionStatus.InvalidTarget);
                }

                if (isLeaf)
                {
                    if (!metadata.IsRegularFile || metadata.HardLinkCount != 1)
                    {
                        return new(SqlitePathInspectionStatus.InvalidTarget);
                    }

                    return new(SqlitePathInspectionStatus.ExistingFile, current);
                }
            }

            return new(SqlitePathInspectionStatus.InvalidTarget);
        }
        catch (UnauthorizedAccessException)
        {
            return new(SqlitePathInspectionStatus.PermissionDenied);
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentException or NotSupportedException)
        {
            return new(SqlitePathInspectionStatus.CapabilityNotSupported);
        }
    }

    public SqliteSidecarInspectionStatus InspectSidecars(string canonicalPath)
    {
        try
        {
            var parent = Path.GetDirectoryName(canonicalPath);
            var leaf = Path.GetFileName(canonicalPath);
            if (parent is null)
            {
                return SqliteSidecarInspectionStatus.CapabilityNotSupported;
            }

            var sidecars = new HashSet<string>(StringComparer.Ordinal)
            {
                leaf + "-journal",
                leaf + "-wal",
                leaf + "-shm"
            };
            if (new DirectoryInfo(parent).EnumerateFileSystemInfos()
                .Any(entry => sidecars.Contains(entry.Name)))
            {
                return SqliteSidecarInspectionStatus.Present;
            }

            // Resolve each name through the filesystem as well: ordinal directory entries can
            // miss case aliases (including in a missing target's leaf or the sidecar suffix).
            // Attribute lookup observes directories and dangling links without opening SQLite;
            // unlike Exists, it does not hide access failures as an absent sidecar.
            foreach (var sidecar in sidecars)
            {
                try
                {
                    _ = File.GetAttributes(Path.Combine(parent, sidecar));
                    return SqliteSidecarInspectionStatus.Present;
                }
                catch (FileNotFoundException)
                {
                    // Only an absent entry permits checking the next sidecar name.
                }
            }

            return SqliteSidecarInspectionStatus.None;
        }
        catch (UnauthorizedAccessException)
        {
            return SqliteSidecarInspectionStatus.PermissionDenied;
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentException or NotSupportedException)
        {
            return SqliteSidecarInspectionStatus.CapabilityNotSupported;
        }
    }

    public string CreateTemporaryFile(string canonicalTargetPath)
    {
        var parent = Path.GetDirectoryName(canonicalTargetPath) ??
            throw new IOException("The SQLite target parent directory is unavailable.");
        var leaf = Path.GetFileName(canonicalTargetPath);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var path = Path.Combine(parent, $".{leaf}.servicemantle-{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // A GUID collision is extraordinarily unlikely, but never adopt or delete it.
            }
        }

        throw new IOException("A unique SQLite preparation file could not be created.");
    }

    public SqlitePublishStatus Publish(string temporaryPath, string canonicalTargetPath)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (RenameNoReplaceLinux(
                    -100,
                    temporaryPath,
                    -100,
                    canonicalTargetPath,
                    1) == 0)
                {
                    return SqlitePublishStatus.Published;
                }

                return Marshal.GetLastPInvokeError() switch
                {
                    17 => SqlitePublishStatus.TargetExists,
                    1 or 13 => SqlitePublishStatus.PermissionDenied,
                    18 or 22 or 38 or 95 => SqlitePublishStatus.CapabilityNotSupported,
                    _ => SqlitePublishStatus.Failed
                };
            }
            catch (UnauthorizedAccessException)
            {
                return SqlitePublishStatus.PermissionDenied;
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                return SqlitePublishStatus.CapabilityNotSupported;
            }
            catch (IOException)
            {
                return SqlitePublishStatus.Failed;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                if (RenameExclusiveMac(temporaryPath, canonicalTargetPath, 4) == 0)
                {
                    return SqlitePublishStatus.Published;
                }

                return Marshal.GetLastPInvokeError() switch
                {
                    17 => SqlitePublishStatus.TargetExists,
                    1 or 13 => SqlitePublishStatus.PermissionDenied,
                    18 or 22 or 45 => SqlitePublishStatus.CapabilityNotSupported,
                    _ => SqlitePublishStatus.Failed
                };
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                return SqlitePublishStatus.CapabilityNotSupported;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (MoveFileWithoutReplacement(temporaryPath, canonicalTargetPath, 0))
                {
                    return SqlitePublishStatus.Published;
                }

                return Marshal.GetLastPInvokeError() switch
                {
                    80 or 183 => SqlitePublishStatus.TargetExists,
                    5 or 32 or 33 => SqlitePublishStatus.PermissionDenied,
                    50 => SqlitePublishStatus.CapabilityNotSupported,
                    _ => SqlitePublishStatus.Failed
                };
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                return SqlitePublishStatus.CapabilityNotSupported;
            }
        }

        return SqlitePublishStatus.CapabilityNotSupported;
    }

    public void DeleteTemporaryFile(string temporaryPath)
    {
        foreach (var path in new[]
        {
            temporaryPath,
            temporaryPath + "-journal",
            temporaryPath + "-wal",
            temporaryPath + "-shm"
        })
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Cleanup is best effort and restricted to this call's unique temporary names.
            }
        }
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int RenameNoReplaceLinux(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int RenameExclusiveMac(string oldPath, string newPath, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileWithoutReplacement(
        string existingPath,
        string newPath,
        uint flags);
}

internal enum SqliteNativeFileMetadataStatus
{
    Success,
    PermissionDenied,
    InvalidTarget,
    CapabilityNotSupported
}

internal readonly record struct SqliteNativeFileMetadata(
    SqliteNativeFileMetadataStatus Status,
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

    internal static SqliteNativeFileMetadata Inspect(string path)
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
                    SqliteNativeFileMetadataStatus.CapabilityNotSupported,
                    false,
                    false,
                    false,
                    0);
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacLStat(path, out var stat) == 0
                    ? FromUnix(stat.Mode, stat.HardLinkCount)
                    : FromNativeError(Marshal.GetLastPInvokeError());
            }

            if (OperatingSystem.IsWindows())
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
                    SqliteNativeFileMetadataStatus.Success,
                    isDirectory,
                    !isDirectory && !isLink && !attributes.HasFlag(FileAttributes.Device),
                    isLink,
                    information.NumberOfLinks);
            }

            return new(SqliteNativeFileMetadataStatus.CapabilityNotSupported, false, false, false, 0);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return new(SqliteNativeFileMetadataStatus.CapabilityNotSupported, false, false, false, 0);
        }
    }

    private static SqliteNativeFileMetadata FromUnix(uint mode, ulong hardLinkCount)
    {
        var type = mode & FileTypeMask;
        return new(
            SqliteNativeFileMetadataStatus.Success,
            type == DirectoryType,
            type == RegularFileType,
            type == SymbolicLinkType,
            hardLinkCount);
    }

    private static SqliteNativeFileMetadata FromNativeError(int error) =>
        error is 1 or 13
            ? new(SqliteNativeFileMetadataStatus.PermissionDenied, false, false, false, 0)
            : error is 2 or 20 or 40
                ? new(SqliteNativeFileMetadataStatus.InvalidTarget, false, false, false, 0)
                : new(SqliteNativeFileMetadataStatus.CapabilityNotSupported, false, false, false, 0);

    private static SqliteNativeFileMetadata FromWindowsError(int error) =>
        error is ErrorAccessDenied or ErrorSharingViolation or ErrorLockViolation
            ? new(SqliteNativeFileMetadataStatus.PermissionDenied, false, false, false, 0)
            : new(SqliteNativeFileMetadataStatus.CapabilityNotSupported, false, false, false, 0);

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
