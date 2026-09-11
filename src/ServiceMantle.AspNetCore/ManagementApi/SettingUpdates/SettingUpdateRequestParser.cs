using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using ServiceMantle.Audit;
using ServiceMantle.Configuration;

namespace ServiceMantle.AspNetCore.ManagementApi.SettingUpdates;

internal static class SettingUpdateRequestParser
{
    internal const int MaximumBodyLength = 256 * 1024;
    internal const int MaximumJsonDepth = 8;
    internal const int MaximumChanges = 32;
    internal const int MaximumKeyLength = 128;

    internal static async ValueTask<ServiceSettingUpdateCommand?> ParseAsync(
        HttpContext context,
        ManagementAuditOperator operatorInfo)
    {
        if (context.Request.QueryString.HasValue
            || context.Request.Headers.ContainsKey(HeaderNames.ContentEncoding)
            || !IsJsonContentType(context.Request.ContentType)
            || context.Request.ContentLength is > MaximumBodyLength)
        {
            return null;
        }

        var body = new ArrayBufferWriter<byte>();
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var remaining = MaximumBodyLength - body.WrittenCount;
                var read = await context.Request.Body
                    .ReadAsync(rented.AsMemory(0, Math.Min(rented.Length, remaining + 1)), context.RequestAborted)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (read > remaining)
                {
                    return null;
                }

                body.Write(rented.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }

        try
        {
            using var document = JsonDocument.Parse(
                body.WrittenMemory,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth
                });
            return Parse(document.RootElement, operatorInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ServiceSettingUpdateCommand? Parse(
        JsonElement root,
        ManagementAuditOperator operatorInfo)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement expectedVersion = default;
        JsonElement changes = default;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return null;
            }

            switch (property.Name)
            {
                case "expectedVersion":
                    expectedVersion = property.Value;
                    break;
                case "changes":
                    changes = property.Value;
                    break;
                default:
                    return null;
            }
        }

        if (seen.Count != 2
            || !TryNonNegativeInt64(expectedVersion, out var version)
            || changes.ValueKind != JsonValueKind.Array
            || changes.GetArrayLength() is < 1 or > MaximumChanges)
        {
            return null;
        }

        var parsed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in changes.EnumerateArray())
        {
            if (!TryChange(item, out var key, out var value) || !parsed.TryAdd(key!, value))
            {
                return null;
            }
        }

        try
        {
            return new ServiceSettingUpdateCommand(version, parsed, operatorInfo);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryChange(JsonElement item, out string? key, out string? value)
    {
        key = null;
        value = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonElement rawKey = default;
        JsonElement rawValue = default;
        foreach (var property in item.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "key":
                    rawKey = property.Value;
                    break;
                case "value":
                    rawValue = property.Value;
                    break;
                default:
                    return false;
            }
        }

        if (seen.Count != 2
            || rawKey.ValueKind != JsonValueKind.String
            || rawValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            return false;
        }

        var originalKey = rawKey.GetString();
        if (originalKey is null || originalKey.Length is < 1 or > MaximumKeyLength)
        {
            return false;
        }

        key = originalKey.Trim();
        if (key.Length == 0)
        {
            return false;
        }

        value = rawValue.ValueKind == JsonValueKind.Null ? null : rawValue.GetString();
        return true;
    }

    private static bool TryNonNegativeInt64(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var text = element.GetRawText();
        if (text.Length == 0 || text.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        return element.TryGetInt64(out value) && value >= 0;
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)
            || !MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            || !string.Equals(parsed.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parsed.Parameters.Count == 0)
        {
            return true;
        }

        return parsed.Parameters.Count == 1
            && string.Equals(
                parsed.Parameters[0].Name.Value,
                "charset",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                HeaderUtilities.RemoveQuotes(parsed.Parameters[0].Value).Value,
                "utf-8",
                StringComparison.OrdinalIgnoreCase);
    }
}
