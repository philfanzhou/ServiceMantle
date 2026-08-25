using System.Collections;
using System.Text.Json;
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
}
