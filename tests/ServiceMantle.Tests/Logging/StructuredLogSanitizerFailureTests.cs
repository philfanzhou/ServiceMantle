using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.Tests.Logging;

public sealed class StructuredLogSanitizerFailureTests
{
    [Fact]
    public void Circular_reference_is_replaced_and_original_object_is_never_returned()
    {
        var node = new Node { Name = "root" };
        node.Next = node;
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.Sanitize(node);
        var serialized = JsonSerializer.Serialize(output);

        Assert.NotSame(node, output);
        Assert.Contains("root", serialized, StringComparison.Ordinal);
        Assert.Contains(StructuredLogSanitizer.CircularReference, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Throwing_property_is_replaced_and_denied_property_is_never_read()
    {
        var input = new ThrowingProperties();
        var sanitizer = new StructuredLogSanitizer();

        var output = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(sanitizer.Sanitize(input));

        Assert.Equal(StructuredLogSanitizer.SanitizationFailed, output["PublicValue"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["Password"]);
        Assert.Equal(0, input.PasswordGetterCount);
    }

    [Fact]
    public void Throwing_enumerator_discards_partial_output_and_returns_only_failure_marker()
    {
        const string secret = "enumerated-secret";
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.Sanitize(new ThrowingEnumerable(secret));
        var serialized = JsonSerializer.Serialize(output);

        Assert.Equal(StructuredLogSanitizer.SanitizationFailed, output);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Throwing_root_field_enumeration_returns_only_failure_dictionary()
    {
        const string secret = "field-enumeration-secret";
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.SanitizeFields(new ThrowingFields(secret));
        var serialized = JsonSerializer.Serialize(output);

        var item = Assert.Single(output);
        Assert.Equal("SanitizationFailure", item.Key);
        Assert.Equal(StructuredLogSanitizer.SanitizationFailed, item.Value);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_messages_stack_traces_and_data_are_never_emitted()
    {
        const string secret = "exception-message-secret";
        var exception = new InvalidOperationException(
            secret,
            new ArgumentException("inner-secret"));
        exception.Data["token"] = "data-secret";
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.Sanitize(exception);
        var serialized = JsonSerializer.Serialize(output);

        Assert.Contains(typeof(InvalidOperationException).FullName!, serialized, StringComparison.Ordinal);
        Assert.Contains(typeof(ArgumentException).FullName!, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("inner-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("data-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Depth_and_collection_limits_emit_stable_markers()
    {
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            MaximumDepth = 1,
            MaximumCollectionCount = 2
        });
        var root = new Node
        {
            Name = "root",
            Next = new Node
            {
                Name = "child",
                Next = new Node { Name = "grandchild" }
            }
        };

        var objectOutput = JsonSerializer.Serialize(sanitizer.Sanitize(root));
        var collectionOutput = Assert.IsAssignableFrom<IReadOnlyList<object?>>(
            sanitizer.Sanitize(new[] { 1, 2, 3, 4 }));

        Assert.Contains(StructuredLogSanitizer.MaximumDepthExceeded, objectOutput, StringComparison.Ordinal);
        Assert.Equal([1, 2, StructuredLogSanitizer.CollectionTruncated], collectionOutput);
    }

    [Fact]
    public void Root_field_and_header_collections_emit_truncation_markers()
    {
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            MaximumCollectionCount = 1
        });
        KeyValuePair<string, object?>[] entries =
        [
            new("First", 1),
            new("Second", 2),
            new("Third", 3)
        ];

        var fields = sanitizer.SanitizeFields(entries);
        var headers = sanitizer.SanitizeHeaders(entries);

        Assert.Equal(2, fields.Count);
        Assert.Equal(1, fields["First"]);
        Assert.Equal(StructuredLogSanitizer.CollectionTruncated, fields["CollectionTruncated"]);
        Assert.Equal(2, headers.Count);
        Assert.Equal(1, headers["First"]);
        Assert.Equal(StructuredLogSanitizer.CollectionTruncated, headers["CollectionTruncated"]);
    }

    [Fact]
    public void Root_field_and_header_collections_stop_enumerating_lazy_infinite_sources()
    {
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            MaximumCollectionCount = 2
        });
        var fieldSource = new GuardedInfiniteEntries();
        var headerSource = new GuardedInfiniteEntries();

        var fields = sanitizer.SanitizeFields(fieldSource);
        var headers = sanitizer.SanitizeHeaders(headerSource);

        Assert.Equal(3, fieldSource.MoveNextCount);
        Assert.Equal(StructuredLogSanitizer.CollectionTruncated, fields["CollectionTruncated"]);
        Assert.Equal(3, headerSource.MoveNextCount);
        Assert.Equal(StructuredLogSanitizer.CollectionTruncated, headers["CollectionTruncated"]);
    }

    [Fact]
    public void Deep_JsonNode_uses_the_configured_depth_limit_instead_of_serializer_defaults()
    {
        const int levels = 80;
        JsonNode input = JsonValue.Create("leaf")!;
        for (var level = 0; level < levels; level++)
        {
            input = new JsonObject { ["Child"] = input };
        }

        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            MaximumDepth = 128
        });

        object? output = sanitizer.Sanitize(input);

        for (var level = 0; level < levels; level++)
        {
            var dictionary = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(output);
            output = dictionary["Child"];
        }

        Assert.Equal("leaf", output);
    }

