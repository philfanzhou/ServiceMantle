using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ServiceMantle.Bootstrap;

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

    private static readonly Type[] BuiltInSensitiveTypes =
    [
        typeof(ISensitiveLogValue),
        typeof(SecureString),
        typeof(NetworkCredential),
        typeof(AuthenticationHeaderValue),
        typeof(HttpHeaders),
        typeof(HttpContent),
        typeof(HttpRequestMessage),
        typeof(HttpResponseMessage),
        typeof(DbConnectionStringBuilder),
        typeof(DbConnection),
        typeof(AsymmetricAlgorithm),
        typeof(X509Certificate),
        typeof(BootstrapConfiguration),
        typeof(BootstrapDatabaseConfiguration)
    ];

    private readonly FrozenSet<Type> sensitiveTypes;
    private readonly int maximumDepth;
    private readonly int maximumCollectionCount;
    private readonly int maximumStringLength;
    private readonly Func<string, int, string> freeTextSanitizer;
    private readonly StructuredLogJsonTraverser jsonTraverser = new();
    private readonly StructuredLogNamePolicy namePolicy;
    private readonly StructuredLogObjectDeconstructor objectDeconstructor = new();

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
        options ??= new StructuredLogSanitizerOptions();
        ArgumentNullException.ThrowIfNull(freeTextSanitizer);
        ValidateLimits(options);

        this.freeTextSanitizer = freeTextSanitizer;
        namePolicy = new StructuredLogNamePolicy(options);
        sensitiveTypes = MaterializeSensitiveTypes(options.SensitiveTypes);
        maximumDepth = options.MaximumDepth;
        maximumCollectionCount = options.MaximumCollectionCount;
        maximumStringLength = options.MaximumStringLength;
    }

    /// <summary>
    /// Sanitizes any supported scalar, dictionary, JSON value, collection, exception, or object.
    /// Unsupported or failing shapes return a safe replacement and never the original value.
    /// </summary>
    public object? Sanitize(object? value)
    {
        try
        {
            return SanitizeValue(
                value,
                depth: 0,
                new HashSet<object>(ReferenceEqualityComparer.Instance));
        }
        catch
        {
            return SanitizationFailed;
        }
    }

    /// <summary>
    /// Sanitizes named structured fields using configured allow/deny rules.
    /// Invalid names are removed and denied names retain only the redaction marker.
    /// </summary>
    public IReadOnlyDictionary<string, object?> SanitizeFields(
        IEnumerable<KeyValuePair<string, object?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        try
        {
            var output = new Dictionary<string, object?>(StringComparer.Ordinal);
            var activeReferences = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var count = 0;
            foreach (var (name, value) in fields)
            {
                if (count++ >= maximumCollectionCount)
                {
                    output["CollectionTruncated"] = CollectionTruncated;
                    break;
                }

                AddField(output, name, value, depth: 0, activeReferences);
            }

            return new ReadOnlyDictionary<string, object?>(output);
        }
        catch
        {
            return FailureDictionary();
        }
    }

    /// <summary>
    /// Sanitizes Headers using a case-insensitive Header allow/deny policy. Header values may be
    /// strings, string collections, or other supported structured values.
    /// </summary>
    public IReadOnlyDictionary<string, object?> SanitizeHeaders(
        IEnumerable<KeyValuePair<string, object?>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        try
        {
            var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var activeReferences = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var count = 0;

            foreach (var (name, value) in headers)
            {
                if (count++ >= maximumCollectionCount)
                {
                    output["CollectionTruncated"] = CollectionTruncated;
                    break;
                }

                if (!namePolicy.TryClassifyHeader(
                        name,
                        out var outputName,
                        out var denied,
                        out var allowed) ||
                    !allowed)
                {
                    continue;
                }

                object? sanitizedValue;
                if (denied)
                {
                    sanitizedValue = RedactedValue;
                }
                else
                {
                    sanitizedValue = SanitizeValue(value, depth: 0, activeReferences);
                }

                if (!output.TryAdd(outputName, sanitizedValue))
                {
                    output[outputName] = RedactedValue;
                }
            }

            return new ReadOnlyDictionary<string, object?>(output);
        }
        catch
        {
            return FailureDictionary();
        }
    }

    /// <summary>
    /// Applies the explicitly bounded best-effort free-text contract. Recognized secret assignments,
    /// credential URIs, connection strings, bearer tokens, JWT-like values, and private-key blocks are
    /// redacted. Unlabelled opaque secrets are not detectable; use structured denied fields, denied
    /// Headers, <see cref="ISensitiveLogValue"/>, or registered sensitive types for a hard guarantee.
    /// </summary>
    public string? SanitizeFreeText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return freeTextSanitizer(value, maximumStringLength);
        }
        catch
        {
            return SanitizationFailed;
        }
    }

    private object? SanitizeValue(
        object? value,
        int depth,
        HashSet<object> activeReferences)
    {
        if (value is null)
        {
            return null;
        }

        if (depth > maximumDepth)
        {
            return MaximumDepthExceeded;
        }

        var type = value.GetType();
        if (IsSensitiveType(type))
        {
            return RedactedValue;
        }

        if (value is string text)
        {
            return SanitizeFreeText(text);
        }

        if (value is char character)
        {
            return SanitizeFreeText(character.ToString());
        }

        if (IsBinary(value))
        {
            return BinaryValue;
        }

        if (StructuredLogSafeScalar.IsSupported(type))
        {
            return StructuredLogSafeScalar.Normalize(value);
        }

        if (value is Uri uri)
        {
            return SanitizeFreeText(uri.AbsoluteUri);
        }

        if (value is IPAddress ipAddress)
        {
            return ipAddress.ToString();
        }

        if (value is Type reflectedType)
        {
            return reflectedType.FullName ?? reflectedType.Name;
        }

        var trackReference = !type.IsValueType;
        if (trackReference && !activeReferences.Add(value))
        {
            return CircularReference;
        }

        try
        {
            if (jsonTraverser.TrySanitize(
                    value,
                    depth,
                    activeReferences,
                    this,
                    out var sanitizedJson))
            {
                return sanitizedJson;
            }

            if (objectDeconstructor.TryDeconstruct(
                    value,
                    depth,
                    activeReferences,
                    this,
                    out var deconstructed))
            {
                return deconstructed;
            }

            return $"[OBJECT:{type.FullName ?? type.Name}]";
        }
        catch
        {
            return SanitizationFailed;
        }
        finally
        {
            if (trackReference)
            {
                activeReferences.Remove(value);
            }
        }
    }

    internal int MaximumCollectionCount => maximumCollectionCount;

    internal object? SanitizeNestedValue(
        object? value,
        int depth,
        HashSet<object> activeReferences) =>
        SanitizeValue(value, depth, activeReferences);

    internal void AddField(
        IDictionary<string, object?> output,
        string name,
        object? value,
        int depth,
        HashSet<object> activeReferences)
    {
        if (!namePolicy.TryClassifyField(
                name,
                out var outputName,
                out var denied,
                out var allowed) ||
            !allowed)
        {
            return;
        }

        var sanitizedValue = denied
            ? RedactedValue
            : SanitizeValue(value, depth, activeReferences);
        AddOutput(output, outputName, sanitizedValue);
    }

    internal static void AddOutput(
        IDictionary<string, object?> output,
        string name,
        object? value)
    {
        if (!output.TryAdd(name, value))
        {
            output[name] = RedactedValue;
        }
    }

    internal StructuredLogNamePolicy NamePolicy => namePolicy;

    private bool IsSensitiveType(Type actualType) =>
        sensitiveTypes.Any(sensitiveType => sensitiveType.IsAssignableFrom(actualType));

    internal static bool IsSupportedJsonValueScalar(object? value) =>
        value is JsonElement or string or char ||
        value is not null && StructuredLogSafeScalar.IsSupported(value.GetType());

    private static bool IsBinary(object value) =>
        value is Memory<byte> or
            ReadOnlyMemory<byte> or
            ArraySegment<byte> or
            IEnumerable<byte> ||
        value.GetType() is { IsArray: true } type && type.GetElementType() == typeof(byte);

    private static FrozenSet<Type> MaterializeSensitiveTypes(IEnumerable<Type> configured)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configured);
            var types = new HashSet<Type>(BuiltInSensitiveTypes);
            foreach (var type in configured)
            {
                if (type is null || type.ContainsGenericParameters)
                {
                    throw new ArgumentException();
                }

                types.Add(type);
            }

            return types.ToFrozenSet();
        }
        catch
        {
            throw new ArgumentException("Structured log sensitive type options are invalid.");
        }
    }

    private static void ValidateLimits(StructuredLogSanitizerOptions options)
    {
        if (options.MaximumDepth is < 1 or > 128 ||
            options.MaximumCollectionCount is < 1 or > 100_000 ||
            options.MaximumStringLength is < 1 or > 1_000_000)
        {
            throw new ArgumentException("Structured log traversal limits are invalid.");
        }
    }

    private static IReadOnlyDictionary<string, object?> FailureDictionary() =>
        new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["SanitizationFailure"] = SanitizationFailed
            });
}
