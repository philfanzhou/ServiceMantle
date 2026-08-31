namespace ServiceMantle.Migration;

/// <summary>
/// Represents the state of the database observed before attempting migration.
/// </summary>
public enum MigrationObservationState
{
    /// <summary>
    /// The database is empty and ready for schema creation.
    /// </summary>
    Empty,

    /// <summary>
    /// The database exists with a schema compatible with the current application version.
    /// No migration is needed.
    /// </summary>
    CurrentVersionCompatible,

    /// <summary>
    /// The database exists with a schema that requires migration to the current version.
    /// </summary>
    PendingMigration,

    /// <summary>
    /// The database schema version is newer than the current application supports.
    /// Downgrade is required and migration must not proceed.
    /// </summary>
    VersionTooNew,

    /// <summary>
    /// The database state could not be determined.
    /// </summary>
    InspectionFailed
}

/// <summary>
/// Provides the migration execution boundary for a consuming service.
/// </summary>
public interface IDatabaseMigrationExecutor
{
    /// <summary>
    /// Inspects the current database state without making any changes.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancellation token. The orchestrator cancels it for caller cancellation or detected lease
    /// loss; implementations must observe it promptly.
    /// </param>
    /// <returns>The observed state of the database.</returns>
    ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the complete migration workflow for the consuming service.
    /// This is called only once per orchestration session when migration is needed.
    /// The executor is responsible for the full migration flow, including schema changes,
    /// data backfill, validation, and state updates.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancellation token. The orchestrator cancels it for caller cancellation or detected lease
    /// loss; implementations must observe it promptly. Cancellation cannot roll back side effects
    /// the implementation has already committed.
    /// </param>
    /// <exception cref="OperationCanceledException">Migration was cancelled.</exception>
    /// <exception cref="Exception">Migration failed with a service-specific error.</exception>
    ValueTask ExecuteAsync(CancellationToken cancellationToken = default);
}
