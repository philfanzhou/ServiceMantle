using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.Tests.Logging;

public sealed class LogFreeTextSanitizerTests
{
    [Theory]
    [InlineData("password=secret-value")]
    [InlineData("Host=db;Database=app;User Id=admin")]
    [InlineData("postgresql://admin:secret@database/app")]
    [InlineData("https://user:password@example.test/path")]
    public void Recognized_assignment_connection_string_and_credential_uri_replace_the_entire_text(
        string value)
    {
        var sanitizer = new StructuredLogSanitizer();

        Assert.Equal(StructuredLogSanitizer.RedactedValue, sanitizer.SanitizeFreeText(value));
    }

    [Fact]
    public void Bearer_JWT_and_private_key_shapes_are_redacted_in_otherwise_safe_text()
    {
        const string bearer = "Bearer abcdefghijklmnopqrstuvwxyz";
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.abcdefghijklmno.pqrstuvwxyzABCDE";
        const string privateKey = "-----BEGIN PRIVATE KEY-----abc-----END PRIVATE KEY-----";
        var input = $"auth {bearer}; jwt {jwt}; key {privateKey}";
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.SanitizeFreeText(input)!;

        Assert.DoesNotContain(bearer, output, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, output, StringComparison.Ordinal);
        Assert.DoesNotContain(privateKey, output, StringComparison.Ordinal);
        Assert.Contains("Bearer [REDACTED]", output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_TOKEN]", output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_KEY]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Control_and_line_separator_characters_are_replaced_to_prevent_log_injection()
    {
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.SanitizeFreeText("first\r\nsecond\u2028third");

        Assert.Equal("first  second third", output);
    }

    [Fact]
    public void Oversized_free_text_is_replaced_in_full()
    {
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            MaximumStringLength = 8
        });

        Assert.Equal(
            StructuredLogSanitizer.OversizedValue,
            sanitizer.SanitizeFreeText("123456789"));
    }

    [Fact]
    public void Unlabelled_opaque_values_are_outside_the_detection_guarantee()
    {
        const string opaqueValue = "uR3m7Qp9L2x5";
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.SanitizeFreeText($"operation id {opaqueValue}");

        // This behavior intentionally fixes the documented boundary: arbitrary opaque text cannot
        // be distinguished from a legitimate identifier. Callers must use a denied structured
        // field/Header, ISensitiveLogValue, or a registered sensitive type for guaranteed removal.
        Assert.Equal($"operation id {opaqueValue}", output);
    }
}
