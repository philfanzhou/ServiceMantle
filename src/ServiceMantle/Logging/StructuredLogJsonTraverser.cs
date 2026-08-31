using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServiceMantle.Logging;

/// <summary>
/// Owns bounded traversal of every supported JSON representation. Container budgets and primitive
/// conversion are deliberately shared so a JSON representation cannot introduce its own bypass.
/// </summary>
internal sealed class StructuredLogJsonTraverser
{
    internal bool TrySanitize(
        object value,
        int depth,
        HashSet<object> activeReferences,
        StructuredLogSanitizer sanitizer,
        out object? sanitized)
    {
        if (!JsonTraversalValue.TryCreate(value, out var jsonValue))
        {
            sanitized = null;
            return false;
        }

        sanitized = Sanitize(jsonValue, depth, activeReferences, sanitizer);
        return true;
    }

    private static object? Sanitize(
        JsonTraversalValue value,
        int depth,
        HashSet<object> activeReferences,
        StructuredLogSanitizer sanitizer)
    {
        if (depth > sanitizer.MaximumDepth)
        {
            return StructuredLogSanitizer.MaximumDepthExceeded;
        }

        return value.Kind switch
        {
            JsonTraversalKind.Object => SanitizeObject(value, depth, activeReferences, sanitizer),
            JsonTraversalKind.Array => SanitizeArray(value, depth, activeReferences, sanitizer),
            JsonTraversalKind.Scalar => SanitizeScalar(value, depth, activeReferences, sanitizer),
            _ => StructuredLogSanitizer.SanitizationFailed
        };
    }

    private static IReadOnlyDictionary<string, object?> SanitizeObject(
        JsonTraversalValue value,
        int depth,
        HashSet<object> activeReferences,
        StructuredLogSanitizer sanitizer)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (count++ >= sanitizer.MaximumCollectionCount)
            {
                output["CollectionTruncated"] = StructuredLogSanitizer.CollectionTruncated;
                break;
            }

