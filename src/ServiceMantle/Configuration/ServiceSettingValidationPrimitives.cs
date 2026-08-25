namespace ServiceMantle.Configuration;

internal static class ServiceSettingValidationPrimitives
{
    public static string NormalizeKey(string key)
    {
        if (key is null)
        {
            throw new ServiceSettingDefinitionException(null, "setting.definition.invalid_key");
        }

        var normalized = key.Trim().ToLowerInvariant();
        if (normalized.Length is < 1 or > 128 || !IsAsciiLetterOrDigit(normalized[0]))
        {
            throw new ServiceSettingDefinitionException(null, "setting.definition.invalid_key");
        }

        for (var index = 1; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw new ServiceSettingDefinitionException(null, "setting.definition.invalid_key");
            }
        }

        return normalized;
    }

    public static void ValidateErrorCode(string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode) ||
            errorCode.Length > 128 ||
            !IsAsciiLetterOrDigit(errorCode[0]))
        {
            throw new ArgumentException("The validation error code has an invalid format.", nameof(errorCode));
        }

        for (var index = 1; index < errorCode.Length; index++)
        {
            var character = errorCode[index];
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw new ArgumentException("The validation error code has an invalid format.", nameof(errorCode));
            }
        }
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