    [Fact]
    public void Wide_JsonNode_stops_before_serializing_values_beyond_the_collection_limit()
    {
        var converter = new ThrowingJsonPayloadConverter();
        var serializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        serializerOptions.Converters.Add(converter);
        var typeInfo = (JsonTypeInfo<ThrowingJsonPayload>)serializerOptions.GetTypeInfo(
            typeof(ThrowingJsonPayload));
        var input = new JsonArray { 1, 2 };
        for (var index = 0; index < 10_000; index++)
        {
            input.Add(index);
        }

        input.Add(JsonValue.Create(new ThrowingJsonPayload(), typeInfo));
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            MaximumCollectionCount = 2
        });

        var output = Assert.IsAssignableFrom<IReadOnlyList<object?>>(sanitizer.Sanitize(input));

        Assert.Equal([1m, 2m, StructuredLogSanitizer.CollectionTruncated], output);
        Assert.Equal(0, converter.WriteCount);
    }

    [Fact]
    public void Custom_JsonValue_fails_closed_without_reading_object_properties()
    {
        var payload = new GetterTrackingJsonPayload();
        var serializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        var typeInfo = (JsonTypeInfo<GetterTrackingJsonPayload>)serializerOptions.GetTypeInfo(
            typeof(GetterTrackingJsonPayload));
        var input = JsonValue.Create(payload, typeInfo);
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            MaximumCollectionCount = 2
        });

        var output = sanitizer.Sanitize(input);

        Assert.Equal(StructuredLogSanitizer.SanitizationFailed, output);
        Assert.Equal(0, payload.ValuesGetterCount);
    }

    [Fact]
    public void Custom_JsonValue_fails_closed_without_invoking_its_converter()
    {
        var converter = new ThrowingJsonPayloadConverter();
        var serializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        serializerOptions.Converters.Add(converter);
        var typeInfo = (JsonTypeInfo<ThrowingJsonPayload>)serializerOptions.GetTypeInfo(
            typeof(ThrowingJsonPayload));
        var input = JsonValue.Create(new ThrowingJsonPayload(), typeInfo);
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.Sanitize(input);

        Assert.Equal(StructuredLogSanitizer.SanitizationFailed, output);
        Assert.Equal(0, converter.WriteCount);
    }

    [Fact]
    public void Invalid_options_fail_closed_without_exposing_option_contents()
    {
        const string secret = "option-enumeration-secret";

        var exception = Assert.Throws<ArgumentException>(() =>
            new StructuredLogSanitizer(new StructuredLogSanitizerOptions
            {
                DeniedFieldNames = new ThrowingNames(secret)
            }));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.Throws<ArgumentException>(() =>
            new StructuredLogSanitizer(new StructuredLogSanitizerOptions
            {
                MaximumDepth = 0
            }));
    }

    [Fact]
    public void One_sanitizer_supports_concurrent_independent_calls()
    {
        var sanitizer = new StructuredLogSanitizer();

        Parallel.For(0, 500, index =>
        {
            var secret = $"secret-{index}";
            var output = sanitizer.SanitizeFields(
            [
                new("Sequence", index),
                new("Token", secret),
                new("Nested", new { Sequence = index, Password = secret })
            ]);
            var serialized = JsonSerializer.Serialize(output);

            Assert.Equal(index, output["Sequence"]);
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        });
    }

    private sealed class Node
    {
        public string? Name { get; init; }

        public Node? Next { get; set; }
    }

    private sealed class ThrowingProperties
    {
        public int PasswordGetterCount { get; private set; }

        public string Password
        {
            get
            {
                PasswordGetterCount++;
                return "password-that-must-not-be-read";
            }
        }

        public string PublicValue => throw new InvalidOperationException("getter-secret");
    }

    private sealed class ThrowingEnumerable(string secret) : IEnumerable<object?>
    {
        public IEnumerator<object?> GetEnumerator()
        {
            yield return new { Token = secret };
            throw new InvalidOperationException(secret);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingFields(string secret)
        : IEnumerable<KeyValuePair<string, object?>>
    {
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return new KeyValuePair<string, object?>("Token", secret);
            throw new InvalidOperationException(secret);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingNames(string secret) : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator()
        {
            yield return "safe-name";
            throw new InvalidOperationException(secret);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GuardedInfiniteEntries
        : IEnumerable<KeyValuePair<string, object?>>
    {
        public int MoveNextCount { get; private set; }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var index = 0; ; index++)
            {
                MoveNextCount++;
                if (MoveNextCount > 3)
                {
                    throw new InvalidOperationException("Collection limit was not enforced.");
                }

                yield return new KeyValuePair<string, object?>($"Entry-{index}", index);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingJsonPayload;

    private sealed class GetterTrackingJsonPayload
    {
        public int ValuesGetterCount { get; private set; }

        public IReadOnlyList<int> Values
        {
            get
            {
                ValuesGetterCount++;
                return Enumerable.Range(0, 10_000).ToArray();
            }
        }
    }

    private sealed class ThrowingJsonPayloadConverter : JsonConverter<ThrowingJsonPayload>
    {
        public int WriteCount { get; private set; }

        public override ThrowingJsonPayload Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            ThrowingJsonPayload value,
            JsonSerializerOptions options)
        {
            WriteCount++;
            throw new InvalidOperationException("Value beyond the traversal limit was serialized.");
        }
    }
}
