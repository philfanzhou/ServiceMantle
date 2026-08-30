namespace ServiceMantle.Http;

/// <summary>
/// Defines the stable fields and fail-closed response used by ServiceMantle Problem Details.
/// </summary>
public static class ServiceMantleProblemDetailsDefaults
{
    /// <summary>
    /// The stable type-URI prefix for ServiceMantle error codes.
    /// </summary>
    public const string TypeUriPrefix = "urn:servicemantle:error:";

    /// <summary>
    /// The extension field containing the request Correlation ID.
    /// </summary>
    public const string CorrelationIdExtensionName = "correlationId";

    /// <summary>
    /// The extension field containing the stable error code.
    /// </summary>
    public const string ErrorCodeExtensionName = "errorCode";

    /// <summary>
    /// The error code returned for every exception without an exact registered mapping.
    /// </summary>
    public const string InternalServerErrorCode = "http.internal_server_error";

    /// <summary>
    /// The fixed title returned for every exception without an exact registered mapping.
    /// </summary>
    public const string InternalServerErrorTitle = "An unexpected error occurred.";

    /// <summary>
    /// The stable type URI returned for every exception without an exact registered mapping.
    /// </summary>
    public const string InternalServerErrorType =
        TypeUriPrefix + InternalServerErrorCode;

    /// <summary>
    /// Creates the stable ServiceMantle type URI for a validated error code.
    /// </summary>
    /// <param name="errorCode">A stable lower-case ASCII error code.</param>
    /// <returns>The corresponding ServiceMantle type URI.</returns>
    /// <exception cref="ArgumentException">The error code is not valid.</exception>
    public static string CreateTypeUri(string errorCode) =>
        TypeUriPrefix + ServiceMantleProblemValue.ValidateErrorCode(errorCode, nameof(errorCode));
}
