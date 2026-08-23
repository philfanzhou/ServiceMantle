namespace ServiceMantle.Migration;

/// <summary>
/// Well-known safe error codes for migration operations.
/// </summary>
public static class WellKnownMigrationErrorCodes
{
    /// <summary>
    /// The database provider does not support migration locks.
    /// </summary>
    public const string LockNotSupported = "migration.lock_not_supported";

    /// <summary>
    /// Lock acquisition timed out.
    /// </summary>
    public const string LockTimeout = "migration.lock_timeout";

    /// <summary>
    /// Lock acquisition failed for a provider-specific reason.
    /// </summary>
    public const string LockFailed = "migration.lock_failed";

    /// <summary>
    /// Database state inspection failed.
    /// </summary>
    public const string InspectionFailed = "migration.inspection_failed";

    /// <summary>
    /// The database schema version is newer than the application supports.
    /// </summary>
    public const string VersionTooNew = "migration.version_too_new";

    /// <summary>
    /// Migration execution failed.
    /// </summary>
    public const string ExecutionFailed = "migration.execution_failed";

    /// <summary>
    /// The final database state is not compatible with the current application.
    /// </summary>
    public const string FinalStateInvalid = "migration.final_state_invalid";
}
