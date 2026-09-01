namespace ServiceMantle.Health;

/// <summary>Describes migration progress for readiness evaluation.</summary>
public enum ServiceMigrationReadinessState
{
    /// <summary>Migration has not started.</summary>
    NotStarted,

    /// <summary>Migration is currently running.</summary>
    Running,

    /// <summary>Migration completed successfully.</summary>
    Succeeded,

    /// <summary>Migration failed.</summary>
    Failed,
}

/// <summary>Describes whether the business database is currently reachable.</summary>
public enum ServiceDatabaseReadinessState
{
    /// <summary>The database is reachable.</summary>
    Reachable,

    /// <summary>The database is unreachable.</summary>
    Unreachable,
}

/// <summary>
/// An immutable, value-free snapshot used to evaluate service health.
/// </summary>
public sealed class ServiceHealthSnapshot
{
    /// <summary>Initializes a complete finite health snapshot.</summary>
    /// <param name="phase">The current startup phase.</param>
    /// <param name="migrationStatus">The current migration readiness state.</param>
    /// <param name="databaseStatus">The current database reachability state.</param>
    /// <param name="errorCode">An optional stable safe error code.</param>
    public ServiceHealthSnapshot(
        Installation.ServiceStartupPhase phase,
        ServiceMigrationReadinessState migrationStatus,
        ServiceDatabaseReadinessState databaseStatus,
        string? errorCode = null)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (!Enum.IsDefined(migrationStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(migrationStatus));
        }

        if (!Enum.IsDefined(databaseStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(databaseStatus));
        }

        Phase = phase;
        MigrationStatus = migrationStatus;
        DatabaseStatus = databaseStatus;
        ErrorCode = errorCode is null
            ? null
            : ServiceHealthErrorCode.EnsureValid(errorCode, nameof(errorCode));
    }

    /// <summary>Gets the startup phase sampled by the caller.</summary>
    public Installation.ServiceStartupPhase Phase { get; }

    /// <summary>Gets the migration state sampled by the caller.</summary>
    public ServiceMigrationReadinessState MigrationStatus { get; }

    /// <summary>Gets the database reachability sampled by the caller.</summary>
    public ServiceDatabaseReadinessState DatabaseStatus { get; }

    /// <summary>Gets the optional stable safe error code.</summary>
    public string? ErrorCode { get; }

    /// <summary>Returns only finite states and the safe error code.</summary>
    public override string ToString() =>
        $"ServiceHealthSnapshot(Phase={Phase}, MigrationStatus={MigrationStatus}, DatabaseStatus={DatabaseStatus}, ErrorCode={ErrorCode})";
}

internal static class ServiceHealthErrorCode
{
    internal const int MaximumLength = 128;

    internal static string EnsureValid(string errorCode, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(errorCode, parameterName);
        if (errorCode.Length is < 1 or > MaximumLength || !IsAsciiLetterOrDigit(errorCode[0]))
        {
            throw Invalid(parameterName);
        }

        foreach (var character in errorCode)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw Invalid(parameterName);
            }
        }

        return errorCode;
    }

    private static ArgumentException Invalid(string parameterName) => new(
        "A health error code must contain between 1 and 128 ASCII letters, digits, '.', '_', or '-'.",
        parameterName);

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
