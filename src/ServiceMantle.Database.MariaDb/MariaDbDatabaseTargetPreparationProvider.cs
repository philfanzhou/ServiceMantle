using MySqlConnector;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.MariaDb;

/// <summary>
/// Observes MariaDB database targets and explicitly creates a missing database without altering,
/// dropping, or recreating an existing database.
/// </summary>
public sealed class MariaDbDatabaseTargetPreparationProvider : IDatabaseTargetPreparationProvider
{
    private static readonly TimeSpan MaximumPreparationTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1D);

    private readonly IMariaDbBootstrapProbe observationProbe;
    private readonly IMariaDbDatabaseCreationProbe creationProbe;

    /// <summary>
    /// Initializes the MariaDB target preparation provider with real database probes.
    /// </summary>
    public MariaDbDatabaseTargetPreparationProvider()
        : this(new MariaDbBootstrapProbe(), new MariaDbDatabaseCreationProbe())
    {
    }

    internal MariaDbDatabaseTargetPreparationProvider(
        IMariaDbBootstrapProbe observationProbe,
        IMariaDbDatabaseCreationProbe creationProbe)
    {
        ArgumentNullException.ThrowIfNull(observationProbe);
        ArgumentNullException.ThrowIfNull(creationProbe);

        this.observationProbe = observationProbe;
        this.creationProbe = creationProbe;
    }

    /// <summary>
    /// Gets the canonical MariaDB provider ID, which is distinct from MySQL.
    /// </summary>
    public string ProviderId => WellKnownDatabaseProviderIds.MariaDb;

    /// <summary>
    /// Gets the server-database target kind.
    /// </summary>
    public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;

    /// <summary>
    /// Observes the target through read-only connection attempts and never creates, changes, or
    /// deletes a database object.
    /// </summary>
    public async ValueTask<DatabaseTargetObservation> ObserveAsync(
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsMariaDbProvider(target.Provider))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!MariaDbDatabaseTarget.TryBuildConnectionString(target.ConnectionString, out var builder) ||
            !MariaDbDatabaseTarget.TryGetValidDatabaseName(builder, out _))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        MariaDbDatabaseTarget.ApplySafeTimeouts(builder);

        try
        {
            var outcome = await observationProbe.ProbeAsync(
                    builder,
                    (int)MariaDbDatabaseTarget.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return outcome switch
            {
                MariaDbProbeOutcome.Success => DatabaseTargetObservation.TargetConnectable(),
                MariaDbProbeOutcome.ServerProductMismatch or
                MariaDbProbeOutcome.TargetIdentityMismatch => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget),
                MariaDbProbeOutcome.DatabaseNotFound => DatabaseTargetObservation.TargetMissing(),
                MariaDbProbeOutcome.AuthenticationFailed => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed),
                MariaDbProbeOutcome.TargetAccessDenied => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied),
                MariaDbProbeOutcome.ConnectionFailed => DatabaseTargetObservation.ServerUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.ServerUnreachable),
                _ => DatabaseTargetObservation.ServerUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed)
            };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw CreateSafeCancellationException(cancellationToken);
        }
        catch (Exception)
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }
    }

    /// <summary>
    /// Creates the requested database only when it is missing and the server identifies itself as
    /// MariaDB. Administrative connection information is isolated from pooling and ambient
    /// transactions and is never retained.
    /// </summary>
    public async ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
        DatabaseTargetPreparationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (timeout <= TimeSpan.Zero || timeout > MaximumPreparationTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                $"Preparation timeout must be positive and no greater than {MaximumPreparationTimeout}.");
        }

        if (!IsMariaDbProvider(request.Target.Provider))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!MariaDbDatabaseTarget.TryBuildConnectionString(
                request.Target.ConnectionString,
                out var targetBuilder) ||
            !MariaDbDatabaseTarget.TryGetValidDatabaseName(targetBuilder, out var databaseName))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        if (!MariaDbDatabaseTarget.TryBuildConnectionString(
                request.AdministrativeConnectionString,
                out var administrativeBuilder))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        administrativeBuilder.Database = string.Empty;
        administrativeBuilder.Pooling = false;
        administrativeBuilder.AutoEnlist = false;
        MariaDbDatabaseTarget.ApplySafeTimeouts(administrativeBuilder);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        try
        {
            return await creationProbe.CreateIfMissingAsync(
                    databaseName,
                    administrativeBuilder,
                    linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw CreateSafeCancellationException(cancellationToken);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.Timeout);
        }
        catch (Exception) when (timeoutCts.IsCancellationRequested)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.Timeout);
        }
        catch (Exception)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }
    }

    private static bool IsMariaDbProvider(string provider) =>
        string.Equals(provider, WellKnownDatabaseProviderIds.MariaDb, StringComparison.OrdinalIgnoreCase);

    private static OperationCanceledException CreateSafeCancellationException(
        CancellationToken cancellationToken) =>
        new("Database target preparation was cancelled by the caller.", cancellationToken);
}

internal interface IMariaDbDatabaseCreationProbe
{
    ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
        string databaseName,
        MySqlConnectionStringBuilder administrativeConnectionString,
        CancellationToken cancellationToken);
}

