using System;
using Npgsql;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.PostgreSql;

/// <summary>
/// Observes PostgreSQL database targets and, when explicitly requested, creates a missing target
/// database. Never overwrites, drops, or recreates a database that already exists.
/// </summary>
public sealed class PostgreSqlDatabaseTargetPreparationProvider : IDatabaseTargetPreparationProvider
{
    private const int MaximumConnectTimeoutSeconds = 8;
    private const int CommandTimeoutSeconds = 5;

    private readonly INpgsqlBootstrapProbe observationProbe;
    private readonly INpgsqlDatabaseCreationProbe creationProbe;

    /// <summary>
    /// Initializes the PostgreSQL target preparation provider with real probe implementations.
    /// </summary>
    public PostgreSqlDatabaseTargetPreparationProvider()
        : this(new NpgsqlBootstrapProbe(), new NpgsqlDatabaseCreationProbe())
    {
    }

    /// <summary>
    /// Initializes the PostgreSQL target preparation provider with test probe implementations.
    /// </summary>
    internal PostgreSqlDatabaseTargetPreparationProvider(
        INpgsqlBootstrapProbe observationProbe,
        INpgsqlDatabaseCreationProbe creationProbe)
    {
        ArgumentNullException.ThrowIfNull(observationProbe);
        ArgumentNullException.ThrowIfNull(creationProbe);

        this.observationProbe = observationProbe;
        this.creationProbe = creationProbe;
    }

    /// <summary>
    /// Gets the PostgreSQL provider id.
    /// </summary>
    public string ProviderId => WellKnownDatabaseProviderIds.PostgreSql;

    /// <summary>
    /// Gets the target kind this provider prepares.
    /// </summary>
    public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;

    /// <summary>
    /// Observes a PostgreSQL target by attempting a single connection to it. A structured
    /// "database does not exist" response already proves the server is reachable, so no separate
    /// maintenance-database round trip is required to distinguish an unreachable server from a
    /// missing target.
    /// </summary>
    public async ValueTask<DatabaseTargetObservation> ObserveAsync(
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!IsPostgreSqlProvider(target.Provider))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!TryBuildConnectionString(target.ConnectionString, out var builder) ||
            string.IsNullOrWhiteSpace(builder.Database))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        builder.Timeout = ApplySafeConnectTimeout(builder.Timeout);

        try
        {
            var outcome = await observationProbe.ProbeAsync(builder, CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            return outcome switch
            {
                PostgreSqlProbeOutcome.Success => DatabaseTargetObservation.TargetConnectable(),
                PostgreSqlProbeOutcome.DatabaseNotFound => DatabaseTargetObservation.TargetMissing(),
                PostgreSqlProbeOutcome.AuthenticationFailed => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied),
                PostgreSqlProbeOutcome.ConnectionFailed => DatabaseTargetObservation.ServerUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed),
                _ => DatabaseTargetObservation.ServerUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }
    }

    /// <summary>
    /// Creates the target database only when it does not already exist. The administrative
    /// connection string is used only for the duration of this call and is never persisted,
    /// logged, or returned.
    /// </summary>
    public async ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
        DatabaseTargetPreparationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Preparation timeout must be positive.", nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!IsPostgreSqlProvider(request.Target.Provider))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!TryBuildConnectionString(request.Target.ConnectionString, out var targetBuilder) ||
            string.IsNullOrWhiteSpace(targetBuilder.Database))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        if (!TryBuildConnectionString(request.AdministrativeConnectionString, out var administrativeBuilder))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        var databaseName = targetBuilder.Database!;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await creationProbe.CreateIfMissingAsync(databaseName, administrativeBuilder, linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return DatabaseTargetPreparationResult.Failure(WellKnownDatabaseTargetPreparationErrorCodes.Timeout);
        }
        catch (Exception)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }
    }

    private static bool IsPostgreSqlProvider(string provider) =>
        string.Equals(provider, WellKnownDatabaseProviderIds.PostgreSql, StringComparison.OrdinalIgnoreCase);

    private static bool TryBuildConnectionString(string connectionString, out NpgsqlConnectionStringBuilder builder)
    {
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NpgsqlException)
        {
            builder = null!;
            return false;
        }
    }

    private static int ApplySafeConnectTimeout(int timeout)
    {
        if (timeout <= 0)
        {
            return MaximumConnectTimeoutSeconds;
        }

        return Math.Min(timeout, MaximumConnectTimeoutSeconds);
    }
}

