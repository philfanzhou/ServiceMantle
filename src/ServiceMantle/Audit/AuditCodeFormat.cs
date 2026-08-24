namespace ServiceMantle.Audit;

/// <summary>
/// Shared normalization rules for bounded audit code values (action, target type, operator source).
/// Codes are lowercase ASCII, must start with a letter or digit, and may contain '.', '_', '-', or ':'
/// as namespace separators so consuming services can extend their own codes safely.
/// </summary>
internal static class AuditCodeFormat
{
    internal static bool TryNormalize(string value, int maxLength, out string normalizedValue)
    {
        normalizedValue = string.Empty;
        var candidate = value.Trim().ToLowerInvariant();

        if (candidate.Length is < 1 || candidate.Length > maxLength || !IsAsciiLetterOrDigit(candidate[0]))
        {
            return false;
        }

        for (var index = 1; index < candidate.Length; index++)
        {
            var character = candidate[index];
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':'))
            {
                return false;
            }
        }

        normalizedValue = candidate;
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
