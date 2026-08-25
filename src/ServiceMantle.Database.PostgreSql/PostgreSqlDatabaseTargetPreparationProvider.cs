using System;
using System.Text;
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
    private static readonly TimeSpan MaximumPreparationTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1D);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsPostgreSqlProvider(target.Provider))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!TryBuildConnectionString(target.ConnectionString, out var builder) ||
            !TryGetValidDatabaseName(builder, out _))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        builder.Timeout = ApplySafeConnectTimeout(builder.Timeout);

        try
        {
            var outcome = await observationProbe.ProbeAsync(builder, CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return outcome switch
            {
                PostgreSqlProbeOutcome.Success => DatabaseTargetObservation.TargetConnectable(),
                PostgreSqlProbeOutcome.TargetIdentityMismatch => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget),
                PostgreSqlProbeOutcome.DatabaseNotFound => DatabaseTargetObservation.TargetMissing(),
                PostgreSqlProbeOutcome.AuthenticationFailed => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed),
                PostgreSqlProbeOutcome.TargetAccessDenied => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                    targetExists: true),
                PostgreSqlProbeOutcome.ConnectionFailed => DatabaseTargetObservation.ServerUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed),
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

        cancellationToken.ThrowIfCancellationRequested();

        if (timeout <= TimeSpan.Zero || timeout > MaximumPreparationTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                $"Preparation timeout must be positive and no greater than {MaximumPreparationTimeout}.");
        }

        if (!IsPostgreSqlProvider(request.Target.Provider))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!TryBuildConnectionString(request.Target.ConnectionString, out var targetBuilder) ||
            !TryGetValidDatabaseName(targetBuilder, out var databaseName) ||
            !TryGetValidRoleName(targetBuilder, out var ownerName))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        if (!TryBuildConnectionString(request.AdministrativeConnectionString, out var administrativeBuilder))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        // Administrative credentials are scoped to this call. Npgsql pooling would otherwise
        // retain the privileged physical connection after DisposeAsync returns. The connection
        // must also remain outside any ambient transaction because PostgreSQL prohibits
        // CREATE DATABASE inside a transaction block.
        administrativeBuilder.Pooling = false;
        administrativeBuilder.Enlist = false;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await creationProbe.CreateIfMissingAsync(
                    databaseName,
                    ownerName,
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
            return DatabaseTargetPreparationResult.Failure(WellKnownDatabaseTargetPreparationErrorCodes.Timeout);
        }
        catch (Exception) when (timeoutCts.IsCancellationRequested)
        {
            return DatabaseTargetPreparationResult.Failure(WellKnownDatabaseTargetPreparationErrorCodes.Timeout);
        }
        catch (Exception)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }
    }

    private static bool IsPostgreSqlProvider(string provider) =>
        PostgreSqlProviderId.IsSupported(provider);

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

    private static bool TryGetValidDatabaseName(
        NpgsqlConnectionStringBuilder builder,
        out string databaseName)
    {
        databaseName = builder.Database ?? string.Empty;
        if (string.IsNullOrWhiteSpace(databaseName) || databaseName.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(databaseName);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryGetValidRoleName(
        NpgsqlConnectionStringBuilder builder,
        out string roleName)
    {
        roleName = builder.Username ?? string.Empty;
        if (string.IsNullOrWhiteSpace(roleName) || roleName.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(roleName);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static OperationCanceledException CreateSafeCancellationException(CancellationToken cancellationToken) =>
        new("Database target preparation was cancelled by the caller.", cancellationToken);

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
        string ownerName,
        NpgsqlConnectionStringBuilder administrativeConnectionString,
        CancellationToken cancellationToken);
}

internal sealed class NpgsqlDatabaseCreationProbe : INpgsqlDatabaseCreationProbe
{
    private const int MaximumIdentifierBytes = 63;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<CancellationToken, ValueTask>? afterMissingTargetObserved;

    internal NpgsqlDatabaseCreationProbe(
        Func<CancellationToken, ValueTask>? afterMissingTargetObserved = null)
    {
        this.afterMissingTargetObserved = afterMissingTargetObserved;
    }

    public async ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
        string databaseName,
        string ownerName,
        NpgsqlConnectionStringBuilder administrativeConnectionString,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = null;
        try
        {
            connection = new NpgsqlConnection(administrativeConnectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var serverEncoding = await GetServerEncodingAsync(connection, cancellationToken).ConfigureAwait(false);

            // Validate before any side effect: the created database must be reachable through the
            // same driver that prepared it.
            if (!IsIdentitySupportedByDriver(databaseName, serverEncoding))
            {
                return DatabaseTargetPreparationResult.Failure(
                    WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
            }

            var existingOwner =
                await GetExistingDatabaseOwnerAsync(connection, databaseName, cancellationToken)
                    .ConfigureAwait(false);
            if (existingOwner is not null)
            {
                return string.Equals(existingOwner, ownerName, StringComparison.Ordinal)
                    ? DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.AlreadyExists)
                    : DatabaseTargetPreparationResult.Failure(
                        WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict);
            }

            if (!await IsExistingRoleSupportedByDriverAsync(
                    connection, ownerName, serverEncoding, cancellationToken).ConfigureAwait(false))
            {
                return DatabaseTargetPreparationResult.Failure(
                    WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
            }

            if (afterMissingTargetObserved is not null)
            {
                await afterMissingTargetObserved(cancellationToken).ConfigureAwait(false);
            }

            await using (var createCommand = connection.CreateCommand())
            {
                createCommand.CommandText =
                    $"CREATE DATABASE {QuoteIdentifier(databaseName)} OWNER {QuoteIdentifier(ownerName)}";

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
                    // (42P04). Both only mean the target now exists; whether this call can treat it
                    // as ready still depends on who owns it.
                    var raceOwner = await GetExistingDatabaseOwnerAsync(
                            connection, databaseName, cancellationToken).ConfigureAwait(false);

                    return string.Equals(raceOwner, ownerName, StringComparison.Ordinal)
                        ? DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.AlreadyExists)
                        : DatabaseTargetPreparationResult.Failure(
                            WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict);
                }
            }
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

    private static async ValueTask<string?> GetExistingDatabaseOwnerAsync(
        NpgsqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText =
            "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name";
        var parameter = checkCommand.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = databaseName;
        checkCommand.Parameters.Add(parameter);

        var owner = await checkCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return owner?.ToString();
    }

    private static async ValueTask<string> GetServerEncodingAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_setting('server_encoding')";

        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Npgsql writes the startup-packet database and user names as UTF-8, and PostgreSQL silently
    /// truncates them at its NAMEDATALEN limit. The server stores identifiers converted from the
    /// client encoding into server_encoding but matches the incoming startup name by raw bytes, so
    /// an identifier is only reachable end-to-end when its UTF-8 form fits the identifier limit
    /// and is byte-identical to its stored form: either the server stores UTF-8, or the identifier
    /// is pure ASCII. Anything else would let preparation succeed while the application can never
    /// connect to the result.
    /// </summary>
    private static bool IsIdentitySupportedByDriver(string identifier, string serverEncoding)
    {
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(identifier);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        return byteCount <= MaximumIdentifierBytes &&
            (string.Equals(serverEncoding, "UTF8", StringComparison.OrdinalIgnoreCase) || IsAscii(identifier));
    }

    private static bool IsAscii(string value) =>
        !value.Any(character => character > 127);

    private static async ValueTask<bool> IsExistingRoleSupportedByDriverAsync(
        NpgsqlConnection connection,
        string roleName,
        string serverEncoding,
        CancellationToken cancellationToken)
    {
        if (!IsIdentitySupportedByDriver(roleName, serverEncoding))
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @name)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = roleName;
        command.Parameters.Add(parameter);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool IsConcurrentCreationRace(PostgresException exception) =>
        string.Equals(exception.SqlState, PostgresErrorCodes.DuplicateDatabase, StringComparison.Ordinal) ||
        (string.Equals(exception.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal) &&
         string.Equals(exception.ConstraintName, "pg_database_datname_index", StringComparison.Ordinal));

    internal static string ClassifyFailure(Exception exception)
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
            PostgreSqlProbeOutcome.AuthenticationFailed => WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
            PostgreSqlProbeOutcome.TargetAccessDenied => WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
            PostgreSqlProbeOutcome.TargetIdentityMismatch => WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget,
            PostgreSqlProbeOutcome.ConnectionFailed => WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            PostgreSqlProbeOutcome.DatabaseNotFound => WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            _ => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed
        };
    }
}
