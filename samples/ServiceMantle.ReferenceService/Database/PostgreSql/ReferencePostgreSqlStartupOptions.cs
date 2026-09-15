using Npgsql;
using ServiceMantle.Installation;

namespace ServiceMantle.ReferenceService.Database.PostgreSql;

/// <summary>
/// The consumer's explicit, fixed inputs for the opt-in PostgreSQL startup deployment gate.
/// </summary>
/// <remarks>
/// <para>
/// Every input is explicit and is read before <c>Build</c>. Omitting a value or holding a
/// privileged account is never read as authorization: the target connection string has to be
/// stated, and only an explicit <see cref="PrepareIfMissingKey"/> of <c>true</c> makes the
/// administrative connection string required at all. An unusable input fails before any
/// provider, network, or EF call happens, and a failure names the setting, never the value it
/// read.
/// </para>
/// <para>
/// The administrative connection string is carried only for the single preparation call the
/// gate makes. It is never handed to the runtime <c>DbContext</c>, the migration lock, or the
/// inspection connection - those all use the target connection string.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlStartupOptions
{
    /// <summary>The explicit activation switch. Defaults to <see langword="false"/>.</summary>
    public const string EnabledKey = "ReferenceService:PostgreSqlStartup:Enabled";

    /// <summary>The target runtime connection. Required when the gate is enabled.</summary>
    public const string ConnectionStringKey = "ReferenceService:PostgreSqlStartup:ConnectionString";

    /// <summary>The explicit permission to create a missing target. Defaults to <see langword="false"/>.</summary>
    public const string PrepareIfMissingKey = "ReferenceService:PostgreSqlStartup:PrepareIfMissing";

    /// <summary>
    /// The administrative connection for one preparation call. Required only when
    /// <see cref="PrepareIfMissingKey"/> is <c>true</c>; not read otherwise.
    /// </summary>
    public const string AdministrativeConnectionStringKey =
        "ReferenceService:PostgreSqlStartup:AdministrativeConnectionString";

    private ReferencePostgreSqlStartupOptions(
        string targetConnectionString,
        bool prepareIfMissing,
        string? administrativeConnectionString)
    {
        TargetConnectionString = targetConnectionString;
        PrepareIfMissing = prepareIfMissing;
        AdministrativeConnectionString = administrativeConnectionString;
    }

    /// <summary>Gets the target runtime connection the whole gate shares.</summary>
    public string TargetConnectionString { get; }

    /// <summary>Gets whether a missing target may be created by an explicit preparation.</summary>
    public bool PrepareIfMissing { get; }

    /// <summary>
    /// Gets the administrative connection for the one preparation call, or null when preparation
    /// was not permitted. Non-null exactly when <see cref="PrepareIfMissing"/> is <see langword="true"/>.
    /// </summary>
    public string? AdministrativeConnectionString { get; }

    /// <summary>
    /// Gets the fixed budget for the preparation call. It bounds neither the migration itself nor
    /// total startup.
    /// </summary>
    public static TimeSpan PreparationBudget => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the fixed budget for acquiring the migration lock. It bounds neither the migration
    /// execution nor total startup.
    /// </summary>
    public static TimeSpan LockAcquireBudget => TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads the explicit inputs, or returns <see langword="null"/> when the gate is not switched on.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The gate is on and an input is missing or unusable. The message names the setting only.
    /// </exception>
    public static ReferencePostgreSqlStartupOptions? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!bool.TryParse(configuration[EnabledKey], out var enabled) || !enabled)
        {
            return null;
        }

        var prepareValue = configuration[PrepareIfMissingKey];
        var prepareIfMissing = false;
        if (prepareValue is not null && !bool.TryParse(prepareValue, out prepareIfMissing))
        {
            throw Invalid(PrepareIfMissingKey);
        }

        var targetConnectionString = configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(targetConnectionString) ||
            !IsUsableConnectionString(targetConnectionString))
        {
            throw Invalid(ConnectionStringKey);
        }

        // The administrative connection is not even read when preparation was not permitted.
        string? administrativeConnectionString = null;
        if (prepareIfMissing)
        {
            var administrative = configuration[AdministrativeConnectionStringKey];
            if (string.IsNullOrWhiteSpace(administrative) ||
                !IsUsableConnectionString(administrative))
            {
                throw Invalid(AdministrativeConnectionStringKey);
            }

            administrativeConnectionString = administrative.Trim();
        }

        return new ReferencePostgreSqlStartupOptions(
            targetConnectionString.Trim(),
            prepareIfMissing,
            administrativeConnectionString);
    }

    private static bool IsUsableConnectionString(string value)
    {
        try
        {
            _ = new NpgsqlConnectionStringBuilder(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static InvalidOperationException Invalid(string key) => new(
        $"The reference PostgreSQL startup deployment requires an explicit, usable '{key}'. " +
        "The configured value is not echoed.");
}

/// <summary>The finite outcome of the sample's PostgreSQL startup deployment gate.</summary>
public enum ReferencePostgreSqlStartupOutcome
{
    /// <summary>The target is absent and creating it was not explicitly permitted.</summary>
    TargetMissing,

    /// <summary>The target could not be observed, or was observed as unusable.</summary>
    TargetUnavailable,

    /// <summary>The explicit preparation of the missing target failed.</summary>
    PreparationFailed,

    /// <summary>The migration lock was not acquired or its lease was lost.</summary>
    LockUnavailable,

    /// <summary>The database schema version is newer than the current application version.</summary>
    VersionTooNew,

    /// <summary>Migration orchestration did not report success, or the migration scope failed to release.</summary>
    MigrationFailed,

    /// <summary>No installation row exists for this service. Never created or repaired here.</summary>
    InstallationStateMissing,

    /// <summary>The installation row exists but could not be read into a valid state.</summary>
    InstallationStateInvalid,

    /// <summary>The target is migrated and its installation row resolves a startup phase.</summary>
    Ready,
}

/// <summary>
/// The safe result of the startup gate. It carries a finite outcome, whether the consumer's
/// migration executor ran, and - only when ready - the resolved startup phase. Never a
/// connection string, a password, or a provider message.
/// </summary>
public sealed class ReferencePostgreSqlStartupResult
{
    /// <summary>Creates a result without a startup phase.</summary>
    public ReferencePostgreSqlStartupResult(
        ReferencePostgreSqlStartupOutcome outcome,
        bool executorWasCalled)
        : this(outcome, executorWasCalled, phase: null)
    {
    }

    /// <summary>Creates a result, carrying a startup phase only for a ready outcome.</summary>
    public ReferencePostgreSqlStartupResult(
        ReferencePostgreSqlStartupOutcome outcome,
        bool executorWasCalled,
        ServiceStartupPhase? phase)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (phase is not null && !Enum.IsDefined(phase.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (outcome == ReferencePostgreSqlStartupOutcome.Ready && phase is null)
        {
            throw new ArgumentException(
                "A ready outcome requires the resolved startup phase.",
                nameof(phase));
        }

        if (outcome != ReferencePostgreSqlStartupOutcome.Ready && phase is not null)
        {
            throw new ArgumentException(
                "Only a ready outcome carries a startup phase.",
                nameof(phase));
        }

        Outcome = outcome;
        ExecutorWasCalled = executorWasCalled;
        ServiceStartupPhase = phase;
    }

    /// <summary>Gets the finite outcome.</summary>
    public ReferencePostgreSqlStartupOutcome Outcome { get; }

    /// <summary>Gets whether the consumer's migration executor was invoked.</summary>
    public bool ExecutorWasCalled { get; }

    /// <summary>Gets the resolved startup phase, or null before the outcome is ready.</summary>
    public ServiceStartupPhase? ServiceStartupPhase { get; }

    /// <summary>Gets whether the host may finish starting.</summary>
    public bool IsReady => Outcome == ReferencePostgreSqlStartupOutcome.Ready;

    /// <summary>Returns only the finite outcome and phase.</summary>
    public override string ToString() =>
        $"ReferencePostgreSqlStartupResult(Outcome={Outcome}, ExecutorWasCalled={ExecutorWasCalled}, " +
        $"ServiceStartupPhase={ServiceStartupPhase})";
}
