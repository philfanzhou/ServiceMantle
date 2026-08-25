using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ServiceMantle.Logging;

internal static partial class LogFreeTextSanitizer
{
    private const string SensitiveKeyExpression =
        @"pass(?:wd|[\s_-]*(?:word|phrase))?|pwd|secret|token|api[\s_-]*key|" +
        @"connection[\s_-]*(?:string|str)|conn[\s_-]*str|credential|private[\s_-]*key|" +
        @"root[\s_-]*key|master[\s_-]*key|setup[\s_-]*code|client[\s_-]*secret|" +
        @"access[\s_-]*key|account[\s_-]*key|authorization|cookie";

    internal static string Sanitize(string value, int maximumLength)
    {
        if (value.Length > maximumLength)
        {
            return StructuredLogSanitizer.OversizedValue;
        }

        var cleaned = RemoveLogInjectionCharacters(value);

        // A recognized secret assignment, credential-bearing URI, or connection string can contain
        // arbitrary punctuation. Replace the complete field instead of guessing its value boundary.
        if (SensitiveAssignmentPattern().IsMatch(cleaned) ||
            DatabaseUriPattern().IsMatch(cleaned) ||
            ConnectionStringPattern().IsMatch(cleaned))
        {
            return StructuredLogSanitizer.RedactedValue;
        }

        var redacted = BearerTokenPattern().Replace(cleaned, "Bearer [REDACTED]");
        redacted = JwtLikePattern().Replace(redacted, "[REDACTED_TOKEN]");
        redacted = PemBlockPattern().Replace(redacted, "[REDACTED_KEY]");
        return redacted;
    }

    private static string RemoveLogInjectionCharacters(string value)
    {
        StringBuilder? builder = null;

        for (var index = 0; index < value.Length;)
        {
            var status = Rune.DecodeFromUtf16(
                value.AsSpan(index),
                out var rune,
                out var charactersConsumed);
            var category = status == OperationStatus.Done
                ? Rune.GetUnicodeCategory(rune)
                : UnicodeCategory.Surrogate;
            if (status != OperationStatus.Done ||
                category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                builder ??= new StringBuilder(value);
                charactersConsumed = Math.Max(charactersConsumed, 1);
                for (var offset = 0; offset < charactersConsumed; offset++)
                {
                    builder[index + offset] = ' ';
                }
            }

            index += Math.Max(charactersConsumed, 1);
        }

        return builder?.ToString() ?? value;
    }

    [GeneratedRegex(
        @"(?:" + SensitiveKeyExpression + @")[""']?\s*(?::|=)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentPattern();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:postgres(?:ql)?|mysql|mariadb|sqlserver):\/\/[^\s,;]+|" +
        @"(?<![A-Za-z0-9])(?:[A-Za-z][A-Za-z0-9+.-]*):\/\/[^\s\/?#@]+@[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseUriPattern();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:host|server|data[\s_-]*source|address|database|" +
        @"initial[\s_-]*catalog|user[\s_-]*id|uid)\s*=\s*[^;\r\n]+" +
        @"(?:;\s*[A-Za-z][A-Za-z0-9 _-]*\s*=\s*[^;\r\n]*)*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPattern();

    [GeneratedRegex(
        @"Bearer\s+[A-Za-z0-9\-_.~+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtLikePattern();

    [GeneratedRegex(
        @"-----BEGIN[^-]*PRIVATE KEY-----[\s\S]*?-----END[^-]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PemBlockPattern();
}
