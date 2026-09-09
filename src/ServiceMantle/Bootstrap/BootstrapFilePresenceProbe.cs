namespace ServiceMantle.Bootstrap;

/// <summary>
/// What one read-only open established about the Bootstrap file.
/// </summary>
internal enum BootstrapFilePresence
{
    /// <summary>The open reported the file, or its directory, as not found.</summary>
    Absent,

    /// <summary>The open succeeded, which is proof the file is there.</summary>
    Present,

    /// <summary>
    /// The open failed for a reason that leaves existence unestablished. This is neither evidence
    /// of presence nor evidence of absence.
    /// </summary>
    Unknown
}

/// <summary>
/// Decides whether the Bootstrap file exists from the answer of one read-only open.
/// </summary>
/// <remarks>
/// <para>
/// A negative <see cref="File.Exists(string)"/> result also means "the path could not be inspected":
/// on a directory the caller cannot traverse it returns <see langword="false"/> for a file that is
/// present, and the access failure it swallowed never reaches a handler. An open separates the two
/// answers, because the operating system reporting the file or its directory as not found is proof
/// of absence, while a denied or failed open is not.
/// </para>
/// <para>
/// The open takes read access only, shares read, write, and delete so it interferes with nothing
/// else, and reads no byte: existence is the whole question, and the Bootstrap file's connection
/// string and MasterKey are never touched by it.
/// </para>
/// <para>
/// This is a Bootstrap existence probe and not a file system abstraction. It answers one question
/// about one path, and the evidence it gives is only about the moment of the open: an outside
/// process may create, replace, or delete the file immediately afterwards.
/// </para>
/// </remarks>
internal static class BootstrapFilePresenceProbe
{
    /// <summary>
    /// Opens the Bootstrap file for existence evidence only, without reading it.
    /// </summary>
    internal static FileStream Open(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);

    /// <summary>
    /// Classifies what <paramref name="open"/> answered about <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The Bootstrap file path to probe.</param>
    /// <param name="open">
    /// The open to use. Production passes <see cref="Open"/>; a test passes an opener that produces
    /// one of the finite failures this classification is defined over.
    /// </param>
    internal static BootstrapFilePresence Probe(string path, Func<string, FileStream> open)
    {
        FileStream stream;
        try
        {
            stream = open(path);
        }
        catch (FileNotFoundException)
        {
            return BootstrapFilePresence.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return BootstrapFilePresence.Absent;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return BootstrapFilePresence.Unknown;
        }

        try
        {
            stream.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The open already proved the file is there, so this is the conservative half of the
            // classification rather than a necessary one: a probe that could not complete cleanly
            // reports an unestablished result instead of a positive one.
            return BootstrapFilePresence.Unknown;
        }

        return BootstrapFilePresence.Present;
    }
}
