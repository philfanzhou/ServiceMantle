using System.Net.Sockets;
using Npgsql;
using ServiceMantle.Health;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

/// <summary>
/// Unit tests for <see cref="PostgreSqlDatabaseProbeFailureClassifier"/>: the SQLSTATE class
/// table of the health probe failure seam, with no database required.
/// </summary>
public sealed class PostgreSqlDatabaseProbeFailureClassifierTests
{
    [Theory]
    [InlineData("42P01", ServiceDatabaseProbeFailureKind.SchemaUnreadable)] // undefined table
    [InlineData("42703", ServiceDatabaseProbeFailureKind.SchemaUnreadable)] // undefined column
    [InlineData("42501", ServiceDatabaseProbeFailureKind.SchemaUnreadable)] // insufficient privilege
    [InlineData("42883", ServiceDatabaseProbeFailureKind.SchemaUnreadable)] // undefined function
    [InlineData("08000", ServiceDatabaseProbeFailureKind.ConnectionFailure)] // connection exception
    [InlineData("08006", ServiceDatabaseProbeFailureKind.ConnectionFailure)]
    [InlineData("53300", ServiceDatabaseProbeFailureKind.ConnectionFailure)] // too many connections
    [InlineData("57P01", ServiceDatabaseProbeFailureKind.ConnectionFailure)] // admin shutdown
    [InlineData("57P03", ServiceDatabaseProbeFailureKind.ConnectionFailure)] // cannot connect now
    [InlineData("3D000", ServiceDatabaseProbeFailureKind.ConnectionFailure)] // invalid catalog name
    [InlineData("55P03", ServiceDatabaseProbeFailureKind.ConnectionFailure)] // lock not available
    [InlineData("28P01", ServiceDatabaseProbeFailureKind.Unclassified)] // authentication
    [InlineData("23505", ServiceDatabaseProbeFailureKind.Unclassified)] // unique violation
    [InlineData("XX000", ServiceDatabaseProbeFailureKind.Unclassified)] // internal error
    public void Sqlstate_classes_map_to_the_fixed_failure_kinds(
        string sqlState,
        ServiceDatabaseProbeFailureKind expected)
    {
        var exception = new PostgresException("message-text", "ERROR", "ERROR", sqlState);

        Assert.Equal(expected, Classify(exception));
    }

    [Fact]
    public void A_postgres_exception_without_a_sqlstate_is_a_connection_failure()
    {
        var exception = new NpgsqlException("transport-level failure");

        Assert.Equal(ServiceDatabaseProbeFailureKind.ConnectionFailure, Classify(exception));
    }

    [Fact]
    public void Driver_timeouts_and_socket_failures_are_connection_failures()
    {
        Assert.Equal(
            ServiceDatabaseProbeFailureKind.ConnectionFailure,
            Classify(new TimeoutException("connect timed out")));
        Assert.Equal(
            ServiceDatabaseProbeFailureKind.ConnectionFailure,
            Classify(new NpgsqlException("socket error", new SocketException())));
    }

    [Fact]
    public void Wrapped_provider_exceptions_are_classified_from_the_innermost_sqlstate()
    {
        var postgresException =
            new PostgresException("relation does not exist", "ERROR", "ERROR", "42P01");
        var wrapped = new InvalidOperationException("EF wrapper", postgresException);

        Assert.Equal(ServiceDatabaseProbeFailureKind.SchemaUnreadable, Classify(wrapped));
    }

    [Fact]
    public void Unrelated_exceptions_are_unclassified()
    {
        Assert.Equal(
            ServiceDatabaseProbeFailureKind.Unclassified,
            Classify(new InvalidOperationException("not a database fact")));
        Assert.Equal(
            ServiceDatabaseProbeFailureKind.Unclassified,
            Classify(new ArgumentException("not a database fact either")));
    }

    [Fact]
    public void Null_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDatabaseProbeFailureClassifier()
            .Classify(null!));
    }

    private static ServiceDatabaseProbeFailureKind Classify(Exception exception) =>
        new PostgreSqlDatabaseProbeFailureClassifier().Classify(exception);
}