            sanitizer.AddField(
                output,
                property.Name,
                property.Value,
                depth + 1,
                activeReferences);
        }

        return new ReadOnlyDictionary<string, object?>(output);
    }

    private static IReadOnlyList<object?> SanitizeArray(
        JsonTraversalValue value,
        int depth,
        HashSet<object> activeReferences,
        StructuredLogSanitizer sanitizer)
    {
        var output = new List<object?>();
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (count++ >= sanitizer.MaximumCollectionCount)
            {
                output.Add(StructuredLogSanitizer.CollectionTruncated);
                break;
            }

            output.Add(value.SanitizeArrayItem(
                item,
                depth + 1,
                activeReferences,
                sanitizer));
        }

        return output.AsReadOnly();
    }

    private static object? SanitizeScalar(
        JsonTraversalValue value,
        int depth,
        HashSet<object> activeReferences,
        StructuredLogSanitizer sanitizer)
    {
        var scalar = value.ReadScalar();
        return scalar.Kind switch
        {
            JsonScalarKind.SanitizableValue => sanitizer.SanitizeNestedValue(
                scalar.Value,
                depth,
                activeReferences),
            JsonScalarKind.DirectJsonValue => scalar.Value is string text
                ? sanitizer.SanitizeFreeText(text)
                : scalar.Value,
            JsonScalarKind.RawNumber => scalar.Value,
            _ => StructuredLogSanitizer.SanitizationFailed
        };
    }

    private readonly struct JsonPropertyValue
    {
        private readonly JsonProperty elementProperty;
        private readonly KeyValuePair<string, JsonNode?> nodeProperty;
        private readonly JsonTraversalSource source;

        internal JsonPropertyValue(JsonProperty property)
        {
            elementProperty = property;
            nodeProperty = default;
            source = JsonTraversalSource.Element;
        }

        internal JsonPropertyValue(KeyValuePair<string, JsonNode?> property)
        {
            elementProperty = default;
            nodeProperty = property;
            source = JsonTraversalSource.Node;
        }

        internal string Name => source == JsonTraversalSource.Element
            ? elementProperty.Name
            : nodeProperty.Key;

        internal object? Value => source == JsonTraversalSource.Element
            ? elementProperty.Value
            : nodeProperty.Value;
    }

    private readonly record struct JsonScalarValue(JsonScalarKind Kind, object? Value)
    {
        internal static JsonScalarValue Sanitizable(object? value) =>
            new(JsonScalarKind.SanitizableValue, value);

        internal static JsonScalarValue DirectJsonValue(object? value) =>
            new(JsonScalarKind.DirectJsonValue, value);

        internal static JsonScalarValue RawNumber(string value) =>
            new(JsonScalarKind.RawNumber, value);

        internal static JsonScalarValue Unsupported() =>
            new(JsonScalarKind.Unsupported, null);
    }

    private readonly struct JsonTraversalValue
    {
        private readonly JsonElement element;
        private readonly JsonNode? node;
        private readonly JsonTraversalSource source;

        private JsonTraversalValue(JsonElement element)
        {
            this.element = element;
            node = null;
            source = JsonTraversalSource.Element;
        }

        private JsonTraversalValue(JsonNode node)
        {
            element = default;
            this.node = node;
            source = JsonTraversalSource.Node;
        }

        internal JsonTraversalKind Kind => source switch
        {
            JsonTraversalSource.Element => element.ValueKind switch
            {
                JsonValueKind.Object => JsonTraversalKind.Object,
                JsonValueKind.Array => JsonTraversalKind.Array,
                _ => JsonTraversalKind.Scalar
            },
            JsonTraversalSource.Node => node switch
            {
                JsonObject => JsonTraversalKind.Object,
                JsonArray => JsonTraversalKind.Array,
                JsonValue => JsonTraversalKind.Scalar,
                _ => JsonTraversalKind.Unsupported
            },
            _ => JsonTraversalKind.Unsupported
        };

        internal static bool TryCreate(object value, out JsonTraversalValue jsonValue)
        {
            switch (value)
            {
                case JsonDocument document:
                    jsonValue = new JsonTraversalValue(document.RootElement);
                    return true;
                case JsonElement element:
                    jsonValue = new JsonTraversalValue(element);
                    return true;
                case JsonNode node:
                    jsonValue = new JsonTraversalValue(node);
                    return true;
                default:
                    jsonValue = default;
                    return false;
            }
        }

        internal IEnumerable<JsonPropertyValue> EnumerateObject()
        {
            if (source == JsonTraversalSource.Element)
            {
                foreach (var property in element.EnumerateObject())
                {
                    // Keep the JsonProperty wrapper intact. Its child Value is not read until the
                    // shared budget loop has accepted this property.
                    yield return new JsonPropertyValue(property);
                }

                yield break;
            }

            foreach (var property in (JsonObject)node!)
            {
                yield return new JsonPropertyValue(property);
            }
        }

        internal IEnumerable<object?> EnumerateArray()
        {
            if (source == JsonTraversalSource.Element)
            {
                foreach (var item in element.EnumerateArray())
                {
                    yield return item;
                }

                yield break;
            }

            foreach (var item in (JsonArray)node!)
            {
                yield return item;
            }
        }

        internal object? SanitizeArrayItem(
            object? item,
            int depth,
            HashSet<object> activeReferences,
            StructuredLogSanitizer sanitizer)
        {
            if (source == JsonTraversalSource.Element && item is JsonElement elementItem)
            {
                // JsonElement arrays historically recurse within the JSON path instead of
                // reapplying the caller-configured sensitive type dispatch to each boxed item.
                return Sanitize(
                    new JsonTraversalValue(elementItem),
                    depth,
                    activeReferences,
                    sanitizer);
            }

            return sanitizer.SanitizeNestedValue(item, depth, activeReferences);
        }

        internal JsonScalarValue ReadScalar()
        {
            if (source == JsonTraversalSource.Element)
            {
                return ReadElementScalar(element);
            }

            if (node is not JsonValue jsonValue ||
                !jsonValue.TryGetValue<object>(out var value) ||
                !StructuredLogSanitizer.IsSupportedJsonValueScalar(value))
            {
                return JsonScalarValue.Unsupported();
            }

            if (value is JsonElement nestedElement)
            {
                return nestedElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? JsonScalarValue.Unsupported()
                    : ReadElementScalar(nestedElement);
            }

            return JsonScalarValue.Sanitizable(value);
        }

        private static JsonScalarValue ReadElementScalar(JsonElement element) =>
            element.ValueKind switch
            {
                JsonValueKind.String => JsonScalarValue.DirectJsonValue(element.GetString()),
                JsonValueKind.Number when element.TryGetDecimal(out var number) =>
                    JsonScalarValue.DirectJsonValue(number),
                JsonValueKind.Number => JsonScalarValue.RawNumber(element.GetRawText()),
                JsonValueKind.True => JsonScalarValue.DirectJsonValue(true),
                JsonValueKind.False => JsonScalarValue.DirectJsonValue(false),
                JsonValueKind.Null => JsonScalarValue.DirectJsonValue(null),
                _ => JsonScalarValue.Unsupported()
            };
    }

    private enum JsonTraversalSource
    {
        None,
        Element,
        Node
    }

    private enum JsonTraversalKind
    {
        Unsupported,
        Object,
        Array,
        Scalar
    }

    private enum JsonScalarKind
    {
        Unsupported,
        SanitizableValue,
        DirectJsonValue,
        RawNumber
    }
}
