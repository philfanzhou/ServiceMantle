using System.Collections;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static readonly string[] BuiltInDeniedFieldFragments =
    [
        "password",
        "passwd",
        "passphrase",
        "pwd",
        "secret",
        "token",
        "apikey",
        "connectionstring",
        "connstr",
        "credential",
        "privatekey",
        "rootkey",
        "masterkey",
        "setupcode",
        "clientsecret",
        "accesskey",
        "accountkey",
        "authorization",
        "cookie"
    ];

    private static readonly string[] BuiltInDeniedHeaders =
    [
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "x-api-key",
        "x-auth-token"
    ];

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

    private readonly FrozenSet<string> allowedFields;
    private readonly FrozenSet<string> deniedFieldFragments;
    private readonly FrozenSet<string> allowedHeaders;
    private readonly FrozenSet<string> deniedHeaders;
    private readonly FrozenSet<Type> sensitiveTypes;
    private readonly bool allowUnlistedFields;
    private readonly bool allowUnlistedHeaders;
    private readonly int maximumDepth;
    private readonly int maximumCollectionCount;
    private readonly int maximumStringLength;

    /// <summary>
    /// Initializes a sanitizer and snapshots all mutable option collections.
    /// </summary>
    /// <exception cref="ArgumentException">An option is invalid or cannot be read safely.</exception>
    public StructuredLogSanitizer(StructuredLogSanitizerOptions? options = null)
    {
        options ??= new StructuredLogSanitizerOptions();
        ValidateLimits(options);

        allowedFields = MaterializeFieldNames(options.AllowedFieldNames, []);
        deniedFieldFragments = MaterializeFieldNames(
            options.DeniedFieldNames,
            BuiltInDeniedFieldFragments);
        allowedHeaders = MaterializeHeaderNames(options.AllowedHeaderNames, []);
        deniedHeaders = MaterializeHeaderNames(
            options.DeniedHeaderNames,
            BuiltInDeniedHeaders);
        sensitiveTypes = MaterializeSensitiveTypes(options.SensitiveTypes);
        allowUnlistedFields = options.AllowUnlistedFields;
        allowUnlistedHeaders = options.AllowUnlistedHeaders;
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

                if (!TryNormalizeHeaderName(name, out var normalizedName))
                {
                    continue;
                }

                object? sanitizedValue;
                if (deniedHeaders.Contains(normalizedName))
                {
                    sanitizedValue = RedactedValue;
                }
                else if (!allowUnlistedHeaders && !allowedHeaders.Contains(normalizedName))
                {
                    continue;
                }
                else
                {
                    sanitizedValue = SanitizeValue(value, depth: 0, activeReferences);
                }

                if (!output.TryAdd(name.Trim(), sanitizedValue))
                {
                    output[name.Trim()] = RedactedValue;
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
            return LogFreeTextSanitizer.Sanitize(value, maximumStringLength);
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

        if (IsSafeScalar(type))
        {
            return value;
        }

        if (type.IsEnum)
        {
            try
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return SanitizationFailed;
            }
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
            if (value is Exception exception)
            {
                return SanitizeException(exception, depth, activeReferences);
            }

            if (value is JsonDocument jsonDocument)
            {
                return SanitizeJson(jsonDocument.RootElement, depth, activeReferences);
            }

            if (value is JsonElement jsonElement)
            {
                return SanitizeJson(jsonElement, depth, activeReferences);
            }

            if (value is JsonNode jsonNode)
            {
                return SanitizeJsonNode(jsonNode, depth, activeReferences);
            }

            if (value is IDictionary dictionary)
            {
                return SanitizeDictionary(dictionary, depth, activeReferences);
            }

            if (TrySanitizeGenericDictionary(
                    value,
                    depth,
                    activeReferences,
                    out var genericDictionary))
            {
                return genericDictionary;
            }

            if (value is IEnumerable enumerable)
            {
                return SanitizeEnumerable(enumerable, depth, activeReferences);
            }

            if (value is Delegate or Stream or Task)
            {
                return $"[OBJECT:{type.FullName ?? type.Name}]";
            }

            return SanitizeObject(value, depth, activeReferences);
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

    private IReadOnlyDictionary<string, object?> SanitizeException(
        Exception exception,
        int depth,
        HashSet<object> activeReferences)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ExceptionType"] = exception.GetType().FullName ?? exception.GetType().Name
        };

        if (exception.InnerException is not null)
        {
            output["InnerException"] = SanitizeValue(
                exception.InnerException,
                depth + 1,
                activeReferences);
        }

        return new ReadOnlyDictionary<string, object?>(output);
    }

    private object? SanitizeJson(
        JsonElement element,
        int depth,
        HashSet<object> activeReferences)
    {
        if (depth > maximumDepth)
        {
            return MaximumDepthExceeded;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var output = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var count = 0;
                    foreach (var property in element.EnumerateObject())
                    {
                        if (count++ >= maximumCollectionCount)
                        {
                            output["CollectionTruncated"] = CollectionTruncated;
                            break;
                        }

                        AddField(
                            output,
                            property.Name,
                            property.Value,
                            depth + 1,
                            activeReferences);
                    }

                    return new ReadOnlyDictionary<string, object?>(output);
                }

            case JsonValueKind.Array:
                {
                    var output = new List<object?>();
                    var count = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (count++ >= maximumCollectionCount)
                        {
                            output.Add(CollectionTruncated);
                            break;
                        }

                        output.Add(SanitizeJson(item, depth + 1, activeReferences));
                    }

                    return output.AsReadOnly();
                }

            case JsonValueKind.String:
                return SanitizeFreeText(element.GetString());

            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var number))
                {
                    return number;
                }

                return element.GetRawText();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
                return null;

            default:
                return SanitizationFailed;
        }
    }

    private object? SanitizeJsonNode(
        JsonNode node,
        int depth,
        HashSet<object> activeReferences)
    {
        if (depth > maximumDepth)
        {
            return MaximumDepthExceeded;
        }

        switch (node)
        {
            case JsonObject jsonObject:
                {
                    var output = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var count = 0;
                    foreach (var property in jsonObject)
                    {
                        if (count++ >= maximumCollectionCount)
                        {
                            output["CollectionTruncated"] = CollectionTruncated;
                            break;
                        }

                        AddField(
                            output,
                            property.Key,
                            property.Value,
                            depth + 1,
                            activeReferences);
                    }

                    return new ReadOnlyDictionary<string, object?>(output);
                }

            case JsonArray jsonArray:
                {
                    var output = new List<object?>();
                    var count = 0;
                    foreach (var item in jsonArray)
                    {
                        if (count++ >= maximumCollectionCount)
                        {
                            output.Add(CollectionTruncated);
                            break;
                        }

                        output.Add(SanitizeValue(item, depth + 1, activeReferences));
                    }

                    return output.AsReadOnly();
                }

            case JsonValue jsonValue:
                {
                    if (!jsonValue.TryGetValue<object>(out var value) ||
                        !IsSupportedJsonValueScalar(value))
                    {
                        return SanitizationFailed;
                    }

                    if (value is JsonElement element)
                    {
                        return element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                            ? SanitizationFailed
                            : SanitizeJson(element, depth, activeReferences);
                    }

                    var scalarElement = JsonSerializer.SerializeToElement(value, value.GetType());
                    return SanitizeJson(scalarElement, depth, activeReferences);
                }

            default:
                return SanitizationFailed;
        }
    }

    private IReadOnlyDictionary<string, object?> SanitizeDictionary(
        IDictionary dictionary,
        int depth,
        HashSet<object> activeReferences)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        var count = 0;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (count++ >= maximumCollectionCount)
            {
                output["CollectionTruncated"] = CollectionTruncated;
                break;
            }

            if (!TryGetFieldName(entry.Key, out var fieldName))
            {
                continue;
            }

            AddField(output, fieldName, entry.Value, depth + 1, activeReferences);
        }

        return new ReadOnlyDictionary<string, object?>(output);
    }

    private bool TrySanitizeGenericDictionary(
        object value,
        int depth,
        HashSet<object> activeReferences,
        out object? sanitized)
    {
        var type = value.GetType();
        var isDictionary = type.GetInterfaces().Any(candidate =>
        {
            if (!candidate.IsGenericType)
            {
                return false;
            }

            var definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return true;
            }

            if (definition != typeof(IEnumerable<>))
            {
                return false;
            }

            var elementType = candidate.GetGenericArguments()[0];
            return elementType.IsGenericType &&
                elementType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
        });

        if (!isDictionary)
        {
            sanitized = null;
            return false;
        }

        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        var count = 0;
        foreach (var item in (IEnumerable)value)
        {
            if (count++ >= maximumCollectionCount)
            {
                output["CollectionTruncated"] = CollectionTruncated;
                break;
            }

            if (item is null)
            {
                continue;
            }

            var itemType = item.GetType();
            var keyProperty = itemType.GetProperty("Key", BindingFlags.Instance | BindingFlags.Public);
            var valueProperty = itemType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
            if (keyProperty is null || valueProperty is null)
            {
                throw new InvalidOperationException();
            }

            var key = keyProperty.GetValue(item);
            if (!TryGetFieldName(key, out var fieldName))
            {
                continue;
            }

            if (IsDeniedField(fieldName))
            {
                AddField(output, fieldName, value: null, depth + 1, activeReferences);
                continue;
            }

            AddField(
                output,
                fieldName,
                valueProperty.GetValue(item),
                depth + 1,
                activeReferences);
        }

        sanitized = new ReadOnlyDictionary<string, object?>(output);
        return true;
    }

    private IReadOnlyList<object?> SanitizeEnumerable(
        IEnumerable enumerable,
        int depth,
        HashSet<object> activeReferences)
    {
        var output = new List<object?>();
        var count = 0;
        foreach (var item in enumerable)
        {
            if (count++ >= maximumCollectionCount)
            {
                output.Add(CollectionTruncated);
                break;
            }

            output.Add(SanitizeValue(item, depth + 1, activeReferences));
        }

        return output.AsReadOnly();
    }

    private object SanitizeObject(
        object value,
        int depth,
        HashSet<object> activeReferences)
    {
        var type = value.GetType();
        var members = type
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Where(member => member is PropertyInfo or FieldInfo)
            .OrderBy(member => member.Name, StringComparer.Ordinal)
            .ToArray();

        if (members.Length == 0)
        {
            return $"[OBJECT:{type.FullName ?? type.Name}]";
        }

        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        var count = 0;
        foreach (var member in members)
        {
            if (count++ >= maximumCollectionCount)
            {
                output["CollectionTruncated"] = CollectionTruncated;
                break;
            }

            if (!TryClassifyField(member.Name, out var outputName, out var denied, out var allowed) ||
                !allowed)
            {
                continue;
            }

            if (denied)
            {
                AddOutput(output, outputName, RedactedValue);
                continue;
            }

            object? memberValue;
            try
            {
                memberValue = member switch
                {
                    PropertyInfo property when
                        property.GetMethod is not null &&
                        !property.GetMethod.IsStatic &&
                        property.GetIndexParameters().Length == 0 => property.GetValue(value),
                    FieldInfo field when !field.IsStatic => field.GetValue(value),
                    _ => SanitizationFailed
                };
            }
            catch
            {
                memberValue = SanitizationFailed;
            }

            AddOutput(
                output,
                outputName,
                SanitizeValue(memberValue, depth + 1, activeReferences));
        }

        return new ReadOnlyDictionary<string, object?>(output);
    }

    private void AddField(
        IDictionary<string, object?> output,
        string name,
        object? value,
        int depth,
        HashSet<object> activeReferences)
    {
        if (!TryClassifyField(name, out var outputName, out var denied, out var allowed) || !allowed)
        {
            return;
        }

        var sanitizedValue = denied
            ? RedactedValue
            : SanitizeValue(value, depth, activeReferences);
        AddOutput(output, outputName, sanitizedValue);
    }

    private static void AddOutput(
        IDictionary<string, object?> output,
        string name,
        object? value)
    {
        if (!output.TryAdd(name, value))
        {
            output[name] = RedactedValue;
        }
    }

    private bool TryClassifyField(
        string name,
        out string outputName,
        out bool denied,
        out bool allowed)
    {
        if (!TryNormalizeFieldName(name, out outputName, out var policyName))
        {
            denied = true;
            allowed = false;
            return false;
        }

        denied = deniedFieldFragments.Any(fragment =>
            policyName.Contains(fragment, StringComparison.Ordinal));
        allowed = denied || allowUnlistedFields || allowedFields.Contains(policyName);
        return true;
    }

    private bool IsDeniedField(string name) =>
        TryClassifyField(name, out _, out var denied, out _) && denied;

    private bool IsSensitiveType(Type actualType) =>
        sensitiveTypes.Any(sensitiveType => sensitiveType.IsAssignableFrom(actualType));

    private static bool IsSafeScalar(Type type) =>
        Type.GetTypeCode(type) is
            TypeCode.Boolean or
            TypeCode.Byte or
            TypeCode.SByte or
            TypeCode.Int16 or
            TypeCode.UInt16 or
            TypeCode.Int32 or
            TypeCode.UInt32 or
            TypeCode.Int64 or
            TypeCode.UInt64 or
            TypeCode.Single or
            TypeCode.Double or
            TypeCode.Decimal or
            TypeCode.DateTime ||
        type == typeof(Guid) ||
        type == typeof(DateOnly) ||
        type == typeof(TimeOnly) ||
        type == typeof(TimeSpan);

    private static bool IsSupportedJsonValueScalar(object? value) =>
        value is JsonElement or string or char or DateTimeOffset ||
        value is not null && (IsSafeScalar(value.GetType()) || value.GetType().IsEnum);

    private static bool IsBinary(object value) =>
        value is Memory<byte> or
            ReadOnlyMemory<byte> or
            ArraySegment<byte> or
            IEnumerable<byte> ||
        value.GetType() is { IsArray: true } type && type.GetElementType() == typeof(byte);

    private static bool TryGetFieldName(object? key, out string fieldName)
    {
        switch (key)
        {
            case string text:
                fieldName = text;
                return true;
            case Guid guid:
                fieldName = guid.ToString("D");
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                fieldName = Convert.ToString(key, CultureInfo.InvariantCulture)!;
                return true;
            default:
                fieldName = string.Empty;
                return false;
        }
    }

    private static bool TryNormalizeFieldName(
        string? name,
        out string outputName,
        out string policyName)
    {
        outputName = string.Empty;
        policyName = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var candidate = name.Trim();
        if (candidate.Length > 128)
        {
            return false;
        }

        Span<char> policyBuffer = stackalloc char[candidate.Length];
        var policyLength = 0;
        foreach (var character in candidate)
        {
            if (character is >= 'A' and <= 'Z')
            {
                policyBuffer[policyLength++] = (char)(character + ('a' - 'A'));
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                policyBuffer[policyLength++] = character;
            }
            else if (character is not ('.' or '_' or '-' or '@' or ':' or '/' or '[' or ']'))
            {
                return false;
            }
        }

        if (policyLength == 0)
        {
            return false;
        }

        outputName = candidate;
        policyName = new string(policyBuffer[..policyLength]);
        return true;
    }

    private static string NormalizeConfiguredFieldName(string name)
    {
        if (!TryNormalizeFieldName(name, out _, out var policyName))
        {
            throw new ArgumentException("A structured log field policy name is invalid.");
        }

        return policyName;
    }

    private static bool TryNormalizeHeaderName(string? name, out string normalizedName)
    {
        normalizedName = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var candidate = name.Trim();
        if (candidate.Length > 128)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[candidate.Length];
        for (var index = 0; index < candidate.Length; index++)
        {
            var character = candidate[index];
            if (character is >= 'A' and <= 'Z')
            {
                buffer[index] = (char)(character + ('a' - 'A'));
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            {
                buffer[index] = character;
            }
            else
            {
                return false;
            }
        }

        normalizedName = new string(buffer);
        return true;
    }

    private static FrozenSet<string> MaterializeFieldNames(
        IEnumerable<string> configured,
        IEnumerable<string> builtIn)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configured);
            return builtIn
                .Concat(configured)
                .Select(NormalizeConfiguredFieldName)
                .ToFrozenSet(StringComparer.Ordinal);
        }
        catch
        {
            throw new ArgumentException("Structured log field policy options are invalid.");
        }
    }

    private static FrozenSet<string> MaterializeHeaderNames(
        IEnumerable<string> configured,
        IEnumerable<string> builtIn)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configured);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in builtIn.Concat(configured))
            {
                if (!TryNormalizeHeaderName(name, out var normalizedName))
                {
                    throw new ArgumentException();
                }

                names.Add(normalizedName);
            }

            return names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            throw new ArgumentException("Structured log Header policy options are invalid.");
        }
    }

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
