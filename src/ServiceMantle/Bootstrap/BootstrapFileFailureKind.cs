namespace ServiceMantle.Bootstrap;

/// <summary>
/// The closed set of machine-readable reasons a bootstrap file operation failed.
/// </summary>
/// <remarks>
/// <para>
/// A consumer projects this value instead of parsing <see cref="BootstrapException.Message"/>, the
/// file path, or a platform exception. The set is closed: a value that is not listed here is never
/// produced, and a consumer that receives an unrecognized value must treat it as
/// <see cref="Unavailable"/>.
/// </para>
/// <para>
/// Only a failure the store proved is classified beyond <see cref="Unavailable"/>. A failure whose
/// cause the store could not establish - a denied read, an undiagnosed I/O error, a damaged or
/// mismatched file - stays <see cref="Unavailable"/>, and a single negative
/// <see cref="System.IO.File.Exists(string)"/> observation is never treated as proof of absence
/// because it also reports false when the path cannot be inspected.
/// </para>
/// </remarks>
public enum BootstrapFileFailureKind
{
    /// <summary>
    /// The bootstrap file could not be read or written, and the store did not establish that the
    /// target already exists or is missing. This is the default for every unclassified failure.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The operation required a target that does not yet exist, and the operating system refused to
    /// publish over the one that is there. Produced by a create that refuses to overwrite.
    /// </summary>
    TargetAlreadyExists = 1,

    /// <summary>
    /// The operation required an existing target, and the store proved the target is absent by
    /// opening it and being told it does not exist. Produced by a load or a replace.
    /// </summary>
    TargetMissing = 2
}
