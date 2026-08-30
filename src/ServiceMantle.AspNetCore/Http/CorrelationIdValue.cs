using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace ServiceMantle.Http;

/// <summary>
/// Applies the fixed Correlation ID acceptance and generation rules.
/// </summary>
internal static class CorrelationIdValue
{
    internal const int MaximumLength = 64;

    /// <summary>
    /// Resolves the single Correlation ID for one request by reading the request headers exactly once.
    /// </summary>
    /// <remarks>
    /// A caller value is reused only when the request carries exactly one header value that already
    /// satisfies the accepted shape. Missing, empty, whitespace, overlong, illegal, comma-joined, and
    /// repeated headers are discarded as a whole; the value is never trimmed, normalized, truncated,
    /// escaped, or partially selected.
    /// </remarks>
    internal static string Resolve(IHeaderDictionary headers)
    {
        var headerValues = headers[ServiceMantleHeaderNames.CorrelationId];
        return headerValues.Count == 1 && IsAccepted(headerValues[0])
            ? headerValues[0]!
            : Generate();
    }

    /// <summary>
    /// Generates a new Correlation ID as 32 lowercase hexadecimal characters without separators.
    /// </summary>
    internal static string Generate() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Determines whether a caller supplied value may be reused verbatim.
    /// </summary>
    internal static bool IsAccepted(string? value)
    {
        if (value is null ||
            value.Length is < 1 or > MaximumLength ||
            !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
