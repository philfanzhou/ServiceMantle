using System.Data;
using System.IO;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.SqlServer;

/// <summary>
/// Observes SQL Server database targets and explicitly creates a missing database without altering,
/// dropping, or recreating an existing database.
/// </summary>
public sealed class SqlServerDatabaseTargetPreparationProvider : IDatabaseTargetPreparationProvider
{
    private static readonly TimeSpan MaximumPreparationTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1D);

    private readonly ISqlServerTargetObservationProbe observationProbe;
    private readonly ISqlServerDatabaseCreationProbe creationProbe;

    /// <summary>
    /// Initializes the SQL Server target preparation provider with real database probes.
    /// </summary>
    public SqlServerDatabaseTargetPreparationProvider()
        : this(new SqlServerTargetObservationProbe(), new SqlServerDatabaseCreationProbe())
    {
    }

    internal SqlServerDatabaseTargetPreparationProvider(
        ISqlServerTargetObservationProbe observationProbe,
        ISqlServerDatabaseCreationProbe creationProbe)
    {
        ArgumentNullException.ThrowIfNull(observationProbe);
        ArgumentNullException.ThrowIfNull(creationProbe);

        this.observationProbe = observationProbe;
        this.creationProbe = creationProbe;
    }

    /// <summary>
    /// Gets the canonical SQL Server provider ID.
    /// </summary>
    public string ProviderId => WellKnownDatabaseProviderIds.SqlServer;

    /// <summary>
    /// Gets the server-database target kind.
    /// </summary>
    public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;

    /// <summary>
    /// Observes the target using target credentials and read-only metadata queries. This operation
    /// never creates, changes, or deletes a database object.
    /// </summary>
    public async ValueTask<DatabaseTargetObservation> ObserveAsync(
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSqlServerProvider(target.Provider))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!SqlServerDatabaseTarget.TryBuildConnectionString(target.ConnectionString, out var builder) ||
            !SqlServerDatabaseTarget.TryGetValidDatabaseName(builder, out _))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        SqlServerDatabaseTarget.ApplySafeTimeouts(builder);

        try
        {
            var outcome = await observationProbe.ObserveAsync(builder, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return outcome switch
            {
                SqlServerObservationOutcome.Success => DatabaseTargetObservation.TargetConnectable(),
                SqlServerObservationOutcome.TargetIdentityMismatch =>
                    DatabaseTargetObservation.TargetUnreachable(
                        WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget),
                SqlServerObservationOutcome.ServerVersionUnsupported =>
                    DatabaseTargetObservation.TargetUnreachable(
                        WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed),
                SqlServerObservationOutcome.TargetMissing => DatabaseTargetObservation.TargetMissing(),
                SqlServerObservationOutcome.TargetAccessDeniedUnknown =>
                    DatabaseTargetObservation.TargetUnreachable(
                        WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied),
                SqlServerObservationOutcome.TargetAccessDeniedExisting =>
                    DatabaseTargetObservation.TargetUnreachable(
                        WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                        targetExists: true),
                SqlServerObservationOutcome.TargetUnavailableExisting =>
                    DatabaseTargetObservation.TargetUnreachable(
                        WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
                        targetExists: true),
                SqlServerObservationOutcome.AuthenticationFailed =>
                    DatabaseTargetObservation.TargetUnreachable(
                        WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed),
                SqlServerObservationOutcome.ConnectionFailed =>
                    DatabaseTargetObservation.ServerUnreachable(
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
    /// Creates the requested database only when it is missing. Administrative connection
    /// information is isolated from pooling, ambient transactions, and connection retries and is
    /// never retained.
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

        if (!IsSqlServerProvider(request.Target.Provider))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!SqlServerDatabaseTarget.TryBuildConnectionString(
                request.Target.ConnectionString,
                out var targetBuilder) ||
            !SqlServerDatabaseTarget.TryGetValidDatabaseName(targetBuilder, out var databaseName))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        if (!SqlServerDatabaseTarget.TryBuildConnectionString(
                request.AdministrativeConnectionString,
                out var administrativeBuilder) ||
            !string.IsNullOrEmpty(administrativeBuilder.AttachDBFilename))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        administrativeBuilder.InitialCatalog = "master";
        administrativeBuilder.Pooling = false;
        administrativeBuilder.Enlist = false;
        SqlServerDatabaseTarget.ApplySafeTimeouts(administrativeBuilder);

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

    private static bool IsSqlServerProvider(string provider) =>
        string.Equals(provider, WellKnownDatabaseProviderIds.SqlServer, StringComparison.OrdinalIgnoreCase);

    private static OperationCanceledException CreateSafeCancellationException(
        CancellationToken cancellationToken) =>
        new("Database target preparation was cancelled by the caller.", cancellationToken);
}

internal interface ISqlServerDatabaseCreationProbe
{
    ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
        string databaseName,
        SqlConnectionStringBuilder administrativeConnectionString,
        CancellationToken cancellationToken);
}

internal sealed class SqlServerDatabaseCreationProbe : ISqlServerDatabaseCreationProbe
{
    internal const string DatabaseCollation = "Latin1_General_100_CI_AS_SC_UTF8";
    private readonly Func<CancellationToken, ValueTask>? afterMissingTargetObserved;

    internal SqlServerDatabaseCreationProbe(
        Func<CancellationToken, ValueTask>? afterMissingTargetObserved = null)
    {
        this.afterMissingTargetObserved = afterMissingTargetObserved;
    }

    public async ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
        string databaseName,
        SqlConnectionStringBuilder administrativeConnectionString,
        CancellationToken cancellationToken)
    {
        SqlConnection? connection = null;
        try
        {
            connection = new SqlConnection(administrativeConnectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (await GetServerMajorVersionAsync(connection, cancellationToken).ConfigureAwait(false) <
                SqlServerDatabaseTarget.MinimumSupportedServerMajorVersion)
            {
                return DatabaseTargetPreparationResult.Failure(
                    WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
            }

            var existing = await FindExistingDatabaseAsync(
                    connection,
                    databaseName,
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

            if (existing == ExistingDatabaseMatch.VisibilityUnknown)
            {
                return DatabaseTargetPreparationResult.Failure(
                    WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied);
            }

            if (afterMissingTargetObserved is not null)
            {
                await afterMissingTargetObserved(cancellationToken).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"CREATE DATABASE {SqlServerDatabaseTarget.QuoteIdentifier(databaseName)} " +
                $"COLLATE {DatabaseCollation}";
            command.CommandTimeout = SqlServerDatabaseTarget.CommandTimeoutSeconds;

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return DatabaseTargetPreparationResult.Success(
                    DatabaseTargetPreparationOutcome.Created);
            }
            catch (SqlException exception) when (ContainsError(exception, 1801))
            {
                var raceResult = await FindExistingDatabaseAsync(
                        connection,
                        databaseName,
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
                    "SQL Server database creation was cancelled.",
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
            if (current is SqlException sqlException)
            {
                foreach (SqlError error in sqlException.Errors)
                {
                    var classified = ClassifyFailure(error.Number);
                    if (classified != WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed)
                    {
                        return classified;
                    }
                }
            }

            if (current is TimeoutException)
            {
                return WellKnownDatabaseTargetPreparationErrorCodes.Timeout;
            }

            if (current is SocketException or IOException)
            {
                return WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed;
            }
        }

        return WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed;
    }

    internal static string ClassifyFailure(int errorNumber)
    {
        if (errorNumber == -2)
        {
            return WellKnownDatabaseTargetPreparationErrorCodes.Timeout;
        }

        if (errorNumber is 229 or 262 or 916 or 15151)
        {
            return WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied;
        }

        if (errorNumber == 1801)
        {
            return WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict;
        }

        return SqlServerProbeFailureClassifier.Classify(errorNumber) switch
        {
            SqlServerObservationOutcome.AuthenticationFailed =>
                WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
            SqlServerObservationOutcome.ConnectionFailed or
            SqlServerObservationOutcome.TargetAccessDeniedUnknown =>
                WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            _ => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed
        };
    }

    private static async ValueTask<int> GetServerMajorVersionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))";
        command.CommandTimeout = SqlServerDatabaseTarget.CommandTimeoutSeconds;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<ExistingDatabaseMatch> FindExistingDatabaseAsync(
        SqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE'),
                HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER ANY DATABASE'),
                HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE DATABASE'),
                candidate.match
            FROM (VALUES (0)) AS singleton(value)
            OUTER APPLY
            (
                SELECT TOP (1)
                    CASE
                        WHEN [name] COLLATE Latin1_General_100_BIN2 =
                             @databaseName COLLATE Latin1_General_100_BIN2 THEN 1
                        ELSE 2
                    END AS match
                FROM sys.databases
                WHERE [name] = @databaseName
                ORDER BY CASE
                    WHEN [name] COLLATE Latin1_General_100_BIN2 =
                         @databaseName COLLATE Latin1_General_100_BIN2 THEN 0
                    ELSE 1
                END
            ) AS candidate;
            """;
        command.CommandTimeout = SqlServerDatabaseTarget.CommandTimeoutSeconds;
        command.Parameters.Add("@databaseName", SqlDbType.NVarChar, 123).Value = databaseName;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ExistingDatabaseMatch.VisibilityUnknown;
        }

        return InterpretExistingDatabase(
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3));
    }

    internal static ExistingDatabaseMatch InterpretExistingDatabase(
        int? hasViewAnyDatabase,
        int? hasAlterAnyDatabase,
        int? hasCreateDatabase,
        int? match)
    {
        return match switch
        {
            1 => ExistingDatabaseMatch.Exact,
            2 => ExistingDatabaseMatch.Conflicting,
            _ when SqlServerDatabaseTarget.HasCompleteDatabaseVisibility(
                hasViewAnyDatabase,
                hasAlterAnyDatabase,
                hasCreateDatabase) =>
                ExistingDatabaseMatch.Missing,
            _ => ExistingDatabaseMatch.VisibilityUnknown
        };
    }

    private static bool ContainsError(SqlException exception, int errorNumber)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number == errorNumber)
            {
                return true;
            }
        }

        return false;
    }

    internal enum ExistingDatabaseMatch
    {
        Missing,
        Exact,
        Conflicting,
        VisibilityUnknown
    }
}
