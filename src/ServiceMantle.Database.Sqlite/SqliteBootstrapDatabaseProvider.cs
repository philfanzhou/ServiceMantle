using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.Sqlite;

/// <summary>Validates existing local SQLite file targets for Bootstrap persistence.</summary>
/// <remarks>
/// Uses the same File parsing and read-only observation boundary as
/// <see cref="SqliteDatabaseTargetPreparationProvider"/>. Validation never prepares a missing
/// target or attempts recovery. Preparation and deployment capabilities require their own
/// explicit registration. External file replacement and cross-process exclusion are not guaranteed.
/// </remarks>
public sealed class SqliteBootstrapDatabaseProvider : IBootstrapDatabaseProvider
{
    private readonly SqliteDatabaseTargetPreparationProvider observer;

    /// <summary>Initializes the provider with local read-only file observation.</summary>
    public SqliteBootstrapDatabaseProvider()
        : this(new SqliteDatabaseTargetPreparationProvider())
    {
    }

    internal SqliteBootstrapDatabaseProvider(SqliteDatabaseTargetPreparationProvider observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        this.observer = observer;
    }

    /// <summary>Gets the canonical SQLite File descriptor; server versions are forbidden.</summary>
    public BootstrapDatabaseProviderDescriptor Descriptor { get; } = new(
        WellKnownDatabaseProviderIds.Sqlite,
        "SQLite",
        BootstrapDatabaseTargetKind.File,
        BootstrapServerVersionRequirement.Forbidden);

    /// <summary>
    /// Accepts only a target that can be observed as connectable without file writes.
    /// Failures return fixed Bootstrap codes without paths, connection strings, or driver messages.
    /// Caller cancellation propagates with the original token.
    /// </summary>
    public async ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapDatabaseConfiguration database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(database.Provider, Descriptor.Id, StringComparison.OrdinalIgnoreCase))
        {
            return BootstrapValidationResult.Failure("database.provider_mismatch");
        }

        if (!string.IsNullOrWhiteSpace(database.ServerVersion))
        {
            return BootstrapValidationResult.Failure("database.server_version_not_allowed");
        }

        var observation = await observer.ObserveAsync(database, cancellationToken).ConfigureAwait(false);
        return observation.Status switch
        {
            DatabaseTargetObservationStatus.TargetConnectable => BootstrapValidationResult.Success(),
            DatabaseTargetObservationStatus.TargetMissing =>
                BootstrapValidationResult.Failure("database.target_not_found"),
            _ => BootstrapValidationResult.Failure(observation.ErrorCode switch
            {
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget =>
                    "database.connection_string_invalid",
                WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied =>
                    "database.permission_denied",
                WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed or
                    WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict => "database.connection_failed",
                _ => "database.provider_validation_failed"
            })
        };
    }
}
