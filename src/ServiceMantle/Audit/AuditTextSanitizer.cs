using System.Globalization;
using System.Text;

namespace ServiceMantle.Audit;

/// <summary>
/// Cleans free-text audit field values: trims surrounding whitespace, strips control characters
/// (log-injection defense), and enforces a maximum length.
/// </summary>
internal static class AuditTextSanitizer
{
    internal static string? Clean(string? value, int maxLength, string errorCode, string fieldDescription)
    {
        if (value is null)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Control)
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(character);
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        if (cleaned.Length > maxLength)
        {
            throw new ManagementAuditException(
                errorCode,
                $"The audit {fieldDescription} exceeds the maximum allowed length of {maxLength} characters.");
        }

        return cleaned;
    }

    internal static string CleanRequired(string value, int maxLength, string errorCode, string fieldDescription)
    {
        ArgumentNullException.ThrowIfNull(value);

        var cleaned = Clean(value, maxLength, errorCode, fieldDescription);
        if (cleaned is null)
        {
            throw new ManagementAuditException(
                errorCode,
                $"The audit {fieldDescription} must not be empty.");
        }

        return cleaned;
    }
}
