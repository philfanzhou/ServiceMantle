namespace ServiceMantle.Bootstrap;

internal static class DatabaseProviderId
{
    public static string Normalize(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (!TryNormalize(value, out var normalized))
        {
            throw new ArgumentException(
                "The provider id must be 1 to 64 characters, start with an ASCII letter or digit, " +
                "and contain only ASCII letters, digits, dot, dash, or underscore.",
                parameterName);
        }

        return normalized;
    }

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length is 0 or > 64 || !IsAsciiLetterOrDigit(candidate[0]))
        {
            return false;
        }

        for (var i = 1; i < candidate.Length; i++)
        {
            var character = candidate[i];
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        normalized = candidate;
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
