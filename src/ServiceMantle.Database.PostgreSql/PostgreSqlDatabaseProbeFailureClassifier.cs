using System.Net.Sockets;
using Npgsql;
using ServiceMantle.Health;

namespace ServiceMantle.Database.PostgreSql;

/// <summary>
/// The PostgreSQL implementation of the health probe failure classification seam: it separates
/// connection-class failures and unreadable mapped schema from exceptions it cannot classify.
/// </summary>
/// <remarks>
/// <para>
/// Classification follows the PostgreSQL SQLSTATE classes the ServiceMantle health snapshot
/// contract distinguishes:
/// <list type="bullet">
/// <item>SQLSTATE class 42 (undefined object, access-rule violation) →
/// <see cref="ServiceDatabaseProbeFailureKind.SchemaUnreadable"/>: the connection works but a
/// probed table or column is dropped, renamed, or no longer readable.</item>
/// <item>Connection-class failures → <see cref="ServiceDatabaseProbeFailureKind.ConnectionFailure"/>:
/// SQLSTATE class 08 (connection exception), 53 (insufficient resources), 57 (operator
/// intervention, including shutdown), <c>3D000</c> (invalid catalog name), <c>55P03</c>
/// (lock not available), a <see cref="PostgresException"/> without a SQLSTATE (transport-level),
/// any other <see cref="NpgsqlException"/> or <see cref="TimeoutException"/>, and wrapped
/// <see cref="SocketException"/>s.</item>
/// <item>Everything else (for example class 28 authentication failures) →
/// <see cref="ServiceDatabaseProbeFailureKind.Unclassified"/>: the exception propagates and the
/// health endpoints answer with their own safe error code.</item>
/// </list>
/// </para>
/// <para>
/// The classifier is a pure function of the exception: no I/O, no clock, no state. It never
/// includes exception text, SQLSTATE values, identifiers, or connection values in its answer —
/// the answer is one enum value.
/// </para>
/// </remarks>
public sealed class PostgreSqlDatabaseProbeFailureClassifier : IServiceDatabaseProbeFailureClassifier
{
    /// <summary>Classifies one PostgreSQL probe exception into a failure kind.</summary>
    /// <param name="exception">The exception observed by the probe.</param>
    /// <returns>
    /// <see cref="ServiceDatabaseProbeFailureKind.ConnectionFailure"/>,
    /// <see cref="ServiceDatabaseProbeFailureKind.SchemaUnreadable"/>, or
    /// <see cref="ServiceDatabaseProbeFailureKind.Unclassified"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public ServiceDatabaseProbeFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Walk the exception chain: drivers and EF Core can wrap the provider exception one or
        // more levels deep, and the first PostgreSQL fact anywhere in the chain decides.
        var current = exception;
        while (current is not null)
        {
            if (current is PostgresException postgresException)
            {
                return ClassifyPostgresException(postgresException);
            }

            if (current is TimeoutException or SocketException)
            {
                return ServiceDatabaseProbeFailureKind.ConnectionFailure;
            }

            if (current is NpgsqlException)
            {
                return ServiceDatabaseProbeFailureKind.ConnectionFailure;
            }

            current = current.InnerException;
        }

        return ServiceDatabaseProbeFailureKind.Unclassified;
    }

    private static ServiceDatabaseProbeFailureKind ClassifyPostgresException(
        PostgresException exception)
    {
        var sqlState = exception.SqlState;

        // A PostgresException without a SQLSTATE did not come from a parsed server error
        // response; it is a transport-level fact.
        if (string.IsNullOrEmpty(sqlState))
        {
            return ServiceDatabaseProbeFailureKind.ConnectionFailure;
        }

        if (sqlState.StartsWith("42", StringComparison.Ordinal))
        {
            return ServiceDatabaseProbeFailureKind.SchemaUnreadable;
        }

        if (sqlState.StartsWith("08", StringComparison.Ordinal) ||
            sqlState.StartsWith("53", StringComparison.Ordinal) ||
            sqlState.StartsWith("57", StringComparison.Ordinal) ||
            sqlState is "3D000" or "55P03")
        {
            return ServiceDatabaseProbeFailureKind.ConnectionFailure;
        }

        return ServiceDatabaseProbeFailureKind.Unclassified;
    }
}
