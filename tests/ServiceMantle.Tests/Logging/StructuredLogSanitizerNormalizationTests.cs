using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.Tests.Logging;

public sealed class StructuredLogSanitizerNormalizationTests
{
    [Fact]
    public void Field_policy_preserves_normalization_classification_order_and_collisions()
    {
        var maximumLengthName = new string('A', 128);
        var oversizedName = new string('B', 129);
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            AllowUnlistedFields = false,
            AllowedFieldNames =
            [
                "allowed.field",
                "trace.id/part[0]@source:name",
                maximumLengthName,
                "duplicate"
            ],
            DeniedFieldNames = ["custom-secret"]
        });

        var output = sanitizer.SanitizeFields(
        [
            new(" Allowed.Field ", "safe"),
            new("Trace.Id/Part[0]@Source:Name", 7),
            new("custom_secret", "custom-secret-value"),
            new("ACCESS-TOKEN", "built-in-secret-value"),
            new("unlisted", "removed"),
            new("bad name", "removed"),
            new(maximumLengthName, 128),
            new(oversizedName, "removed"),
            new("Duplicate", "first"),
            new(" Duplicate ", "second")
        ]);

        Assert.Equal(
            [
                "Allowed.Field",
                "Trace.Id/Part[0]@Source:Name",
                "custom_secret",
                "ACCESS-TOKEN",
                maximumLengthName,
                "Duplicate"
            ],
            output.Keys);
        Assert.Equal("safe", output["Allowed.Field"]);
        Assert.Equal(7, output["Trace.Id/Part[0]@Source:Name"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["custom_secret"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["ACCESS-TOKEN"]);
        Assert.Equal(128, output[maximumLengthName]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["Duplicate"]);
    }

    [Fact]
    public void Header_policy_preserves_normalization_classification_values_order_and_collisions()
    {
        var maximumLengthName = new string('H', 128);
        var oversizedName = new string('J', 129);
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            AllowUnlistedHeaders = false,
            AllowedHeaderNames =
            [
                "X-Allowed",
                "X-Multi",
                "X-Duplicate",
                "Authorization",
                maximumLengthName
            ],
            DeniedHeaderNames = ["X-Secret"]
        });

        var output = sanitizer.SanitizeHeaders(
        [
            new(" x-allowed ", "safe"),
            new("X-SECRET", "custom-secret-value"),
            new("Authorization", "built-in-secret-value"),
            new("X-Multi", new[] { "one", "two" }),
            new("X-Unlisted", "removed"),
            new("Bad Header", "removed"),
            new(maximumLengthName, 128),
            new(oversizedName, "removed"),
            new("X-Duplicate", "first"),
            new(" x-duplicate ", "second")
        ]);

        Assert.Equal(
            ["x-allowed", "X-SECRET", "Authorization", "X-Multi", maximumLengthName, "X-Duplicate"],
            output.Keys);
        Assert.Equal("safe", output["X-Allowed"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["x-secret"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["authorization"]);
        Assert.Equal(["one", "two"], Assert.IsAssignableFrom<IReadOnlyList<object?>>(output["x-multi"]));
        Assert.Equal(128, output[maximumLengthName]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output["x-duplicate"]);
    }

    [Fact]
    public void Name_policy_snapshots_mutable_option_collections()
    {
        var allowedFields = new List<string> { "InitialField" };
        var deniedFields = new List<string> { "InitialSecret" };
        var allowedHeaders = new List<string> { "X-Initial" };
        var deniedHeaders = new List<string> { "X-Initial-Secret" };
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            AllowUnlistedFields = false,
            AllowedFieldNames = allowedFields,
            DeniedFieldNames = deniedFields,
            AllowUnlistedHeaders = false,
            AllowedHeaderNames = allowedHeaders,
            DeniedHeaderNames = deniedHeaders
        });

        allowedFields.Add("LaterField");
        deniedFields.Add("LaterValue");
        allowedHeaders.Add("X-Later");
        deniedHeaders.Add("X-Later-Secret");

        var fields = sanitizer.SanitizeFields(
        [
            new("InitialField", 1),
            new("Initial_Secret", "redacted"),
            new("LaterField", "removed"),
            new("LaterValue", "removed")
        ]);
        var headers = sanitizer.SanitizeHeaders(
        [
            new("X-Initial", 1),
            new("X-Initial-Secret", "redacted"),
            new("X-Later", "removed"),
            new("X-Later-Secret", "removed")
        ]);

        Assert.Equal(["InitialField", "Initial_Secret"], fields.Keys);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, fields["Initial_Secret"]);
        Assert.Equal(["X-Initial", "X-Initial-Secret"], headers.Keys);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, headers["X-Initial-Secret"]);
    }

    [Fact]
    public void Free_text_facade_preserves_null_recognition_length_and_failure_behavior()
    {
        var observedMaximumLength = 0;
        var sanitizer = new StructuredLogSanitizer(new StructuredLogSanitizerOptions
        {
            MaximumStringLength = 8
        });
        var failingSanitizer = CreateWithFreeTextSanitizer(
            new StructuredLogSanitizerOptions { MaximumStringLength = 8 },
            (_, maximumLength) =>
            {
                observedMaximumLength = maximumLength;
                throw new InvalidOperationException();
            });

        Assert.Null(sanitizer.SanitizeFreeText(null));
        Assert.Equal(StructuredLogSanitizer.RedactedValue, sanitizer.SanitizeFreeText("token=x"));
        Assert.Equal("ordinary", sanitizer.SanitizeFreeText("ordinary"));
        Assert.Equal(StructuredLogSanitizer.OversizedValue, sanitizer.SanitizeFreeText("123456789"));
        Assert.Equal(
            StructuredLogSanitizer.SanitizationFailed,
            failingSanitizer.SanitizeFreeText("ordinary"));
        Assert.Equal(8, observedMaximumLength);
    }

    [Fact]
    public void Scalar_exit_preserves_types_and_replaces_non_finite_values_on_all_supported_paths()
    {
        var sanitizer = new StructuredLogSanitizer();
        double? nullable = double.NegativeInfinity;
        var member = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            sanitizer.Sanitize(new { Value = double.PositiveInfinity }));
        var collection = Assert.IsAssignableFrom<IReadOnlyList<object?>>(
            sanitizer.Sanitize(new object?[] { float.NaN }));
        var json = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            sanitizer.Sanitize(new JsonObject
            {
                ["Value"] = JsonValue.Create(double.NaN)
            }));

        Assert.Equal(1.25d, Assert.IsType<double>(sanitizer.Sanitize(1.25d)));
        Assert.Equal(2.5f, Assert.IsType<float>(sanitizer.Sanitize(2.5f)));
        Assert.Equal(
            StructuredLogSanitizer.UnrepresentableValue,
            sanitizer.Sanitize(double.NaN));
        Assert.Equal(StructuredLogSanitizer.UnrepresentableValue, member["Value"]);
        Assert.Equal(StructuredLogSanitizer.UnrepresentableValue, collection[0]);
        Assert.Equal(StructuredLogSanitizer.UnrepresentableValue, json["Value"]);
        Assert.Equal(StructuredLogSanitizer.UnrepresentableValue, sanitizer.Sanitize(nullable));
    }

    [Fact]
    public void JsonElement_primitive_conversion_remains_independent_from_the_clr_scalar_exit()
    {
        using var document = JsonDocument.Parse("""{"Number":1.25,"Boolean":true}""");
        var sanitizer = new StructuredLogSanitizer();

        var output = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            sanitizer.Sanitize(document.RootElement));

        Assert.Equal(1.25m, Assert.IsType<decimal>(output["Number"]));
        Assert.True(Assert.IsType<bool>(output["Boolean"]));
        Assert.Equal("{\"Number\":1.25,\"Boolean\":true}", JsonSerializer.Serialize(output));
    }

    private static StructuredLogSanitizer CreateWithFreeTextSanitizer(
        StructuredLogSanitizerOptions options,
        Func<string, int, string> freeTextSanitizer)
    {
        var constructor = typeof(StructuredLogSanitizer).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(StructuredLogSanitizerOptions), typeof(Func<string, int, string>)],
            modifiers: null);

        return Assert.IsType<StructuredLogSanitizer>(constructor!.Invoke(
        [
            options,
            freeTextSanitizer
        ]));
    }
}
