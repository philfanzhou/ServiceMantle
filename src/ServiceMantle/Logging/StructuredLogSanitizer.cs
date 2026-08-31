namespace ServiceMantle.Logging;

/// <summary>
/// Produces a new, sink-neutral object graph after applying fail-closed field, Header, type,
/// free-text, recursion, and traversal rules. It never returns an input object as a fallback.
/// </summary>
public sealed class StructuredLogSanitizer
{
    /// <summary>The stable replacement for a recognized sensitive value.</summary>
    public const string RedactedValue = "[REDACTED]";

    /// <summary>The stable replacement when cleaning cannot complete safely.</summary>
    public const string SanitizationFailed = "[SANITIZATION_FAILED]";

    /// <summary>The stable replacement for a circular reference.</summary>
    public const string CircularReference = "[CIRCULAR_REFERENCE]";

    /// <summary>The stable replacement when recursive traversal exceeds its configured bound.</summary>
    public const string MaximumDepthExceeded = "[MAXIMUM_DEPTH_EXCEEDED]";

    /// <summary>The stable marker appended when a collection exceeds its configured bound.</summary>
    public const string CollectionTruncated = "[COLLECTION_TRUNCATED]";

    /// <summary>The stable replacement for binary material.</summary>
    public const string BinaryValue = "[BINARY_REDACTED]";

    /// <summary>The stable replacement for oversized free text.</summary>
    public const string OversizedValue = "[OVERSIZED_VALUE_REDACTED]";

    /// <summary>The stable replacement for a scalar no sink can represent.</summary>
    public const string UnrepresentableValue = "[UNREPRESENTABLE_VALUE]";

    private readonly StructuredLogTraversalCoordinator traversalCoordinator;

    /// <summary>
    /// Initializes a sanitizer and snapshots all mutable option collections.
    /// </summary>
    /// <exception cref="ArgumentException">An option is invalid or cannot be read safely.</exception>
    public StructuredLogSanitizer(StructuredLogSanitizerOptions? options = null)
        : this(options, LogFreeTextSanitizer.Sanitize)
    {
    }

    internal StructuredLogSanitizer(
        StructuredLogSanitizerOptions? options,
        Func<string, int, string> freeTextSanitizer)
    {
        ArgumentNullException.ThrowIfNull(freeTextSanitizer);
        traversalCoordinator = new StructuredLogTraversalCoordinator(
            options ?? new StructuredLogSanitizerOptions(),
            freeTextSanitizer);
    }

    /// <summary>
    /// Sanitizes any supported scalar, dictionary, JSON value, collection, exception, or object.
    /// Unsupported or failing shapes return a safe replacement and never the original value.
    /// </summary>
    public object? Sanitize(object? value) => traversalCoordinator.Sanitize(value);

    /// <summary>
    /// Sanitizes named structured fields using configured allow/deny rules.
    /// Invalid names are removed and denied names retain only the redaction marker.
    /// </summary>
    public IReadOnlyDictionary<string, object?> SanitizeFields(
        IEnumerable<KeyValuePair<string, object?>> fields)
        => traversalCoordinator.SanitizeFields(fields);

    /// <summary>
    /// Sanitizes Headers using a case-insensitive Header allow/deny policy. Header values may be
    /// strings, string collections, or other supported structured values.
    /// </summary>
    public IReadOnlyDictionary<string, object?> SanitizeHeaders(
        IEnumerable<KeyValuePair<string, object?>> headers)
        => traversalCoordinator.SanitizeHeaders(headers);

    /// <summary>
    /// Applies the explicitly bounded best-effort free-text contract. Recognized secret assignments,
    /// credential URIs, connection strings, bearer tokens, JWT-like values, and private-key blocks are
    /// redacted. Unlabelled opaque secrets are not detectable; use structured denied fields, denied
    /// Headers, <see cref="ISensitiveLogValue"/>, or registered sensitive types for a hard guarantee.
    /// </summary>
    public string? SanitizeFreeText(string? value) =>
        traversalCoordinator.SanitizeFreeText(value);
}
