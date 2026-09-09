using Oracle.ManagedDataAccess.Client;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.Oracle;

/// <summary>
/// Observes an Oracle local PDB user and explicitly prepares the same-named schema without
/// modifying any pre-existing user.
/// </summary>
public sealed class OracleDatabaseTargetPreparationProvider : IDatabaseTargetPreparationProvider
{
    private static readonly TimeSpan MaximumPreparationTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1D);

    private readonly IOracleDatabaseOperations operations;

    /// <summary>Initializes the provider with real ODP.NET database operations.</summary>
    public OracleDatabaseTargetPreparationProvider()
        : this(new OracleDatabaseOperations())
    {
    }

    internal OracleDatabaseTargetPreparationProvider(IOracleDatabaseOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = operations;
    }

    /// <summary>Gets the canonical Oracle provider ID.</summary>
    public string ProviderId => WellKnownDatabaseProviderIds.Oracle;

    /// <summary>Gets the Oracle user-owned server-schema target kind.</summary>
    public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerSchema;

    /// <summary>Observes the target user without claiming absence after ambiguous credential failure.</summary>
    public async ValueTask<DatabaseTargetObservation> ObserveAsync(
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsOracleProvider(target.Provider))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        if (!HasSupportedVersion(target.ServerVersion) ||
            !OracleDatabaseTarget.TryBuildConnectionString(target.ConnectionString, out var builder) ||
            !OracleDatabaseTarget.TryGetTargetIdentity(builder, out var userName, out _, out _))
        {
            return DatabaseTargetObservation.ServerUnreachable(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        OracleDatabaseTarget.ApplySafeTimeout(builder);
        try
        {
            var outcome = await operations.ProbeTargetAsync(builder, userName, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return MapObservation(outcome);
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
    /// Creates and grants <c>CREATE SESSION</c> only to a user proved absent in the target PDB.
    /// File-shaped requests are rejected before connection parsing or database operations.
    /// </summary>
    /// <remarks>
    /// Once the underlying database operations have returned or failed, the outcome is resolved at a
    /// single completion checkpoint that applies the fixed precedence of ADR 0001: a permitted
    /// compensation that did not verifiably remove this call's new user, then caller cancellation,
    /// then the caller-provided overall timeout, then the triggered outcome itself. Cancellation or
    /// timeout observed at that checkpoint therefore outranks a successful underlying result. This
    /// method does not promise to reclassify a cancellation that first becomes observable after the
    /// checkpoint, while the result is being returned or the administrative session is being
    /// released, and it neither interrupts an uncooperative driver nor bounds the total elapsed time.
    /// Cancellation and timeout never widen the compensation window: no additional
    /// <c>DROP USER</c>, re-grant, or retry is issued once <c>GRANT CREATE SESSION</c> has been sent.
    /// </remarks>
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

        if (!IsOracleProvider(request.Target.Provider))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch);
        }

        var administrativeConnectionString = request.AdministrativeConnectionString;
        if (administrativeConnectionString is null)
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        if (!HasSupportedVersion(request.Target.ServerVersion) ||
            !TryBuildRequest(
                request,
                administrativeConnectionString,
                out var targetBuilder,
                out var administrativeBuilder,
                out var targetUserName,
                out var targetPassword,
                out var administrativeUserName))
        {
            return DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        return await ExecutePreparationAsync(
                targetBuilder,
                administrativeBuilder,
                targetUserName,
                targetPassword,
                administrativeUserName,
                cancellationToken,
                timeoutSource.Token,
                linkedSource.Token)
            .ConfigureAwait(false);
    }

    private async ValueTask<DatabaseTargetPreparationResult> ExecutePreparationAsync(
        OracleConnectionStringBuilder targetBuilder,
        OracleConnectionStringBuilder administrativeBuilder,
        string targetUserName,
        string targetPassword,
        string administrativeUserName,
        CancellationToken callerToken,
        CancellationToken timeoutToken,
        CancellationToken operationToken)
    {
        IOracleAdministrativeSession? session = null;
        var creationAcknowledged = false;
        var grantIssued = false;
        DatabaseTargetPreparationResult triggeredResult;
        try
        {
            session = await operations.OpenAdministrativeSessionAsync(
                    administrativeBuilder,
                    administrativeUserName,
                    operationToken)
                .ConfigureAwait(false);

            var match = await session.FindUserAsync(targetUserName, operationToken).ConfigureAwait(false);
            if (match == OracleUserMatch.Conflicting)
            {
                triggeredResult = Failure(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict);
            }
            else if (match == OracleUserMatch.Exact)
            {
                triggeredResult = await ResolveExistingAsync(targetBuilder, targetUserName, operationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                triggeredResult = await CreateAndVerifyAsync(session).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            var compensationFailed = creationAcknowledged && !grantIssued &&
                !await TryCompensateAsync(
                        administrativeBuilder,
                        administrativeUserName,
                        targetUserName)
                    .ConfigureAwait(false);

            if (compensationFailed)
            {
                return Failure(WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed);
            }

            if (callerToken.IsCancellationRequested)
            {
                throw SafeCancellation(callerToken);
            }

            if (timeoutToken.IsCancellationRequested)
            {
                return Failure(WellKnownDatabaseTargetPreparationErrorCodes.Timeout);
            }

            return Failure(MapPreparationException(exception));
        }
        finally
        {
            await DisposeSafelyAsync(session).ConfigureAwait(false);
        }

        // Every path that produced a triggered result did so without a permitted, failed
        // compensation, so ADR 0001 leaves caller cancellation ahead of the overall timeout.
        return CompleteTriggeredResult(triggeredResult, callerToken, timeoutToken);

        async ValueTask<DatabaseTargetPreparationResult> CreateAndVerifyAsync(
            IOracleAdministrativeSession openSession)
        {
            try
            {
                await openSession.CreateUserAsync(targetUserName, targetPassword, operationToken)
                    .ConfigureAwait(false);
                creationAcknowledged = true;
            }
            catch (OracleOperationException exception)
                when (exception.Kind == OracleFailureKind.TargetConflict)
            {
                return await ResolveCreateRaceAsync(
                        openSession,
                        targetBuilder,
                        targetUserName,
                        operationToken)
                    .ConfigureAwait(false);
            }

            operationToken.ThrowIfCancellationRequested();
            grantIssued = true;
            await openSession.GrantCreateSessionAsync(targetUserName, operationToken).ConfigureAwait(false);

            var freshOutcome = await operations.ProbeTargetAsync(
                    targetBuilder,
                    targetUserName,
                    operationToken)
                .ConfigureAwait(false);
            return freshOutcome == OracleTargetProbeOutcome.Success
                ? DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created)
                : Failure(MapPreparationProbeFailure(freshOutcome));
        }
    }

    /// <summary>
    /// Applies the ADR 0001 completion precedence to a result the underlying operations returned
    /// normally, so caller cancellation and the overall timeout are not lost when no exception
    /// carried them out of the orchestration.
    /// </summary>
    private static DatabaseTargetPreparationResult CompleteTriggeredResult(
        DatabaseTargetPreparationResult triggeredResult,
        CancellationToken callerToken,
        CancellationToken timeoutToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            throw SafeCancellation(callerToken);
        }

        return timeoutToken.IsCancellationRequested
            ? Failure(WellKnownDatabaseTargetPreparationErrorCodes.Timeout)
            : triggeredResult;
    }

    private async ValueTask<DatabaseTargetPreparationResult> ResolveExistingAsync(
        OracleConnectionStringBuilder targetBuilder,
        string targetUserName,
        CancellationToken cancellationToken)
    {
        var outcome = await operations.ProbeTargetAsync(
                targetBuilder,
                targetUserName,
                cancellationToken)
            .ConfigureAwait(false);
        return outcome == OracleTargetProbeOutcome.Success
            ? DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.AlreadyExists)
            : Failure(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict);
    }

    private async ValueTask<DatabaseTargetPreparationResult> ResolveCreateRaceAsync(
        IOracleAdministrativeSession session,
        OracleConnectionStringBuilder targetBuilder,
        string targetUserName,
        CancellationToken cancellationToken)
    {
        var match = await session.FindUserAsync(targetUserName, cancellationToken).ConfigureAwait(false);
        return match == OracleUserMatch.Exact
            ? await ResolveExistingAsync(targetBuilder, targetUserName, cancellationToken).ConfigureAwait(false)
            : Failure(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict);
    }

    private async ValueTask<bool> TryCompensateAsync(
        OracleConnectionStringBuilder administrativeBuilder,
        string administrativeUserName,
        string targetUserName)
    {
        using var compensationSource = new CancellationTokenSource(OracleDatabaseTarget.CompensationTimeout);
        IOracleAdministrativeSession? compensationSession = null;
        try
        {
            compensationSession = await operations.OpenAdministrativeSessionAsync(
                    administrativeBuilder,
                    administrativeUserName,
                    compensationSource.Token)
                .ConfigureAwait(false);
            var before = await compensationSession.FindUserAsync(targetUserName, compensationSource.Token)
                .ConfigureAwait(false);
            if (before == OracleUserMatch.Missing)
            {
                return true;
            }

            if (before != OracleUserMatch.Exact)
            {
                return false;
            }

            await compensationSession.DropUserAsync(targetUserName, compensationSource.Token)
                .ConfigureAwait(false);
            return await compensationSession.FindUserAsync(targetUserName, compensationSource.Token)
                .ConfigureAwait(false) == OracleUserMatch.Missing;
        }
        catch
        {
            return false;
        }
        finally
        {
            await DisposeSafelyAsync(compensationSession).ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeSafelyAsync(IOracleAdministrativeSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Cleanup cannot replace the already classified primary outcome.
        }
    }

    private static bool TryBuildRequest(
        DatabaseTargetPreparationRequest request,
        string administrativeConnectionString,
        out OracleConnectionStringBuilder targetBuilder,
        out OracleConnectionStringBuilder administrativeBuilder,
        out string targetUserName,
        out string targetPassword,
        out string administrativeUserName)
    {
        targetBuilder = null!;
        administrativeBuilder = null!;
        targetUserName = string.Empty;
        targetPassword = string.Empty;
        administrativeUserName = string.Empty;

        if (!OracleDatabaseTarget.TryBuildConnectionString(request.Target.ConnectionString, out targetBuilder) ||
            !OracleDatabaseTarget.TryGetTargetIdentity(
                targetBuilder,
                out targetUserName,
                out targetPassword,
                out var targetDataSource) ||
            !OracleDatabaseTarget.TryBuildConnectionString(
                administrativeConnectionString,
                out administrativeBuilder) ||
            !OracleDatabaseTarget.TryGetAdministrativeIdentity(
                administrativeBuilder,
                out administrativeUserName,
                out var administrativeDataSource) ||
            !OracleDatabaseTarget.HasSameDataSource(targetDataSource, administrativeDataSource))
        {
            return false;
        }

        OracleDatabaseTarget.ApplySafeTimeout(targetBuilder);
        OracleDatabaseTarget.IsolateAdministrativeConnection(administrativeBuilder);
        return true;
    }

    private static DatabaseTargetObservation MapObservation(OracleTargetProbeOutcome outcome) => outcome switch
    {
        OracleTargetProbeOutcome.Success => DatabaseTargetObservation.TargetConnectable(),
        OracleTargetProbeOutcome.IdentityMismatch or
        OracleTargetProbeOutcome.UnsupportedTopology => DatabaseTargetObservation.TargetUnreachable(
            WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget,
            targetExists: true),
        OracleTargetProbeOutcome.TopologyPermissionDenied or
        OracleTargetProbeOutcome.CreateSessionDenied => DatabaseTargetObservation.TargetUnreachable(
            WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
            targetExists: true),
        OracleTargetProbeOutcome.AccountLocked or
        OracleTargetProbeOutcome.PasswordExpired => DatabaseTargetObservation.TargetUnreachable(
            WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
            targetExists: true),
        OracleTargetProbeOutcome.InvalidCredentials => DatabaseTargetObservation.TargetUnreachable(
            WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed),
        OracleTargetProbeOutcome.ConnectionFailed => DatabaseTargetObservation.ServerUnreachable(
            WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed),
        _ => DatabaseTargetObservation.ServerUnreachable(
            WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed)
    };

    private static string MapPreparationProbeFailure(OracleTargetProbeOutcome outcome) => outcome switch
    {
        OracleTargetProbeOutcome.IdentityMismatch or
        OracleTargetProbeOutcome.UnsupportedTopology =>
            WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget,
        OracleTargetProbeOutcome.TopologyPermissionDenied or
        OracleTargetProbeOutcome.CreateSessionDenied =>
            WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
        OracleTargetProbeOutcome.AccountLocked or
        OracleTargetProbeOutcome.PasswordExpired or
        OracleTargetProbeOutcome.InvalidCredentials =>
            WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict,
        OracleTargetProbeOutcome.ConnectionFailed =>
            WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
        _ => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed
    };

    private static string MapPreparationException(Exception exception) => exception switch
    {
        OracleOperationException { Kind: OracleFailureKind.AuthenticationFailed } =>
            WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed,
        OracleOperationException { Kind: OracleFailureKind.PermissionDenied } =>
            WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
        OracleOperationException { Kind: OracleFailureKind.TargetConflict } =>
            WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict,
        OracleOperationException { Kind: OracleFailureKind.ConnectionFailed } =>
            WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
        OracleOperationException { Kind: OracleFailureKind.InvalidTarget } =>
            WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget,
        _ => WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed
    };

    private static bool HasSupportedVersion(string? version) =>
        OracleDatabaseTarget.TryNormalizeServerVersion(version, out var majorVersion) &&
        majorVersion >= OracleDatabaseTarget.MinimumSupportedServerMajorVersion;

    private static bool IsOracleProvider(string provider) =>
        string.Equals(provider, WellKnownDatabaseProviderIds.Oracle, StringComparison.OrdinalIgnoreCase);

    private static DatabaseTargetPreparationResult Failure(string errorCode) =>
        DatabaseTargetPreparationResult.Failure(errorCode);

    private static OperationCanceledException SafeCancellation(CancellationToken cancellationToken) =>
        new("Oracle database target preparation was cancelled by the caller.", cancellationToken);
}
