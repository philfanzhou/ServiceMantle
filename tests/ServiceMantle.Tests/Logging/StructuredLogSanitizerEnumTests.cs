using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.Tests.Logging;

public sealed class StructuredLogSanitizerEnumTests
{
    public static TheoryData<Enum, long> EnumValues => new()
    {
        { SampleEnum.Value, 7L },
        { ByteEnum.Maximum, 255L },
        { LongEnum.Minimum, long.MinValue },
        { ULongEnum.LongMaximum, long.MaxValue },
        { ULongEnum.AboveLongMaximum, long.MinValue },
        { ULongEnum.Maximum, -1L },
        { FlagEnum.Read | FlagEnum.Write, 3L },
    };

    [Theory]
    [MemberData(nameof(EnumValues))]
    public void Enum_underlying_values_and_dictionary_keys_use_the_normalized_long(
        Enum value,
        long expected)
    {
        var sanitizer = new StructuredLogSanitizer();

        var output = sanitizer.Sanitize(value);
        var dictionary = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            sanitizer.Sanitize(new Dictionary<object, string>
            {
                [value] = "safe",
            }));

        Assert.Equal(expected, Assert.IsType<long>(output));
        Assert.Equal("safe", dictionary[expected.ToString(CultureInfo.InvariantCulture)]);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("field")]
    [InlineData("header")]
    [InlineData("member")]
    [InlineData("collection")]
    [InlineData("dictionary-value")]
    [InlineData("dictionary-key")]
    [InlineData("json")]
    [InlineData("nullable")]
    public void Enum_is_normalized_at_the_shared_scalar_exit_on_every_supported_path(string path)
    {
        var sanitizer = new StructuredLogSanitizer();
        SampleEnum? nullable = SampleEnum.Value;

        var output = path switch
        {
            "direct" => sanitizer.Sanitize(SampleEnum.Value),
            "field" => sanitizer.SanitizeFields([new("Kind", SampleEnum.Value)])["Kind"],
            "header" => sanitizer.SanitizeHeaders([new("X-Kind", SampleEnum.Value)])["X-Kind"],
            "member" => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                sanitizer.Sanitize(new EnumContainer(SampleEnum.Value)))["Kind"],
            "collection" => Assert.IsAssignableFrom<IReadOnlyList<object?>>(
                sanitizer.Sanitize(new[] { SampleEnum.Value }))[0],
            "dictionary-value" => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                sanitizer.Sanitize(new Dictionary<string, SampleEnum>
                {
                    ["Kind"] = SampleEnum.Value,
                }))["Kind"],
            "dictionary-key" => long.Parse(
                Assert.Single(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                    sanitizer.Sanitize(new Dictionary<SampleEnum, string>
                    {
                        [SampleEnum.Value] = "safe",
                    }))).Key,
                CultureInfo.InvariantCulture),
            "json" => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                sanitizer.Sanitize(new JsonObject
                {
                    ["Kind"] = JsonValue.Create(SampleEnum.Value),
                }))["Kind"],
            "nullable" => sanitizer.Sanitize(nullable),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };

        Assert.Equal(7L, Assert.IsType<long>(output));
    }

    [Fact]
    public void Json_string_enum_converter_cannot_restore_a_sanitized_member_name()
    {
        var sanitizer = new StructuredLogSanitizer();
        var sinkOptions = new JsonSerializerOptions();
        sinkOptions.Converters.Add(new JsonStringEnumConverter());

        var output = sanitizer.Sanitize(new EnumContainer(SampleEnum.Value));

        Assert.Equal("{\"Kind\":7}", JsonSerializer.Serialize(output, sinkOptions));
    }

    private enum SampleEnum
    {
        Value = 7,
    }

    private enum ByteEnum : byte
    {
        Maximum = byte.MaxValue,
    }

    private enum LongEnum : long
    {
        Minimum = long.MinValue,
    }

    private enum ULongEnum : ulong
    {
        LongMaximum = long.MaxValue,
        AboveLongMaximum = (ulong)long.MaxValue + 1,
        Maximum = ulong.MaxValue,
    }

    [Flags]
    private enum FlagEnum : byte
    {
        Read = 1,
        Write = 2,
    }

    private sealed record EnumContainer(SampleEnum Kind);
}
