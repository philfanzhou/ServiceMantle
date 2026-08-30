using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ServiceMantle.Bootstrap;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.Tests.Logging;

public sealed class StructuredLogSanitizerTests
{
    [Fact]
    public void Sanitize_recursively_redacts_nested_fields_objects_collections_and_json()
    {
        const string password = "nested-password";
        const string apiKey = "nested-api-key";
        const string token = "nested-json-token";
        using var json = JsonDocument.Parse($$"""
            {
              "safe": 3,
              "nested": {
                "token": "{{token}}"
              }
            }
            """);
        var input = new
        {
            Name = "catalog",
            Password = password,
            Items = new object?[]
            {
                42,
                new Dictionary<string, object?>
                {
                    ["Api_Key"] = apiKey,
                    ["Json"] = json.RootElement
                }
            }
        };
        var sanitizer = new StructuredLogSanitizer();

        var sanitized = sanitizer.Sanitize(input);
        var serialized = JsonSerializer.Serialize(sanitized);

        Assert.NotSame(input, sanitized);
        Assert.Contains("catalog", serialized, StringComparison.Ordinal);
        Assert.Contains("42", serialized, StringComparison.Ordinal);
        Assert.Contains("\"safe\":3", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(password, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
        Assert.Contains(StructuredLogSanitizer.RedactedValue, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_allowlist_removes_unlisted_fields_and_deny_rules_take_precedence()
    {
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            AllowUnlistedFields = false,
            AllowedFieldNames = ["message", "access_token"],
            DeniedFieldNames = ["internal-value"]
        });

        var output = sanitizer.SanitizeFields(
        [
            new("message", "safe"),
            new("count", 3),
            new("access_token", "secret-one"),
            new("internal_value", "secret-two")
        ]);

        Assert.Equal("safe", output["message"]);
        Assert.False(output.ContainsKey("count"));
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["access_token"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["internal_value"]);
    }

    [Fact]
    public void Header_rules_redact_defaults_and_registered_names_case_insensitively()
    {
        const string authorization = "Bearer authorization-secret";
        const string cookie = "session=cookie-secret";
        const string customSecret = "custom-header-secret";
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            DeniedHeaderNames = ["X-Custom-Secret"],
            AllowUnlistedHeaders = true
        });

        var output = sanitizer.SanitizeHeaders(
        [
            new("Authorization", authorization),
            new("COOKIE", cookie),
            new("x-custom-secret", new[] { customSecret }),
            new("Accept-Language", "en-US")
        ]);
        var serialized = JsonSerializer.Serialize(output);

        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["Authorization"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["COOKIE"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["x-custom-secret"]);
        Assert.Equal("en-US", output["Accept-Language"]);
        Assert.DoesNotContain(authorization, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(cookie, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(customSecret, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_allowlist_removes_unlisted_headers_but_cannot_override_denies()
    {
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            AllowUnlistedHeaders = false,
            AllowedHeaderNames = ["Content-Type", "Authorization"]
        });

        var output = sanitizer.SanitizeHeaders(
        [
            new("Content-Type", "application/json"),
            new("Accept", "application/json"),
            new("Authorization", "Bearer secret")
        ]);

        Assert.Equal("application/json", output["Content-Type"]);
        Assert.False(output.ContainsKey("Accept"));
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["Authorization"]);
    }

    [Fact]
    public void Known_secret_types_binary_values_and_consumer_markers_are_replaced_without_inspection()
    {
        const string masterKey = "bootstrap-master-key";
        const string connectionString = "Host=db;Password=database-secret";
        const string markedSecret = "marked-secret";
        var bootstrap = new BootstrapConfiguration(
            ServiceId.Parse("catalog"),
            new BootstrapDatabaseConfiguration("postgresql", "16", connectionString),
            masterKey);
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.SanitizeFields(
        [
            new("Bootstrap", bootstrap),
            new("Authentication", new AuthenticationHeaderValue("Bearer", "header-secret")),
            new("Network", new NetworkCredential("user", "credential-secret")),
            new("Marked", new MarkedSecret(markedSecret)),
            new("Bytes", new byte[] { 1, 2, 3 })
        ]);
        var serialized = JsonSerializer.Serialize(output);

        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["Bootstrap"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["Authentication"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["Network"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["Marked"]);
        Assert.Equal(StructuredLogSanitizer.BinaryValue, output["Bytes"]);
        Assert.DoesNotContain(masterKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(markedSecret, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Immutable_and_multidimensional_byte_buffers_are_replaced_without_enumeration()
    {
        var sanitizer = new StructuredLogSanitizer();
        var immutableBytes = ImmutableArray.Create<byte>(1, 2, 3);
        var multidimensionalBytes = new byte[,] { { 4, 5 }, { 6, 7 } };

        var output = sanitizer.SanitizeFields(
        [
            new("ImmutableBytes", immutableBytes),
            new("MultidimensionalBytes", multidimensionalBytes)
        ]);

        Assert.Equal(StructuredLogSanitizer.BinaryValue, output["ImmutableBytes"]);
        Assert.Equal(StructuredLogSanitizer.BinaryValue, output["MultidimensionalBytes"]);
    }

    [Fact]
    public void Consumer_registered_sensitive_type_is_replaced_without_getter_access()
    {
        var sensitive = new RegisteredSecret("registered-secret");
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            SensitiveTypes = [typeof(RegisteredSecret)]
        });

        var output = sanitizer.Sanitize(sensitive);

        Assert.Equal(StructuredLogSanitizer.RedactedValue, output);
        Assert.Equal(0, sensitive.GetterCount);
    }

    [Fact]
    public void Non_string_scalars_are_preserved_without_string_conversion()
    {
        var timestamp = new DateTime(2026, 8, 25, 1, 2, 3, DateTimeKind.Utc);
        var id = Guid.Parse("8e79b1ca-3516-4988-9756-b1d8f4de37df");
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.SanitizeFields(
        [
            new("Count", 12),
            new("Ratio", 1.25m),
            new("Enabled", true),
            new("Timestamp", timestamp),
            new("Id", id)
        ]);

        Assert.Equal(12, output["Count"]);
        Assert.Equal(1.25m, output["Ratio"]);
        Assert.Equal(true, output["Enabled"]);
        Assert.Equal(timestamp, output["Timestamp"]);
        Assert.Equal(id, output["Id"]);
    }

    [Fact]
    public void Invalid_field_and_header_names_are_removed_without_echoing_them()
    {
        var sanitizer = new StructuredLogSanitizer();

        var fields = sanitizer.SanitizeFields(
        [
            new("safe", "value"),
            new("tоken", "confusable-secret") // The second character is Cyrillic.
        ]);
        var headers = sanitizer.SanitizeHeaders(
        [
            new("Good-Header", "value"),
            new("Bad Header", "secret")
        ]);

        Assert.Single(fields);
        Assert.Equal("value", fields["safe"]);
        Assert.Single(headers);
        Assert.Equal("value", headers["Good-Header"]);
    }

    [Fact]
    public void Duplicate_header_names_fail_closed_to_a_redaction_marker()
    {
        var sanitizer = new StructuredLogSanitizer();

        var headers = sanitizer.SanitizeHeaders(
        [
            new("X-Request-Id", "first"),
            new("x-request-id", "second")
        ]);

        Assert.Single(headers);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, headers["X-Request-Id"]);
    }

    [Fact]
    public void Generic_read_only_dictionary_keeps_dictionary_field_semantics()
    {
        var input = new GenericReadOnlyDictionary(
        [
            new("Name", "catalog"),
            new("AccessToken", "sensitive-token"),
        ]);
        var sanitizer = new StructuredLogSanitizer();

        Assert.False((object)input is System.Collections.IDictionary);
        var output = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            sanitizer.Sanitize(input));

        Assert.Equal("catalog", output["Name"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["AccessToken"]);
    }

    private sealed class MarkedSecret(string value) : ISensitiveLogValue
    {
        public string Value { get; } = value;
    }

    private sealed class RegisteredSecret(string value)
    {
        public int GetterCount { get; private set; }

        public string Value
        {
            get
            {
                GetterCount++;
                return value;
            }
        }
    }

    private sealed class GenericReadOnlyDictionary(
        IEnumerable<KeyValuePair<string, object?>> entries)
        : IReadOnlyDictionary<string, object?>
    {
        private readonly Dictionary<string, object?> values = new(entries);

        public object? this[string key] => values[key];

        public IEnumerable<string> Keys => values.Keys;

        public IEnumerable<object?> Values => values.Values;

        public int Count => values.Count;

        public bool ContainsKey(string key) => values.ContainsKey(key);

        public bool TryGetValue(string key, out object? value) => values.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
