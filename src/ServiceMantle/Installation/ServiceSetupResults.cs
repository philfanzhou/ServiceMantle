namespace ServiceMantle.Installation;

/// <summary>Well-known safe error codes emitted by setup orchestration.</summary>
public static class WellKnownServiceSetupErrorCodes
{
    /// <summary>A contributor changed the staging scope during read-only validation.</summary>
    public const string ValidationSideEffect = "setup.validation_side_effect";

    /// <summary>A contributor threw, returned an invalid result, or cancelled internally.</summary>
    public const string ContributorFailed = "setup.contributor_failed";

    /// <summary>The caller-provided staging scope could not discard pending changes.</summary>
    public const string CleanupFailed = "setup.cleanup_failed";
}

/// <summary>The closed, value-free result returned by a setup contributor.</summary>
public sealed class ServiceSetupContributorResult
{
    private static readonly ServiceSetupContributorResult SuccessResult = new(errorCode: null);

    private ServiceSetupContributorResult(string? errorCode)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets whether the contributor accepted the operation.</summary>
    public bool Succeeded => ErrorCode is null;

    /// <summary>Gets the stable safe error code when the contributor rejected the operation.</summary>
    public string? ErrorCode { get; }

    /// <summary>Creates a successful result.</summary>
    public static ServiceSetupContributorResult Success() => SuccessResult;

    /// <summary>Creates a rejected result carrying only a safe error code.</summary>
    public static ServiceSetupContributorResult Rejected(string errorCode) =>
        new(ServiceSetupErrorCode.EnsureValid(errorCode, nameof(errorCode)));

    /// <summary>Returns a value-free safe projection.</summary>
    public override string ToString() =>
        $"ServiceSetupContributorResult(Succeeded={Succeeded}, ErrorCode={ErrorCode})";
}

/// <summary>The closed, value-free result returned by setup orchestration.</summary>
public sealed class ServiceSetupResult
{
    private static readonly ServiceSetupResult SuccessResult = new(errorCode: null);

    private ServiceSetupResult(string? errorCode)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets whether every contributor was validated and registered.</summary>
    public bool Succeeded => ErrorCode is null;

    /// <summary>Gets the stable safe error code when orchestration failed.</summary>
    public string? ErrorCode { get; }

    internal static ServiceSetupResult Success() => SuccessResult;

    internal static ServiceSetupResult Failure(string errorCode) => new(errorCode);

    /// <summary>Returns a value-free safe projection.</summary>
    public override string ToString() =>
        $"ServiceSetupResult(Succeeded={Succeeded}, ErrorCode={ErrorCode})";
}

internal static class ServiceSetupErrorCode
{
    private const int MaximumLength = 128;

    internal static string EnsureValid(string errorCode, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(errorCode, parameterName);

        if (errorCode.Length is < 1 or > MaximumLength || !IsAsciiLetterOrDigit(errorCode[0]))
        {
            throw new ArgumentException(
                "A setup rejection code must contain between 1 and 128 ASCII letters, digits, '.', '_', or '-'.",
                parameterName);
        }

        foreach (var character in errorCode)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw new ArgumentException(
                    "A setup rejection code must contain between 1 and 128 ASCII letters, digits, '.', '_', or '-'.",
                    parameterName);
            }
        }

        return errorCode;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
