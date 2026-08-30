namespace ServiceMantle.Http;

internal static class ServiceMantleProblemValue
{
    internal const int MaximumErrorCodeLength = 128;
    internal const int MaximumExtensionNameLength = 128;
    internal const int MaximumTitleLength = 256;

    private static readonly HashSet<string> ProtectedFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "type",
        "title",
        "status",
        "detail",
        "instance",
        ServiceMantleProblemDetailsDefaults.CorrelationIdExtensionName,
        ServiceMantleProblemDetailsDefaults.ErrorCodeExtensionName,
    };

    internal static string ValidateErrorCode(string errorCode, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(errorCode, parameterName);

        if (errorCode.Length is < 1 or > MaximumErrorCodeLength ||
            !IsLowerAsciiLetterOrDigit(errorCode[0]))
        {
            throw new ArgumentException(
                $"A Problem Details error code must contain between 1 and {MaximumErrorCodeLength} " +
                "characters and begin with a lower-case ASCII letter or digit.",
                parameterName);
        }

        for (var index = 1; index < errorCode.Length; index++)
        {
            var character = errorCode[index];
            if (!IsLowerAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw new ArgumentException(
                    "A Problem Details error code may contain only lower-case ASCII letters, " +
                    "digits, dot, underscore, or dash.",
                    parameterName);
            }
        }

        return errorCode;
    }

    internal static string ValidateTitle(string title, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(title, parameterName);

        if (title.Length is < 1 or > MaximumTitleLength || title.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A Problem Details title must contain between 1 and {MaximumTitleLength} " +
                "characters without control characters.",
                parameterName);
        }

        return title;
    }

    internal static string ValidateExtensionName(string name, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(name, parameterName);

        if (name.Length is < 1 or > MaximumExtensionNameLength || !IsAsciiLetter(name[0]))
        {
            throw new ArgumentException(
                $"A Problem Details extension name must contain between 1 and " +
                $"{MaximumExtensionNameLength} characters and begin with an ASCII letter.",
                parameterName);
        }

        for (var index = 1; index < name.Length; index++)
        {
            var character = name[index];
            if (!IsAsciiLetter(character) &&
                character is not (>= '0' and <= '9') &&
                character is not ('_' or '.' or '-'))
            {
                throw new ArgumentException(
                    "A Problem Details extension name contains an invalid character.",
                    parameterName);
            }
        }

        if (ProtectedFieldNames.Contains(name))
        {
            throw new ArgumentException(
                "A protected Problem Details field cannot be registered as an extension.",
                parameterName);
        }

        return name;
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsLowerAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
