using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Migration;

namespace ServiceMantle.Persistence.Relational;

/// <summary>
/// Selects how strongly <see cref="EfCoreHealthSnapshotSource{TDbContext}"/> probes the database
/// once the startup receipt succeeded.
/// </summary>
public enum EfCoreHealthSnapshotProbeMode
{
    /// <summary>
    /// Opens the scoped context's connection and closes it again. Proves reachability only; a
    /// dropped table or column is not observed.
    /// </summary>
    ConnectionOnly = 0,

    /// <summary>
    /// Opens the connection, then runs one zero-row <c>SELECT</c> over exactly the tables and
    /// columns the current EF model maps, and closes the connection. Proves the mapped schema is
    /// readable; proves nothing about constraints, indexes, triggers, or data.
    /// </summary>
    MappedSchema = 1
}

/// <summary>
/// The generic EF Core health snapshot source for the ServiceMantle health endpoints: it reads
/// the process-local <see cref="StartupDatabaseReceipt"/> and, once that receipt succeeded,
/// probes the current scope's <typeparamref name="TDbContext"/> database read-only.
/// </summary>
/// <typeparam name="TDbContext">The consuming business DbContext type.</typeparam>
/// <remarks>
/// <para>
/// Read-only by contract: the probe never runs DDL, never tracks or reads entities, never writes
/// data, and never triggers installation or migration. It combines two facts:
/// <list type="number">
/// <item>The process-local <see cref="StartupDatabaseReceipt"/>. While the receipt has not
/// reached <see cref="ServiceMigrationReadinessState.Succeeded"/>, no database access happens at
/// all and the snapshot reports the non-ready phase with the receipt's own state.</item>
/// <item>A cancellation-aware read-only probe of the scoped context's database, in the strength
/// selected by <see cref="EfCoreHealthSnapshotProbeMode"/>. Table and column names come
/// exclusively from the compiled EF model — never from request input — and no row data is read
/// or returned.</item>
/// </list>
/// </para>
/// <para>
/// Failure mapping (all value-free; the error code is the configured prefix plus one fixed
/// suffix): receipt not succeeded →
/// <c>(PendingSetup, receipt state, Unreachable, {prefix}.startup_incomplete)</c> or, when the
/// receipt failed, <c>(PendingSetup, Failed, Unreachable, {prefix}.startup_failed)</c>;
/// classifier-reported connection failure →
/// <c>(Completed, Succeeded, Unreachable, {prefix}.database_unreachable)</c>;
/// classifier-reported schema regression →
/// <c>(Completed, Failed, Reachable, {prefix}.schema_unavailable)</c>; unclassified exceptions
/// propagate so the health endpoints answer with their own safe error codes. Cancellation — the
/// caller's own token or the endpoint's probe budget — propagates as
/// <see cref="OperationCanceledException"/> on the received token and is never reported as
/// <c>Unreachable</c>. Every request re-samples; nothing is cached.
/// </para>
/// <para>
/// Non-guarantees: the mapped-schema probe only proves that the tables and columns mapped by the
/// current EF model are readable — not constraints, indexes, triggers, or data correctness; a
/// hung database is bounded by the health endpoint's probe budget, not by this source; the
/// source logs nothing and never puts connection strings, host names, or raw exception text in
/// its output.
/// </para>
/// </remarks>
public sealed class EfCoreHealthSnapshotSource<TDbContext> : IServiceHealthSnapshotSource
    where TDbContext : DbContext
{
    private const string StartupIncompleteCodeSuffix = ".startup_incomplete";
    private const string StartupFailedCodeSuffix = ".startup_failed";
    private const string DatabaseUnreachableCodeSuffix = ".database_unreachable";
    private const string SchemaUnavailableCodeSuffix = ".schema_unavailable";

    // The longest suffix, ".database_unreachable" (21 characters), plus a prefix of at most 107
    // characters stays within the snapshot contract's 128-character error-code bound.
    internal const int MaximumErrorCodePrefixLength = 107;

    private readonly StartupDatabaseReceipt startupReceipt;
    private readonly TDbContext dbContext;
    private readonly IServiceDatabaseProbeFailureClassifier failureClassifier;
    private readonly string errorCodePrefix;
    private readonly EfCoreHealthSnapshotProbeMode probeMode;

    /// <summary>Initializes the snapshot source with its fixed inputs.</summary>
    /// <param name="startupReceipt">The startup gate's process-local receipt singleton.</param>
    /// <param name="dbContext">The scoped business context whose database is probed.</param>
    /// <param name="failureClassifier">
    /// The provider-specific classification seam; see
    /// <see cref="IServiceDatabaseProbeFailureClassifier"/> for its contract.
    /// </param>
    /// <param name="errorCodePrefix">
    /// The safe error-code prefix; must contain 1–107 ASCII letters, digits, '.', '_', or '-'
    /// and start with an ASCII letter or digit, so every projected code stays within the
    /// snapshot contract's 128-character bound.
    /// </param>
    /// <param name="probeMode">The probe strength.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="errorCodePrefix"/> is not a safe
    /// error-code prefix.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="probeMode"/> is not a
    /// defined probe mode.</exception>
    public EfCoreHealthSnapshotSource(
        StartupDatabaseReceipt startupReceipt,
        TDbContext dbContext,
        IServiceDatabaseProbeFailureClassifier failureClassifier,
        string errorCodePrefix,
        EfCoreHealthSnapshotProbeMode probeMode = EfCoreHealthSnapshotProbeMode.MappedSchema)
    {
        ArgumentNullException.ThrowIfNull(startupReceipt);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(failureClassifier);
        ValidateErrorCodePrefix(errorCodePrefix);
        if (!Enum.IsDefined(probeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(probeMode));
        }

        this.startupReceipt = startupReceipt;
        this.dbContext = dbContext;
        this.failureClassifier = failureClassifier;
        this.errorCodePrefix = errorCodePrefix;
        this.probeMode = probeMode;
    }

    /// <summary>Reads one immutable snapshot for the current request.</summary>
    /// <param name="cancellationToken">
    /// The caller's cancellation token; the health endpoints pass their probe budget through it.
    /// </param>
    public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Receipt first: while the startup gate has not succeeded, the database is not probed at
        // all. NotStarted/Running report an incomplete startup; a failed gate keeps its failure
        // state so readiness fails closed even if the host kept serving.
        var receiptState = startupReceipt.State;
        if (receiptState != ServiceMigrationReadinessState.Succeeded)
        {
            return new ServiceHealthSnapshot(
                ServiceStartupPhase.PendingSetup,
                receiptState,
                ServiceDatabaseReadinessState.Unreachable,
                receiptState == ServiceMigrationReadinessState.Failed
                    ? errorCodePrefix + StartupFailedCodeSuffix
                    : errorCodePrefix + StartupIncompleteCodeSuffix);
        }

        // Opening the connection through EF keeps the scoped context the single owner of the
        // connection lifetime; the symmetric close below returns it to the caller's disposal.
        try
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return HandleProbeFailure(exception, cancellationToken);
        }

        try
        {
            if (probeMode == EfCoreHealthSnapshotProbeMode.MappedSchema)
            {
                var connection = dbContext.Database.GetDbConnection();
                var sqlGenerationHelper = dbContext.GetService<ISqlGenerationHelper>();
                foreach (var probeSql in BuildMappedSchemaProbes(sqlGenerationHelper))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = probeSql;
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                ServiceMigrationReadinessState.Succeeded,
                ServiceDatabaseReadinessState.Reachable);
        }
        catch (Exception exception)
        {
            return HandleProbeFailure(exception, cancellationToken);
        }
        finally
        {
            // The snapshot decision is already determined here, so closing must not be able to
            // replace it: cleanup errors are suppressed rather than reported or logged (the
            // close API takes no token, so no cancellation can surface here either).
            try
            {
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Suppressed: the scoped context still disposes the connection it owns.
            }
        }
    }

    internal static void ValidateErrorCodePrefix(string errorCodePrefix)
    {
        if (errorCodePrefix is null ||
            errorCodePrefix.Length is < 1 or > MaximumErrorCodePrefixLength ||
            !IsAsciiLetterOrDigit(errorCodePrefix[0]))
        {
            throw InvalidPrefix();
        }

        foreach (var character in errorCodePrefix)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw InvalidPrefix();
            }
        }
    }

    /// <summary>
    /// Turns one probe exception into the fixed snapshot mapping. Cancellation always
    /// propagates on the received token — before, during, or wrapped by the provider — and an
    /// unclassified exception propagates unchanged so the health endpoints answer with their
    /// own safe error code instead of an invented state.
    /// </summary>
    private ServiceHealthSnapshot HandleProbeFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException operationCanceledException)
        {
            // Cancellation always propagates, whichever token it carries.
            ExceptionDispatchInfo.Throw(operationCanceledException);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Drivers can wrap a cancelled attempt in a provider exception; anything observed
            // while the received token is already cancelled is cancellation, not a
            // reachability fact.
            cancellationToken.ThrowIfCancellationRequested();
        }

        var failureKind = failureClassifier.Classify(exception);
        switch (failureKind)
        {
            case ServiceDatabaseProbeFailureKind.ConnectionFailure:
                return new ServiceHealthSnapshot(
                    ServiceStartupPhase.Completed,
                    ServiceMigrationReadinessState.Succeeded,
                    ServiceDatabaseReadinessState.Unreachable,
                    errorCodePrefix + DatabaseUnreachableCodeSuffix);

            case ServiceDatabaseProbeFailureKind.SchemaUnreadable:
                return new ServiceHealthSnapshot(
                    ServiceStartupPhase.Completed,
                    ServiceMigrationReadinessState.Failed,
                    ServiceDatabaseReadinessState.Reachable,
                    errorCodePrefix + SchemaUnavailableCodeSuffix);

            default:
                ExceptionDispatchInfo.Throw(exception);
                return null!;
        }
    }

    /// <summary>
    /// Builds one zero-row SELECT per EF-mapped table, listing exactly the columns the current
    /// compiled model maps. The EF model is the only accepted source of table and column names;
    /// identifiers are delimited by the provider's own SQL generation helper.
    /// </summary>
    private IEnumerable<string> BuildMappedSchemaProbes(ISqlGenerationHelper sqlGenerationHelper)
    {
        foreach (var entityType in dbContext.Model.GetEntityTypes())
        {
            if (StoreObjectIdentifier.Create(entityType, StoreObjectType.Table) is not { } table)
            {
                continue;
            }

            var columns = entityType.GetProperties()
                .Select(property => property.GetColumnName(table))
                .Where(column => !string.IsNullOrEmpty(column))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // A table with no mapped columns has nothing readable to verify; an empty SELECT
            // list would not be valid SQL.
            if (columns.Count == 0)
            {
                continue;
            }

            var delimitedColumns = string.Join(
                ", ",
                columns.Select(column => sqlGenerationHelper.DelimitIdentifier(column)));
            var delimitedTable = table.Schema is null
                ? sqlGenerationHelper.DelimitIdentifier(table.Name)
                : sqlGenerationHelper.DelimitIdentifier(table.Schema, table.Name);

            // WHERE 1 = 0 is the zero-row predicate every supported provider parses; the
            // statement still binds the table and every listed column.
            yield return $"SELECT {delimitedColumns} FROM {delimitedTable} WHERE 1 = 0";
        }
    }

    private static ArgumentException InvalidPrefix() => new(
        "The error code prefix must contain 1 to 107 ASCII letters, digits, '.', '_', or '-', " +
        "and start with an ASCII letter or digit.",
        nameof(errorCodePrefix));

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
