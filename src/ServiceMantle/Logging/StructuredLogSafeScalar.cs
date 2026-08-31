using System.Globalization;

namespace ServiceMantle.Logging;

/// <summary>
/// Owns recognition and output normalization for sink-safe CLR scalar values.
/// </summary>
internal static class StructuredLogSafeScalar
{
    internal static bool IsSupported(Type type) =>
        type.IsEnum ||
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
        type == typeof(TimeSpan) ||
        type == typeof(DateTimeOffset);

    /// <summary>
    /// The single point where a safe scalar becomes output. Values that no sink can represent
    /// are replaced so the sanitized graph always stays serializable.
    /// </summary>
    internal static object Normalize(object value) => value switch
    {
        Enum enumeration => NormalizeEnum(enumeration),
        double number when !double.IsFinite(number) => StructuredLogSanitizer.UnrepresentableValue,
        float number when !float.IsFinite(number) => StructuredLogSanitizer.UnrepresentableValue,
        _ => value,
    };

    internal static long NormalizeEnum(Enum value) =>
        Enum.GetUnderlyingType(value.GetType()) == typeof(ulong)
            ? unchecked((long)Convert.ToUInt64(value, CultureInfo.InvariantCulture))
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
