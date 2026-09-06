using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Projects the existing safe setting query service onto the two fixed read-only responses.
/// </summary>
/// <remarks>
/// Only the closed projections the core query service already produces are read, and every field is
/// written explicitly. No definition, value, snapshot, or root-key object is handed to a serializer,
/// and neither response carries a default value, a constraint, or an internal error.
/// </remarks>
internal static class ServiceMantleSettingQueryHandlers
{
    /// <summary>Answers the definition catalog without refreshing the snapshot.</summary>
    internal static Task<IResult> Definitions(HttpContext context)
    {
        if (!ServiceMantleSettingQueryMapping.TryParseGroup(context, out var group))
        {
            return Task.FromResult(ServiceMantleManagementApiResults.InvalidRequest());
        }

        var definitions = Service(context).GetDefinitions();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("definitions");
            foreach (var definition in definitions)
            {
                if (!Matches(definition.Key, group))
                {
                    continue;
                }

                writer.WriteStartObject();
                WriteDefinition(
                    writer,
                    definition.Key,
                    definition.ValueType,
                    definition.IsRequired,
                    definition.IsSensitive,
                    definition.HasDefault,
                    definition.RequiresRestart);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Task.FromResult<IResult>(ServiceMantleSettingQueryResult.Json(buffer.WrittenSpan.ToArray()));
    }

    /// <summary>
    /// Refreshes exactly once and answers the complete successful version, or one fixed rejection.
    /// </summary>
    internal static async Task<IResult> CurrentValuesAsync(HttpContext context)
    {
        if (!ServiceMantleSettingQueryMapping.TryParseGroup(context, out var group))
        {
            return ServiceMantleManagementApiResults.InvalidRequest();
        }

        // The caller's cancellation stays the caller's: it is neither converted into the closed
        // rejection nor into an internal error.
        var result = await Service(context).GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // The group filter shapes output only; it can never hide a failure of the complete
            // refresh, and no version, key, partial or previous value is answered.
            return ServiceMantleSettingQueryResult.Unavailable;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", result.Version!.Value);
            writer.WriteStartArray("values");
            foreach (var value in result.Values)
            {
                if (!Matches(value.Key, group))
                {
                    continue;
                }

                writer.WriteStartObject();
                WriteDefinition(
                    writer,
                    value.Key,
                    value.ValueType,
                    value.IsRequired,
                    value.IsSensitive,
                    value.HasDefault,
                    value.RequiresRestart);
                writer.WriteBoolean("hasValue", value.HasValue);
                writer.WriteString("source", ToWireValue(value.Source));
                // The core projection already replaced a sensitive value with null; the null field
                // itself is always written so the shape does not depend on the value.
                if (value.Value is null)
                {
                    writer.WriteNull("value");
                }
                else
                {
                    writer.WriteString("value", value.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return ServiceMantleSettingQueryResult.Json(buffer.WrittenSpan.ToArray());
    }

    private static ServiceSettingQueryService Service(HttpContext context) =>
        context.RequestServices.GetRequiredService<ServiceSettingQueryService>();

    private static bool Matches(string key, string? group) =>
        group is null ||
        string.Equals(key, group, StringComparison.Ordinal) ||
        (key.Length > group.Length &&
            key.StartsWith(group, StringComparison.Ordinal) &&
            key[group.Length] == '.');

    private static void WriteDefinition(
        Utf8JsonWriter writer,
        string key,
        ServiceSettingValueType valueType,
        bool isRequired,
        bool isSensitive,
        bool hasDefault,
        bool requiresRestart)
    {
        writer.WriteString("key", key);
        writer.WriteString("valueType", ToWireValue(valueType));
        writer.WriteBoolean("isRequired", isRequired);
        writer.WriteBoolean("isSensitive", isSensitive);
        writer.WriteBoolean("hasDefault", hasDefault);
        writer.WriteBoolean("requiresRestart", requiresRestart);
    }

    private static string ToWireValue(ServiceSettingValueType valueType) => valueType switch
    {
        ServiceSettingValueType.String => "string",
        ServiceSettingValueType.Number => "number",
        ServiceSettingValueType.Boolean => "boolean",
        ServiceSettingValueType.Json => "json",
        _ => throw new InvalidOperationException("The service setting value type is unknown."),
    };

    private static string ToWireValue(ServiceSettingValueSource source) => source switch
    {
        ServiceSettingValueSource.Missing => "missing",
        ServiceSettingValueSource.Default => "default",
        ServiceSettingValueSource.Persisted => "persisted",
        _ => throw new InvalidOperationException("The service setting value source is unknown."),
    };
}
