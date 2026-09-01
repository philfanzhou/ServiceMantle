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
/// Coordinates value dispatch, reference tracking, traversal budgets, and failure boundaries for
/// every structured-log entry point.
/// </summary>
internal sealed class StructuredLogTraversalCoordinator
{
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

    private readonly Func<string, int, string> freeTextSanitizer;
    private readonly StructuredLogJsonTraverser jsonTraverser = new();
    private readonly int maximumCollectionCount;
    private readonly int maximumDepth;
    private readonly int maximumStringLength;
    private readonly StructuredLogNamePolicy namePolicy;
    private readonly StructuredLogObjectDeconstructor objectDeconstructor = new();
    private readonly FrozenSet<Type> sensitiveTypes;

    internal StructuredLogTraversalCoordinator(
        StructuredLogSanitizerOptions options,
        Func<string, int, string> freeTextSanitizer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(freeTextSanitizer);
        ValidateLimits(options);

        this.freeTextSanitizer = freeTextSanitizer;
        namePolicy = new StructuredLogNamePolicy(options);
        sensitiveTypes = MaterializeSensitiveTypes(options.SensitiveTypes);
        maximumDepth = options.MaximumDepth;
        maximumCollectionCount = options.MaximumCollectionCount;
        maximumStringLength = options.MaximumStringLength;
    }

    internal object? Sanitize(object? value)
    {
        try
        {
            return new TraversalContext(this).SanitizeNestedValue(value, depth: 0);
        }
        catch
        {
            return StructuredLogSanitizer.SanitizationFailed;
        }
    }

    internal IReadOnlyDictionary<string, object?> SanitizeFields(
        IEnumerable<KeyValuePair<string, object?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        try
        {
            var output = new Dictionary<string, object?>(StringComparer.Ordinal);
            var context = new TraversalContext(this);
            var count = 0;
            foreach (var (name, value) in fields)
            {
                if (!context.TryAcceptCollectionItem(ref count))
                {
                    output["CollectionTruncated"] = StructuredLogSanitizer.CollectionTruncated;
                    break;
                }

                context.AddField(output, name, value, depth: 0);
            }

            return new ReadOnlyDictionary<string, object?>(output);
        }
        catch
        {
            return FailureDictionary();
        }
    }

    internal IReadOnlyDictionary<string, object?> SanitizeHeaders(
        IEnumerable<KeyValuePair<string, object?>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        try
        {
            var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var context = new TraversalContext(this);
            var count = 0;
            foreach (var (name, value) in headers)
            {
                if (!context.TryAcceptCollectionItem(ref count))
                {
                    output["CollectionTruncated"] = StructuredLogSanitizer.CollectionTruncated;
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

                var sanitizedValue = denied
                    ? StructuredLogSanitizer.RedactedValue
                    : context.SanitizeNestedValue(value, depth: 0);
                AddOutput(output, outputName, sanitizedValue);
            }

            return new ReadOnlyDictionary<string, object?>(output);
        }
        catch
        {
            return FailureDictionary();
        }
    }

    internal string? SanitizeFreeText(string? value)
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
            return StructuredLogSanitizer.SanitizationFailed;
        }
    }

    private object? SanitizeValue(object? value, int depth, TraversalContext context)
    {
        if (value is null)
        {
            return null;
        }

        if (IsDepthExceeded(depth))
        {
            return StructuredLogSanitizer.MaximumDepthExceeded;
        }

        var type = value.GetType();
        if (IsSensitiveType(type))
        {
            return StructuredLogSanitizer.RedactedValue;
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
            return StructuredLogSanitizer.BinaryValue;
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
        if (trackReference && !context.TryEnterReference(value))
        {
            return StructuredLogSanitizer.CircularReference;
        }

        try
        {
            if (jsonTraverser.TrySanitize(value, depth, context, out var sanitizedJson))
            {
                return sanitizedJson;
            }

            if (objectDeconstructor.TryDeconstruct(value, depth, context, out var deconstructed))
            {
                return deconstructed;
            }

            return $"[OBJECT:{type.FullName ?? type.Name}]";
        }
        catch
        {
            return StructuredLogSanitizer.SanitizationFailed;
        }
        finally
        {
            if (trackReference)
            {
                context.ExitReference(value);
            }
        }
    }

    private void AddField(
        IDictionary<string, object?> output,
        string name,
        object? value,
        int depth,
        TraversalContext context)
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
            ? StructuredLogSanitizer.RedactedValue
            : SanitizeValue(value, depth, context);
        AddOutput(output, outputName, sanitizedValue);
    }

    private static void AddOutput(
        IDictionary<string, object?> output,
        string name,
        object? value)
    {
        if (!output.TryAdd(name, value))
        {
            output[name] = StructuredLogSanitizer.RedactedValue;
        }
    }

    private bool IsDepthExceeded(int depth) => depth > maximumDepth;

    private bool IsSensitiveType(Type actualType) =>
        sensitiveTypes.Any(sensitiveType => sensitiveType.IsAssignableFrom(actualType));

    private bool TryAcceptCollectionItem(ref int count) => count++ < maximumCollectionCount;

    private static bool IsSupportedJsonValueScalar(object? value) =>
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
                ["SanitizationFailure"] = StructuredLogSanitizer.SanitizationFailed
            });

    /// <summary>
    /// Carries the one reference set and delegates every recursive decision back to its owning
    /// coordinator. JSON and reflection paths receive this context instead of sanitizer internals.
    /// </summary>
    internal sealed class TraversalContext(StructuredLogTraversalCoordinator coordinator)
    {
        private readonly HashSet<object> activeReferences =
            new(ReferenceEqualityComparer.Instance);

        internal void AddField(
            IDictionary<string, object?> output,
            string name,
            object? value,
            int depth) =>
            coordinator.AddField(output, name, value, depth, this);

        internal void AddOutput(
            IDictionary<string, object?> output,
            string name,
            object? value) =>
            StructuredLogTraversalCoordinator.AddOutput(output, name, value);

        internal void ExitReference(object value) => activeReferences.Remove(value);

        internal bool IsDeniedField(string name) => coordinator.namePolicy.IsDeniedField(name);

        internal bool IsDepthExceeded(int depth) => coordinator.IsDepthExceeded(depth);

        internal object? SanitizeJsonPrimitive(object? value, bool sanitizeText) =>
            sanitizeText && value is string text
                ? coordinator.SanitizeFreeText(text)
                : value;

        internal object? SanitizeNestedValue(object? value, int depth) =>
            coordinator.SanitizeValue(value, depth, this);

        internal bool IsSupportedJsonValueScalar(object? value) =>
            StructuredLogTraversalCoordinator.IsSupportedJsonValueScalar(value);

        internal bool TryAcceptCollectionItem(ref int count) =>
            coordinator.TryAcceptCollectionItem(ref count);

        internal bool TryClassifyField(
            string name,
            out string outputName,
            out bool denied,
            out bool allowed) =>
            coordinator.namePolicy.TryClassifyField(name, out outputName, out denied, out allowed);

        internal bool TryEnterReference(object value) => activeReferences.Add(value);

        internal bool TryGetFieldName(object? key, out string fieldName) =>
            coordinator.namePolicy.TryGetFieldName(key, out fieldName);
    }
}
