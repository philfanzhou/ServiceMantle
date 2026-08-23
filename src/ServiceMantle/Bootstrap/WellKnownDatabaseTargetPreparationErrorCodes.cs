namespace ServiceMantle.Bootstrap;

/// <summary>
/// Well-known safe error codes for database target preparation operations.
/// </summary>
public static class WellKnownDatabaseTargetPreparationErrorCodes
{
    /// <summary>
    /// No target preparation provider is registered for the requested database provider.
    /// Callers must fail closed rather than treat this as an already-prepared target.
    /// </summary>
    public const string CapabilityNotSupported = "database_target_preparation.capability_not_supported";

    /// <summary>
    /// The target configuration does not identify a database provider registered with this provider.
    /// </summary>
    public const string ProviderMismatch = "database_target_preparation.provider_mismatch";

    /// <summary>
    /// The supplied target or administrative connection information is not usable
    /// (for example, it does not identify a target name).
    /// </summary>
    public const string InvalidTarget = "database_target_preparation.invalid_target";

    /// <summary>
    /// The database server could not be reached using the supplied connection information.
    /// </summary>
    public const string ServerUnreachable = "database_target_preparation.server_unreachable";

    /// <summary>
    /// The administrative connection succeeded but lacked permission to complete the operation.
    /// </summary>
    public const string PermissionDenied = "database_target_preparation.permission_denied";

    /// <summary>
    /// Target creation collided with an existing, differently-owned object of the same name.
    /// </summary>
    public const string TargetConflict = "database_target_preparation.target_conflict";

    /// <summary>
    /// A connection could not be established or was lost while preparing the target.
    /// </summary>
    public const string ConnectionFailed = "database_target_preparation.connection_failed";

    /// <summary>
    /// The preparation operation exceeded its allotted timeout.
    /// </summary>
    public const string Timeout = "database_target_preparation.timeout";

    /// <summary>
    /// Target preparation failed for a provider-specific reason not covered by another code.
    /// </summary>
    public const string PreparationFailed = "database_target_preparation.preparation_failed";
}