internal sealed class MariaDbDatabaseCreationProbe : IMariaDbDatabaseCreationProbe
{
    private const string DatabaseCharacterSet = "utf8mb4";
    private const string DatabaseCollation = "utf8mb4_uca1400_ai_ci";
    private readonly Func<CancellationToken, ValueTask>? afterMissingTargetObserved;

    internal MariaDbDatabaseCreationProbe(
        Func<CancellationToken, ValueTask>? afterMissingTargetObserved = null)
    {
        this.afterMissingTargetObserved = afterMissingTargetObserved;
    }

    public async ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
        string databaseName,
        MySqlConnectionStringBuilder administrativeConnectionString,
        CancellationToken cancellationToken)
    {
        MySqlConnection? connection = null;
        try
        {
            connection = new MySqlConnection(administrativeConnectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (!await IsMariaDbServerAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                return DatabaseTargetPreparationResult.Failure(
                    WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
            }

            var lowerCaseTableNames = await GetLowerCaseTableNamesAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            var existing = await FindExistingDatabaseAsync(
                    connection,
                    databaseName,
                    lowerCaseTableNames,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing == ExistingDatabaseMatch.Exact)
            {
                return DatabaseTargetPreparationResult.Success(
                    DatabaseTargetPreparationOutcome.AlreadyExists);
            }

            if (existing == ExistingDatabaseMatch.Conflicting)
            {
                return DatabaseTargetPreparationResult.Failure(
                    WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict);
            }

            if (afterMissingTargetObserved is not null)
            {
                await afterMissingTargetObserved(cancellationToken).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"CREATE DATABASE {MariaDbDatabaseTarget.QuoteIdentifier(databaseName)} " +
                $"CHARACTER SET {DatabaseCharacterSet} COLLATE {DatabaseCollation}";

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return DatabaseTargetPreparationResult.Success(
                    DatabaseTargetPreparationOutcome.Created);
            }
            catch (MySqlException exception)
                when (exception.ErrorCode == MySqlErrorCode.DatabaseCreateExists)
            {
                var raceResult = await FindExistingDatabaseAsync(
                        connection,
                        databaseName,
                        lowerCaseTableNames,
                        cancellationToken)
                    .ConfigureAwait(false);
                return raceResult == ExistingDatabaseMatch.Exact
                    ? DatabaseTargetPreparationResult.Success(
                        DatabaseTargetPreparationOutcome.AlreadyExists)
                    : DatabaseTargetPreparationResult.Failure(
                        WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict);
            }
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "MariaDB database creation was cancelled.",
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
                    // Cleanup failure must not replace the safe primary result.
                }
            }
        }
    }

    internal static string ClassifyFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlException mySqlException)
            {
                return ClassifyFailure(mySqlException.ErrorCode);
            }

            if (current is System.Net.Sockets.SocketException or
                System.IO.IOException or
                TimeoutException)
            {
                return WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed;
            }
        }

        return WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed;
    }

    internal static string ClassifyFailure(MySqlErrorCode errorCode)
    {
        if (errorCode is
            MySqlErrorCode.DatabaseAccessDenied or
            MySqlErrorCode.SpecifiedAccessDeniedError)
        {
            return WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied;
        }

        if (errorCode == MySqlErrorCode.DatabaseCreateExists)
        {
            return WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict;
        }

        return MariaDbProbeFailureClassifier.Classify(errorCode) switch
        {
            MariaDbProbeOutcome.AuthenticationFailed =>
                WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
            MariaDbProbeOutcome.TargetAccessDenied =>
                WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
            MariaDbProbeOutcome.ConnectionFailed =>
                WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            _ => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed
        };
    }

    private static async ValueTask<bool> IsMariaDbServerAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT VERSION()";
        var serverVersion = (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))?
            .ToString();
        return MariaDbDatabaseTarget.IsMariaDbServerVersion(serverVersion);
    }

    private static async ValueTask<int> GetLowerCaseTableNamesAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@lower_case_table_names";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<ExistingDatabaseMatch> FindExistingDatabaseAsync(
        MySqlConnection connection,
        string databaseName,
        int lowerCaseTableNames,
        CancellationToken cancellationToken)
    {
        await using var exactCommand = connection.CreateCommand();
        exactCommand.CommandText =
            "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA " +
            "WHERE BINARY SCHEMA_NAME = BINARY @name LIMIT 1";
        exactCommand.Parameters.AddWithValue("@name", databaseName);
        var exact = await exactCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (exact is not null)
        {
            return ExistingDatabaseMatch.Exact;
        }

        if (lowerCaseTableNames == 0)
        {
            return ExistingDatabaseMatch.Missing;
        }

        await using var foldedCommand = connection.CreateCommand();
        foldedCommand.CommandText =
            "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA " +
            "WHERE LOWER(SCHEMA_NAME) = LOWER(@name) LIMIT 1";
        foldedCommand.Parameters.AddWithValue("@name", databaseName);
        return await foldedCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null
            ? ExistingDatabaseMatch.Missing
            : ExistingDatabaseMatch.Conflicting;
    }

    private enum ExistingDatabaseMatch
    {
        Missing,
        Exact,
        Conflicting
    }
}
