using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;

namespace ServiceMantle.AspNetCore.ManagementApi.SettingQueries;

/// <summary>
/// Projects the existing safe setting query service onto the two fixed read-only responses.
/// </summary>
/// <remarks>
/// Only the closed projections the core query service already produces are read, and every field is
/// written explicitly. No definition, value, snapshot, or root-key object is handed to a serializer,
/// and neither response carries a default value, a constraint, or an internal error.
/// </remarks>
internal static class SettingQueryHandlers
{
    /// <summary>Answers the definition catalog without refreshing the snapshot.</summary>
    internal static Task<IResult> Definitions(HttpContext context)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        try
        {
            var parsed = SettingQueryMapping.TryParseGroup(context, out var group);

            // Cancellation observed after the bounded query read settles outranks its classification
            // and precedes the scoped resolution: an accepted or rejected group never delivers the
            // 400 or the catalog once the caller has aborted, and the query service is never
            // resolved. The catalog never refreshes the snapshot.
            context.RequestAborted.ThrowIfCancellationRequested();
            if (!parsed)
            {
                return Task.FromResult(ManagementApiResults.InvalidRequest());
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

            context.RequestAborted.ThrowIfCancellationRequested();
            return Task.FromResult<IResult>(SettingQueryResult.Json(buffer.WrittenSpan.ToArray()));
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            // A query dependency - the bounded query read or the scoped resolution - that settles
            // after the request aborted, whether it answered or cancelled internally, does not
            // deliver its ordinary response, and its own exception is never propagated either.
            throw CancelledByCaller(context.RequestAborted);
        }
    }

    /// <summary>
    /// Refreshes exactly once and answers the complete successful version, or one fixed rejection.
    /// </summary>
    internal static async Task<IResult> CurrentValuesAsync(HttpContext context)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        try
        {
            var parsed = SettingQueryMapping.TryParseGroup(context, out var group);

            // Cancellation observed after the bounded query read settles outranks its classification
            // and precedes the single refresh: an accepted or rejected group never delivers the 400,
            // and the snapshot is never refreshed, once the caller has aborted.
            context.RequestAborted.ThrowIfCancellationRequested();
            if (!parsed)
            {
                return ManagementApiResults.InvalidRequest();
            }

            // The caller's cancellation stays the caller's: it is neither converted into the closed
            // rejection nor into an internal error.
            var result = await Service(context).GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);

            // Cancellation observed after the single refresh settles outranks its projection: a
            // successful or failed refresh never delivers the 200 or the fixed 503 once the caller
            // has aborted.
            context.RequestAborted.ThrowIfCancellationRequested();
            if (!result.Succeeded)
            {
                // The group filter shapes output only; it can never hide a failure of the complete
                // refresh, and no version, key, partial or previous value is answered.
                return SettingQueryResult.Unavailable;
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

            context.RequestAborted.ThrowIfCancellationRequested();
            return SettingQueryResult.Json(buffer.WrittenSpan.ToArray());
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            // A query dependency - the bounded query read, the scoped resolution, or the single
            // snapshot refresh - that settles after the request aborted, whether it answered, failed,
            // or cancelled internally, does not deliver its ordinary response, and its own exception
            // is never propagated either.
            throw CancelledByCaller(context.RequestAborted);
        }
    }

    /// <summary>
    /// Builds the caller's own cancellation result. It carries the request token and nothing else:
    /// no dependency exception, no snapshot error code, and no consumer or setting text.
    /// </summary>
    private static OperationCanceledException CancelledByCaller(CancellationToken cancellationToken) =>
        new("The management setting query request was cancelled by the caller.", cancellationToken);

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
