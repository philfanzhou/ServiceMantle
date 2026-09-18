using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using ServiceMantle.Installation;

namespace ServiceMantle.AspNetCore.ManagementApi.Setup;

/// <summary>
/// Parses the one property the Setup completion entry accepts.
/// </summary>
/// <remarks>
/// Every entry owns its own strict parser, so this one states the shared admission rules again
/// rather than sharing a mutable helper with another endpoint's limits. Nothing it reads is echoed:
/// a rejection is a bare <c>false</c> and the candidate never reaches a message, a log, or an
/// exception.
/// </remarks>
internal static class SetupRequestParser
{
    /// <summary>The raw request body limit, counted in bytes before any decoding.</summary>
    internal const int MaximumBodyLength = 4 * 1024;

    /// <summary>The JSON nesting limit. The accepted document needs one object and one string.</summary>
    internal const int MaximumJsonDepth = 4;

    /// <summary>The only accepted top-level property name, matched case sensitively.</summary>
    internal const string CodePropertyName = "code";

    /// <summary>The raw request body limit of the input mode, counted in bytes before any decoding.</summary>
    internal const int MaximumInputBodyLength = 16 * 1024;

    /// <summary>The JSON nesting limit of the input mode. The accepted document needs two objects.</summary>
    internal const int MaximumInputJsonDepth = 8;

    /// <summary>The second accepted top-level property name of the input mode, matched case sensitively.</summary>
    internal const string InputPropertyName = "input";

    /// <summary>
    /// Parses the input mode body <c>{"code":...,"input":{...}}</c>. On success the returned input
    /// owns one pooled buffer and one document; on every other exit the buffer is zeroed and
    /// returned here.
    /// </summary>
    internal static async ValueTask<(SetupCode Code, SetupInput Input)?> ParseWithInputAsync(
        HttpContext context,
        ArrayPool<byte>? pool = null)
    {
        var request = context.Request;
        if (request.QueryString.HasValue ||
            request.Headers.ContainsKey(HeaderNames.ContentEncoding) ||
            !IsJsonContentType(request.ContentType) ||
            request.ContentLength is > MaximumInputBodyLength)
        {
            return null;
        }

        // The length is never trusted from the header alone: one extra byte is read so an undeclared
        // oversized body is caught by the actual byte count.
        var rented = (pool ?? ArrayPool<byte>.Shared).Rent(MaximumInputBodyLength + 1);
        var transferred = false;
        try
        {
            var length = 0;
            while (true)
            {
                var read = await request.Body
                    .ReadAsync(
                        rented.AsMemory(length, MaximumInputBodyLength + 1 - length),
                        context.RequestAborted)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (length > MaximumInputBodyLength)
                {
                    return null;
                }
            }

            return ParseInput(rented, length, pool, out transferred);
        }
        finally
        {
            if (!transferred)
            {
                ReturnCleared(pool, rented);
            }
        }
    }

    private static (SetupCode Code, SetupInput Input)? ParseInput(
        byte[] rented,
        int length,
        ArrayPool<byte>? pool,
        out bool transferred)
    {
        transferred = false;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                rented.AsMemory(0, length),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumInputJsonDepth,
                });
        }
        catch (JsonException)
        {
            return null;
        }

        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement code = default;
            var sawCode = false;
            var sawInput = false;
            foreach (var property in root.EnumerateObject())
            {
                // The names are matched exactly: "Code", "Input", a repeated property, an extra
                // property, and a non-object input value are all rejected here.
                if (!sawCode && string.Equals(property.Name, CodePropertyName, StringComparison.Ordinal))
                {
                    sawCode = true;
                    code = property.Value;
                }
                else if (!sawInput &&
                    string.Equals(property.Name, InputPropertyName, StringComparison.Ordinal) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    sawInput = true;
                }
                else
                {
                    return null;
                }
            }

            if (!sawCode || !sawInput ||
                code.ValueKind != JsonValueKind.String ||
                !SetupCode.TryParse(code.GetString(), out var parsed))
            {
                return null;
            }

            transferred = true;
            return (parsed!, new SetupInput(pool ?? ArrayPool<byte>.Shared, rented, document));
        }
        finally
        {
            if (!transferred)
            {
                // Every rejected shape releases the parsed document here; its pooled bytes are
                // returned by the caller's finally.
                document.Dispose();
            }
        }
    }

    private static void ReturnCleared(ArrayPool<byte>? pool, byte[] rented)
    {
        // The buffer held the raw request bytes; it is zeroed before it returns and the pool clear
        // is kept as a second fence.
        Array.Clear(rented);
        (pool ?? ArrayPool<byte>.Shared).Return(rented, clearArray: true);
    }

    internal static async ValueTask<SetupCode?> ParseAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.QueryString.HasValue ||
            request.Headers.ContainsKey(HeaderNames.ContentEncoding) ||
            !IsJsonContentType(request.ContentType) ||
            request.ContentLength is > MaximumBodyLength)
        {
            return null;
        }

        var body = new ArrayBufferWriter<byte>();
        var rented = ArrayPool<byte>.Shared.Rent(MaximumBodyLength);
        try
        {
            while (true)
            {
                var remaining = MaximumBodyLength - body.WrittenCount;
                var read = await request.Body
                    .ReadAsync(
                        rented.AsMemory(0, Math.Min(rented.Length, remaining + 1)),
                        context.RequestAborted)
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

            return Parse(body.WrittenMemory);
        }
        finally
        {
            // The rented buffer held the candidate Setup Code; it is cleared before it returns.
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static SetupCode? Parse(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement code = default;
            var properties = 0;
            foreach (var property in root.EnumerateObject())
            {
                properties++;
                // The name is matched exactly: "Code" and a repeated "code" are both rejected.
                if (properties > 1 || !string.Equals(property.Name, CodePropertyName, StringComparison.Ordinal))
                {
                    return null;
                }

                code = property.Value;
            }

            // The value is never trimmed or normalized; SetupCode owns the exact 32-character,
            // case-sensitive Base64URL shape.
            return properties == 1 && code.ValueKind == JsonValueKind.String &&
                SetupCode.TryParse(code.GetString(), out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            !MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            !string.Equals(parsed.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parsed.Parameters.Count == 0)
        {
            return true;
        }

        return parsed.Parameters.Count == 1 &&
            string.Equals(parsed.Parameters[0].Name.Value, "charset", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                HeaderUtilities.RemoveQuotes(parsed.Parameters[0].Value).Value,
                "utf-8",
                StringComparison.OrdinalIgnoreCase);
    }
}
