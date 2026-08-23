namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// Identifies the relational SQL dialect used to build the management audit model. The dialect is
/// required because SQL has no portable text-length function for database check constraints.
/// </summary>
public enum ManagementAuditDatabaseDialect
{
    /// <summary>
    /// SQLite SQL dialect.
    /// </summary>
    Sqlite = 0,

    /// <summary>
    /// PostgreSQL SQL dialect.
    /// </summary>
    PostgreSql = 1,

    /// <summary>
    /// Microsoft SQL Server SQL dialect.
    /// </summary>
    SqlServer = 2
}

internal static class ManagementAuditDatabaseFunctions
{
    internal static long? TextByteLength(string? value) =>
        throw new InvalidOperationException("This method may only be used in an EF Core query.");
}
