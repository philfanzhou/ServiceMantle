namespace ServiceMantle.Bootstrap;

internal static class DatabaseTargetPreparationErrorCode
{
    private const string RequiredPrefix = "database_target_preparation.";
    private const int MaximumLength = 64;

    public static string Validate(string? errorCode, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode, parameterName);

        if (errorCode.Length > MaximumLength ||
            !errorCode.StartsWith(RequiredPrefix, StringComparison.Ordinal) ||
            errorCode.Length == RequiredPrefix.Length)
        {
            throw new ArgumentException(
                $"The error code must use the '{RequiredPrefix}' namespace and be at most {MaximumLength} characters.",
                parameterName);
        }

        foreach (var character in errorCode)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                throw new ArgumentException(
                    "The error code contains an invalid character.",
                    parameterName);
            }
        }

        if (!IsWellKnown(errorCode))
        {
            throw new ArgumentException(
                "The error code is not a registered database target preparation error code.",
                parameterName);
        }

        return errorCode;
    }

    private static bool IsWellKnown(string errorCode) =>
        errorCode is
            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported or
            WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch or
            WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget or
            WellKnownDatabaseTargetPreparationErrorCodes.ServerUnreachable or
            WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed or
            WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied or
            WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict or
            WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed or
            WellKnownDatabaseTargetPreparationErrorCodes.Timeout or
            WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed;

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
