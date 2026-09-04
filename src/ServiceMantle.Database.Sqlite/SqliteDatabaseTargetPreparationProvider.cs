using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.Sqlite;

/// <summary>
/// Observes and explicitly prepares local SQLite file targets without overwriting an existing
/// file. The same validated canonical path supplies a privacy-preserving single-instance target
/// identity; no distributed migration lock or deployment gate is provided.
/// </summary>
/// <remarks>
/// Only fully qualified local ordinary-file paths are supported. Symbolic links, reparse points,
/// hard links, custom VFS implementations, shared cache, read-only/memory modes, URI filenames,
/// and encrypted connection strings fail closed. Observation uses a reconstructed read-only,
/// private-cache, non-pooled connection and never probes writability. Preparation initializes a
/// unique same-directory temporary database and publishes it atomically without replacement.
/// External replacement, network filesystem semantics, process termination, and cross-process
/// exclusion are outside this contract.
/// </remarks>
public sealed class SqliteDatabaseTargetPreparationProvider :
    IDatabaseTargetPreparationProvider,
    IDatabaseDeploymentCapabilityProvider
{
    private static readonly TimeSpan MaximumPreparationTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1D);

    private readonly ISqliteTargetFileSystem fileSystem;
    private readonly ISqliteDatabaseAccess databaseAccess;
    private readonly Func<SqlitePreparationCheckpoint, CancellationToken, ValueTask>? checkpoint;

    /// <summary>Initializes the SQLite provider with local filesystem and database access.</summary>
    public SqliteDatabaseTargetPreparationProvider()
        : this(new SqliteTargetFileSystem(), new SqliteDatabaseAccess())
    {
    }

    internal SqliteDatabaseTargetPreparationProvider(
        ISqliteTargetFileSystem fileSystem,
        ISqliteDatabaseAccess databaseAccess,
        Func<SqlitePreparationCheckpoint, CancellationToken, ValueTask>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(databaseAccess);
        this.fileSystem = fileSystem;
        this.databaseAccess = databaseAccess;
        this.checkpoint = checkpoint;
    }

    /// <summary>Gets the canonical SQLite provider identifier.</summary>
    public string ProviderId => WellKnownDatabaseProviderIds.Sqlite;

    /// <summary>Gets the local-file target kind.</summary>
    public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.File;

    /// <summary>Gets the explicit single-instance-only deployment declaration.</summary>
    public DatabaseDeploymentCapability Capability { get; } = new(
        WellKnownDatabaseProviderIds.Sqlite,
        DatabaseDeploymentSupport.SingleInstanceOnly);

    /// <summary>
    /// Observes a validated local SQLite file without creating the file, parent directories,
    /// temporary probes, journals, WAL files, or shared-memory files.
    /// </summary>
    public async ValueTask<DatabaseTargetObservation> ObserveAsync(
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSqliteProvider(target.Provider))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!SqliteFileTarget.TryParse(target.ConnectionString, out var path))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        try
        {
            var inspection = fileSystem.Inspect(path);
            cancellationToken.ThrowIfCancellationRequested();
            return inspection.Status switch
            {
                SqlitePathInspectionStatus.ExistingFile => await ObserveExistingAsync(
                    inspection.CanonicalPath!, cancellationToken).ConfigureAwait(false),
                SqlitePathInspectionStatus.MissingFile => MissingObservation(
                    inspection.CanonicalPath!, cancellationToken),
                SqlitePathInspectionStatus.ParentMissing or SqlitePathInspectionStatus.InvalidTarget =>
                    DatabaseTargetObservation.ServerUnreachable(
                        WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget),
                SqlitePathInspectionStatus.PermissionDenied => DatabaseTargetObservation.ServerUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied),
                _ => DatabaseTargetObservation.ServerUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported)
            };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw SafeCancellation(cancellationToken);
        }
        catch (Exception)
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }
    }

    /// <summary>
    /// Explicitly creates a missing SQLite file from a same-directory temporary database and
    /// publishes it without replacement. Existing targets are opened read-only and returned as
    /// <see cref="DatabaseTargetPreparationOutcome.AlreadyExists"/> only when connectable.
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

        if (!IsSqliteProvider(request.Target.Provider))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (request.AdministrativeConnectionString is not null)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        if (!SqliteFileTarget.TryParse(request.Target.ConnectionString, out var path))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var operationToken = linkedSource.Token;
        string? temporaryPath = null;
        var published = false;

        try
        {
            var inspection = fileSystem.Inspect(path);
            cancellationToken.ThrowIfCancellationRequested();
            operationToken.ThrowIfCancellationRequested();
            if (inspection.Status == SqlitePathInspectionStatus.ExistingFile)
            {
                return await ExistingPreparationResultAsync(
                    inspection.CanonicalPath!, operationToken).ConfigureAwait(false);
            }

            var failure = PathFailure(inspection.Status);
            if (failure is not null)
            {
                return failure;
            }

            var canonicalPath = inspection.CanonicalPath!;
            var sidecars = fileSystem.InspectSidecars(canonicalPath);
            cancellationToken.ThrowIfCancellationRequested();
            operationToken.ThrowIfCancellationRequested();
            if (sidecars != SqliteSidecarInspectionStatus.None)
            {
                return DatabaseTargetPreparationResult.Failure(sidecars switch
                {
                    SqliteSidecarInspectionStatus.Present =>
                        WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict,
                    SqliteSidecarInspectionStatus.PermissionDenied =>
                        WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                    _ => WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported
                });
            }

            temporaryPath = fileSystem.CreateTemporaryFile(canonicalPath);
            cancellationToken.ThrowIfCancellationRequested();
            operationToken.ThrowIfCancellationRequested();
            await InvokeCheckpointAsync(
                SqlitePreparationCheckpoint.TemporaryFileCreated,
                operationToken).ConfigureAwait(false);
            await databaseAccess.InitializeAsync(temporaryPath, operationToken).ConfigureAwait(false);
            await InvokeCheckpointAsync(
                SqlitePreparationCheckpoint.BeforePublish,
                operationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            operationToken.ThrowIfCancellationRequested();

            var publish = fileSystem.Publish(temporaryPath, canonicalPath);
            if (publish == SqlitePublishStatus.TargetExists)
            {
                return await ObservePublishWinnerAsync(canonicalPath, operationToken).ConfigureAwait(false);
            }

            if (publish != SqlitePublishStatus.Published)
            {
                return DatabaseTargetPreparationResult.Failure(
                    publish switch
                    {
                        SqlitePublishStatus.PermissionDenied =>
                            WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                        SqlitePublishStatus.CapabilityNotSupported =>
                            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported,
                        _ => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed
                    });
            }

            published = true;
            await InvokeCheckpointAsync(
                SqlitePreparationCheckpoint.AfterPublish,
                operationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            operationToken.ThrowIfCancellationRequested();
            return DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw SafeCancellation(cancellationToken);
        }
        catch (Exception) when (timeoutSource.IsCancellationRequested)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.Timeout);
        }
        catch (UnauthorizedAccessException)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied);
        }
        catch (SqliteException exception)
        {
            return DatabaseTargetPreparationResult.Failure(ClassifySqliteFailure(exception));
        }
        catch (Exception)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
        }
        finally
        {
            if (!published && temporaryPath is not null)
            {
                fileSystem.DeleteTemporaryFile(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Returns a stable, path-derived identity for process-local single-instance serialization.
    /// The returned value is a SHA-256 digest and does not contain the connection string or path.
    /// This method performs no SQLite I/O and never creates a file.
    /// </summary>
    public ValueTask<string> GetCanonicalTargetIdentityAsync(
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSqliteProvider(target.Provider) ||
            !SqliteFileTarget.TryParse(target.ConnectionString, out var path))
        {
            throw new InvalidOperationException("The SQLite target identity could not be resolved.");
        }

        var inspection = fileSystem.Inspect(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (inspection.Status is not (
            SqlitePathInspectionStatus.ExistingFile or SqlitePathInspectionStatus.MissingFile))
        {
            throw new InvalidOperationException("The SQLite target identity could not be resolved.");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(inspection.CanonicalPath!));
        return ValueTask.FromResult("sqlite-file-sha256:" + Convert.ToHexString(digest));
    }

    private DatabaseTargetObservation MissingObservation(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        var sidecars = fileSystem.InspectSidecars(canonicalPath);
        cancellationToken.ThrowIfCancellationRequested();
        return sidecars switch
        {
            SqliteSidecarInspectionStatus.None => DatabaseTargetObservation.TargetMissing(),
            SqliteSidecarInspectionStatus.Present => DatabaseTargetObservation.TargetUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict),
            SqliteSidecarInspectionStatus.PermissionDenied => DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied),
            _ => DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported)
        };
    }

    private async ValueTask<DatabaseTargetObservation> ObserveExistingAsync(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        var sidecars = fileSystem.InspectSidecars(canonicalPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (sidecars != SqliteSidecarInspectionStatus.None)
        {
            return sidecars switch
            {
                SqliteSidecarInspectionStatus.Present => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict,
                    targetExists: true),
                SqliteSidecarInspectionStatus.PermissionDenied => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                    targetExists: true),
                _ => DatabaseTargetObservation.TargetUnreachable(
                    WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported,
                    targetExists: true)
            };
        }

        var status = await databaseAccess.InspectAsync(canonicalPath, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return status switch
        {
            SqliteDatabaseInspectionStatus.Connectable => DatabaseTargetObservation.TargetConnectable(),
            SqliteDatabaseInspectionStatus.PermissionDenied => DatabaseTargetObservation.TargetUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                targetExists: true),
            SqliteDatabaseInspectionStatus.TargetConflict => DatabaseTargetObservation.TargetUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict,
                targetExists: true),
            _ => DatabaseTargetObservation.TargetUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
                targetExists: true)
        };
    }

    private async ValueTask<DatabaseTargetPreparationResult> ExistingPreparationResultAsync(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        var observation = await ObserveExistingAsync(canonicalPath, cancellationToken)
            .ConfigureAwait(false);
        return observation.Status == DatabaseTargetObservationStatus.TargetConnectable
            ? DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.AlreadyExists)
            : DatabaseTargetPreparationResult.Failure(
                observation.ErrorCode ?? WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
    }

    private async ValueTask<DatabaseTargetPreparationResult> ObservePublishWinnerAsync(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        var inspection = fileSystem.Inspect(canonicalPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (inspection.Status != SqlitePathInspectionStatus.ExistingFile)
        {
            return DatabaseTargetPreparationResult.Failure(
                inspection.Status == SqlitePathInspectionStatus.PermissionDenied
                    ? WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied
                    : WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict);
        }

        return await ExistingPreparationResultAsync(
            inspection.CanonicalPath!, cancellationToken).ConfigureAwait(false);
    }

    private static DatabaseTargetPreparationResult? PathFailure(SqlitePathInspectionStatus status) =>
        status switch
        {
            SqlitePathInspectionStatus.MissingFile => null,
            SqlitePathInspectionStatus.ParentMissing or SqlitePathInspectionStatus.InvalidTarget =>
                DatabaseTargetPreparationResult.Failure(
                    WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget),
            SqlitePathInspectionStatus.PermissionDenied => DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied),
            _ => DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported)
        };

    private ValueTask InvokeCheckpointAsync(
        SqlitePreparationCheckpoint value,
        CancellationToken cancellationToken) =>
        checkpoint?.Invoke(value, cancellationToken) ?? ValueTask.CompletedTask;

    private static string ClassifySqliteFailure(SqliteException exception) =>
        exception.SqliteErrorCode switch
        {
            3 or 14 => WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
            8 => WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict,
            _ => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed
        };

    private static bool IsSqliteProvider(string provider) =>
        string.Equals(provider, WellKnownDatabaseProviderIds.Sqlite, StringComparison.OrdinalIgnoreCase);

    private static OperationCanceledException SafeCancellation(CancellationToken cancellationToken) =>
        new("SQLite target preparation was cancelled by the caller.", cancellationToken);
}

