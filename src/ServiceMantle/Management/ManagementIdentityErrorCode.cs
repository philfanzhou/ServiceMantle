namespace ServiceMantle.Management;

/// <summary>
/// Enforces the safe error-code shape shared by every management identity result.
/// </summary>
internal static class ManagementIdentityErrorCode
{
    internal const int MaximumLength = 64;

    internal static string EnsureValid(string errorCode, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(errorCode, parameterName);

        if (!IsValid(errorCode))
        {
            throw new ArgumentException(
                $"A management identity error code must contain between 1 and {MaximumLength} ASCII letters, digits, '.', '_', or '-'.",
                parameterName);
        }

        return errorCode;
    }

    private static bool IsValid(string errorCode)
    {
        if (errorCode.Length is < 1 or > MaximumLength)
        {
            return false;
        }

        foreach (var character in errorCode)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
