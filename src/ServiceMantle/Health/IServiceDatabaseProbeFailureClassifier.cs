namespace ServiceMantle.Health;

/// <summary>
/// Classifies an exception observed while probing a database for a health snapshot into one of
/// the two facts the snapshot contract distinguishes, or reports that the exception cannot be
/// classified.
/// </summary>
public enum ServiceDatabaseProbeFailureKind
{
    /// <summary>
    /// The database is not reachable through the current connection. Maps to
    /// <see cref="ServiceDatabaseReadinessState.Unreachable"/> without changing the migration
    /// state.
    /// </summary>
    ConnectionFailure,

    /// <summary>
    /// The connection works but a mapped table or column cannot be read. Maps to migration
    /// state <see cref="ServiceMigrationReadinessState.Failed"/> while the database stays
    /// reachable.
    /// </summary>
    SchemaUnreadable,

    /// <summary>
    /// The exception carries no known fact. The probing component lets it propagate so the
    /// health endpoints answer with their own safe error code instead of an invented state.
    /// </summary>
    Unclassified
}

/// <summary>
/// A replaceable classification seam that turns one database probe exception into a
/// <see cref="ServiceDatabaseProbeFailureKind"/>. Implementations are provider-specific; the
/// signature intentionally depends only on base-library types so any database package can
/// implement it without depending on a persistence stack.
/// </summary>
/// <remarks>
/// <para>
/// Contract: the classifier receives a non-null exception that was not caused by caller
/// cancellation (cancellation propagates before classification). It must not perform I/O, must
/// not throw, and must not report <see cref="ServiceDatabaseProbeFailureKind.SchemaUnreadable"/>
/// for a failure observed while a connection was never established.
/// </para>
/// <para>
/// Non-guarantees: the classifier sees one exception from one probe attempt only. It is not
/// asked about background health, is not consulted again on the same failure, and its answer is
/// not evidence about any connection other than the one the probe used.
/// </para>
/// </remarks>
public interface IServiceDatabaseProbeFailureClassifier
{
    /// <summary>Classifies one probe exception into a failure kind.</summary>
    /// <param name="exception">The exception observed by the probe.</param>
    /// <returns>The failure kind; <see cref="ServiceDatabaseProbeFailureKind.Unclassified"/>
    /// when the exception carries no known fact.</returns>
    ServiceDatabaseProbeFailureKind Classify(Exception exception);
}