internal enum SqlitePreparationCheckpoint
{
    TemporaryFileCreated,
    BeforePublish,
    AfterPublish
}

internal enum SqliteDatabaseInspectionStatus
{
    Connectable,
    PermissionDenied,
    TargetConflict,
    ConnectionFailed
}

internal interface ISqliteDatabaseAccess
{
    ValueTask<SqliteDatabaseInspectionStatus> InspectAsync(
        string canonicalPath,
        CancellationToken cancellationToken);

    ValueTask InitializeAsync(string temporaryPath, CancellationToken cancellationToken);
}

internal sealed class SqliteDatabaseAccess : ISqliteDatabaseAccess
{
    public async ValueTask<SqliteDatabaseInspectionStatus> InspectAsync(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(BuildConnectionString(
                canonicalPath,
                SqliteOpenMode.ReadOnly));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_schema";
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return SqliteDatabaseInspectionStatus.Connectable;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return SqliteDatabaseInspectionStatus.PermissionDenied;
        }
        catch (SqliteException exception)
        {
            return exception.SqliteErrorCode switch
            {
                3 or 14 => SqliteDatabaseInspectionStatus.PermissionDenied,
                8 => SqliteDatabaseInspectionStatus.TargetConflict,
                _ => SqliteDatabaseInspectionStatus.ConnectionFailed
            };
        }
        catch (Exception)
        {
            return SqliteDatabaseInspectionStatus.ConnectionFailed;
        }
    }

    public async ValueTask InitializeAsync(
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(
            temporaryPath,
            SqliteOpenMode.ReadWrite));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version = 0";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ConnectionString;
}