/// <summary>
/// Creates a PostgreSQL database using an administrative connection, only when it does not
/// already exist. Concurrent creation by another instance is treated as success, not a failure.
/// </summary>
internal interface INpgsqlDatabaseCreationProbe
{
    ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
        string databaseName,
        NpgsqlConnectionStringBuilder administrativeConnectionString,
        CancellationToken cancellationToken);
}

internal sealed class NpgsqlDatabaseCreationProbe : INpgsqlDatabaseCreationProbe
{
    public async ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
        string databaseName,
        NpgsqlConnectionStringBuilder administrativeConnectionString,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = null;
        try
        {
            connection = new NpgsqlConnection(administrativeConnectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var checkCommand = connection.CreateCommand())
            {
                checkCommand.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
                var parameter = checkCommand.CreateParameter();
                parameter.ParameterName = "@name";
                parameter.Value = databaseName;
                checkCommand.Parameters.Add(parameter);

                var existing = await checkCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    return DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.AlreadyExists);
                }
            }

            await using (var createCommand = connection.CreateCommand())
            {
                createCommand.CommandText =
                    $"CREATE DATABASE \"{databaseName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

                try
                {
                    await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    return DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created);
                }
                catch (PostgresException exception) when (IsConcurrentCreationRace(exception))
                {
                    // Another instance created the database between our existence check and CREATE
                    // DATABASE. A genuinely concurrent race is observed as a unique-key violation on
                    // the pg_database catalog's name index (23505); a race resolved by the time this
                    // statement runs is observed as the higher-level duplicate_database error
                    // (42P04). Both mean the target now exists and this call did not create it.
                    return DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.AlreadyExists);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Npgsql does not always surface a cancelled connection attempt as
            // OperationCanceledException directly; it can wrap it in NpgsqlException or a socket
            // error instead. Treat any failure that occurs while our token is already cancelled as
            // cancellation, so the caller (PrepareAsync) can still distinguish a real timeout from
            // caller-requested cancellation instead of misclassifying it as a connection failure.
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Database target preparation was cancelled.",
                    exception,
                    cancellationToken);
            }

            return DatabaseTargetPreparationResult.Failure(ClassifyFailure(exception));
        }
        finally
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Suppress cleanup errors so they do not mask the primary result.
                }
            }
        }
    }

    private static bool IsConcurrentCreationRace(PostgresException exception) =>
        string.Equals(exception.SqlState, PostgresErrorCodes.DuplicateDatabase, StringComparison.Ordinal) ||
        (string.Equals(exception.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal) &&
         string.Equals(exception.ConstraintName, "pg_database_datname_index", StringComparison.Ordinal));

    private static string ClassifyFailure(Exception exception)
    {
        if (exception is PostgresException postgresException)
        {
            if (string.Equals(postgresException.SqlState, PostgresErrorCodes.InsufficientPrivilege, StringComparison.Ordinal))
            {
                return WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied;
            }

            if (string.Equals(postgresException.SqlState, PostgresErrorCodes.DuplicateObject, StringComparison.Ordinal))
            {
                return WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict;
            }
        }

        var outcome = PostgreSqlProbeFailureClassifier.Classify(exception);
        return outcome switch
        {
            PostgreSqlProbeOutcome.AuthenticationFailed => WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            PostgreSqlProbeOutcome.ConnectionFailed => WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            PostgreSqlProbeOutcome.DatabaseNotFound => WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            _ => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed
        };
    }
}
