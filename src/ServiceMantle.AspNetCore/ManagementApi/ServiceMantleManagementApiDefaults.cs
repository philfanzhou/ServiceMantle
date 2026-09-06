namespace ServiceMantle.Management;

/// <summary>
/// Defines the fixed root, version segment, error codes, and titles of the protected ServiceMantle
/// management API v1 surface.
/// </summary>
public static class ServiceMantleManagementApiDefaults
{
    /// <summary>
    /// The default fully versioned management root.
    /// </summary>
    public const string DefaultRootPath = "/management/v1";

    /// <summary>
    /// The fixed final segment every configured management root must end with.
    /// </summary>
    /// <remarks>
    /// The version is part of the path only. The surface performs no query, header, or content
    /// negotiation and routes exactly one version.
    /// </remarks>
    public const string VersionSegment = "v1";

    /// <summary>
    /// The stable error code returned by the fixed invalid-request result.
    /// </summary>
    public const string InvalidRequestErrorCode = "management.request.invalid";

    /// <summary>
    /// The fixed, input-free title returned by the invalid-request result.
    /// </summary>
    public const string InvalidRequestTitle = "The request is invalid.";

    /// <summary>
    /// The stable error code returned by the fixed conflict result.
    /// </summary>
    public const string ConflictErrorCode = "management.request.conflict";

    /// <summary>
    /// The fixed, input-free title returned by the conflict result.
    /// </summary>
    public const string ConflictTitle = "The request conflicts with the current state.";
}
