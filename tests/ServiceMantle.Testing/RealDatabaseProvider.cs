namespace ServiceMantle.Testing;

/// <summary>
/// Identifies a database product used by a real-database test fixture.
/// </summary>
public enum RealDatabaseProvider
{
    PostgreSql,
    MySql,
    MariaDb,
    Oracle,
    SqlServer
}
