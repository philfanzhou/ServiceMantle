namespace ServiceMantle.Logging;

/// <summary>
/// Configures immutable field, header, type, and traversal limits for a
/// <see cref="StructuredLogSanitizer"/>.
/// </summary>
public sealed class StructuredLogSanitizerOptions
{
    /// <summary>
    /// Gets or initializes field names allowed when <see cref="AllowUnlistedFields"/> is false.
    /// Deny rules always take precedence.
    /// </summary>
    public IEnumerable<string> AllowedFieldNames { get; init; } = [];

    /// <summary>
    /// Gets or initializes additional field-name fragments whose complete values are redacted.
    /// Built-in secret-name rules cannot be removed.
    /// </summary>
    public IEnumerable<string> DeniedFieldNames { get; init; } = [];

    /// <summary>
    /// Gets or initializes header names allowed when <see cref="AllowUnlistedHeaders"/> is false.
    /// Deny rules always take precedence.
    /// </summary>
    public IEnumerable<string> AllowedHeaderNames { get; init; } = [];

    /// <summary>
    /// Gets or initializes additional header names whose complete values are redacted.
    /// Built-in authentication and cookie Header rules cannot be removed.
    /// </summary>
    public IEnumerable<string> DeniedHeaderNames { get; init; } = [];

    /// <summary>
    /// Gets or initializes additional types whose instances are replaced without inspection.
    /// </summary>
    public IEnumerable<Type> SensitiveTypes { get; init; } = [];

    /// <summary>
    /// Gets or initializes whether fields absent from <see cref="AllowedFieldNames"/> remain eligible.
    /// </summary>
    public bool AllowUnlistedFields { get; init; } = true;

    /// <summary>
    /// Gets or initializes whether headers absent from <see cref="AllowedHeaderNames"/> remain eligible.
    /// </summary>
    public bool AllowUnlistedHeaders { get; init; } = true;

    /// <summary>Gets or initializes the maximum recursive object-graph depth.</summary>
    public int MaximumDepth { get; init; } = 16;

    /// <summary>Gets or initializes the maximum number of elements emitted from one collection.</summary>
    public int MaximumCollectionCount { get; init; } = 256;

    /// <summary>
    /// Gets or initializes the maximum accepted free-text length. Oversized text is replaced in full.
    /// </summary>
    public int MaximumStringLength { get; init; } = 16_384;
}
