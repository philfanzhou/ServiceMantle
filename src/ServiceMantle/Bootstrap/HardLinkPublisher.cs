using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ServiceMantle.Bootstrap;

/// <summary>
/// The outcome of publishing a completed file at a path that must still be free.
/// </summary>
internal enum HardLinkResult
{
    /// <summary>The target now names the completed file.</summary>
    Published,

    /// <summary>The operating system refused because the target path is already taken.</summary>
    TargetAlreadyExists,

    /// <summary>The operating system refused for a cause the publisher did not establish.</summary>
    Refused
}

/// <summary>
/// Publishes an already complete file at a path that must not already exist, in one operating
/// system step.
/// </summary>
/// <remarks>
/// <para>
/// A hard link is the only primitive that both picks a single winner and makes the file appear
/// complete. Each alternative gives up one half of that: a rename that overwrites cannot refuse a
/// target that is already there; a rename that refuses one is a check followed by a rename on Unix,
/// so two writers pass the check and the second silently replaces the first; and claiming the path
/// first with an exclusive create publishes an empty file that a process death would strand at the
/// target.
/// </para>
/// <para>
/// Neither .NET nor the base class library exposes a hard link, so the call is made directly -
/// <c>link</c> on Unix, <c>CreateHardLinkW</c> on Windows. Both refuse rather than replace when the
/// new path is taken, and both make the new name refer to the existing content immediately, so a
/// reader can only ever observe the target absent or complete, never partly written.
/// </para>
/// <para>
/// Both names refer to one file afterwards. The caller removes the source name, which leaves the
/// content published under the target alone.
/// </para>
/// </remarks>
internal static class HardLinkPublisher
{
    // EEXIST is 17 on both Linux and macOS.
    private const int UnixTargetExists = 17;
    private const int WindowsFileExists = 80;
    private const int WindowsAlreadyExists = 183;

    private static readonly LinkFunction? UnixLink =
        OperatingSystem.IsWindows() ? null : LoadUnixLink();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
    private delegate int LinkFunction(string existingPath, string newPath);

    /// <summary>
    /// Publishes <paramref name="sourcePath"/> at <paramref name="targetPath"/> without ever
    /// replacing an existing target.
    /// </summary>
    /// <param name="sourcePath">The completed file to publish.</param>
    /// <param name="targetPath">The path that must still be free.</param>
    /// <param name="errorCode">The operating system error, or zero when the file was published.</param>
    internal static HardLinkResult TryPublish(string sourcePath, string targetPath, out int errorCode)
    {
        if (OperatingSystem.IsWindows())
        {
            return PublishOnWindows(sourcePath, targetPath, out errorCode);
        }

        var link = UnixLink;
        if (link is null)
        {
            // The C library was found by neither soname, so no link can be attempted at all.
            errorCode = 0;
            return HardLinkResult.Refused;
        }

        if (link(sourcePath, targetPath) == 0)
        {
            errorCode = 0;
            return HardLinkResult.Published;
        }

        errorCode = Marshal.GetLastPInvokeError();
        return errorCode == UnixTargetExists
            ? HardLinkResult.TargetAlreadyExists
            : HardLinkResult.Refused;
    }

    [SupportedOSPlatform("windows")]
    private static HardLinkResult PublishOnWindows(
        string sourcePath,
        string targetPath,
        out int errorCode)
    {
        if (CreateHardLink(targetPath, sourcePath, IntPtr.Zero))
        {
            errorCode = 0;
            return HardLinkResult.Published;
        }

        errorCode = Marshal.GetLastWin32Error();
        return errorCode is WindowsFileExists or WindowsAlreadyExists
            ? HardLinkResult.TargetAlreadyExists
            : HardLinkResult.Refused;
    }

    /// <summary>
    /// Binds <c>link</c> from the C library.
    /// </summary>
    /// <remarks>
    /// The bare name "libc" is not loadable on a glibc system, where it resolves to a linker script
    /// rather than to the shared object, so the real sonames are tried first.
    /// </remarks>
    private static LinkFunction? LoadUnixLink()
    {
        foreach (var candidate in new[] { "libc.so.6", "libSystem.dylib", "libc.so", "libc" })
        {
            if (NativeLibrary.TryLoad(candidate, out var library) &&
                NativeLibrary.TryGetExport(library, "link", out var symbol))
            {
                return Marshal.GetDelegateForFunctionPointer<LinkFunction>(symbol);
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newPath,
        string existingPath,
        IntPtr securityAttributes);
}
