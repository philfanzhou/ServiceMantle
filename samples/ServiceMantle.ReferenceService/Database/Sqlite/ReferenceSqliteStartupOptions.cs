using Microsoft.Data.Sqlite;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.ReferenceService.Database.Sqlite;

/// <summary>
/// The consumer's explicit, fixed inputs for the opt-in SQLite startup deployment gate.
/// </summary>
/// <remarks>
/// Every input is explicit. Being the only process on this machine, holding no lock, or simply
/// omitting a value is never read as authorization: the deployment mode has to be stated, and an
/// unusable value fails before any provider, file, or EF call happens. A failure names the setting,
/// never the value it read.
/// </remarks>
public sealed class ReferenceSqliteStartupOptions
{
    /// <summary>The explicit activation switch. Defaults to <see langword="false"/>.</summary>
    public const string EnabledKey = "ReferenceService:SqliteStartup:Enabled";

    /// <summary>The explicit deployment mode. Only <c>SingleInstance</c> is accepted.</summary>
    public const string DeploymentModeKey = "ReferenceService:SqliteStartup:DeploymentMode";

    /// <summary>The explicit permission to create a missing file. Defaults to <see langword="false"/>.</summary>
    public const string PrepareIfMissingKey = "ReferenceService:SqliteStartup:PrepareIfMissing";

    /// <summary>The existing sample database path setting.</summary>
    public const string DatabasePathKey = "ReferenceService:DatabasePath";

    private ReferenceSqliteStartupOptions(string databasePath, bool prepareIfMissing)
    {
        DatabasePath = databasePath;
        PrepareIfMissing = prepareIfMissing;
    }

    /// <summary>Gets the absolute local database file path.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets whether a missing file may be created by an explicit preparation.</summary>
    public bool PrepareIfMissing { get; }

    /// <summary>Gets the only deployment mode this sample accepts.</summary>
    public DatabaseDeploymentMode DeploymentMode => DatabaseDeploymentMode.SingleInstance;

    /// <summary>
    /// Gets the fixed budget for the preparation call and for waiting on the process-local
    /// single-instance turn. It bounds neither the migration itself nor total startup.
    /// </summary>
    public static TimeSpan Budget => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the writable connection the provider and EF share. It is <c>ReadWrite</c>, so EF cannot
    /// create the file implicitly through its default <c>ReadWriteCreate</c> mode.
    /// </summary>
    public string TargetConnectionString => Build(SqliteOpenMode.ReadWrite);

    /// <summary>Gets the read-only connection the inspection uses. It creates and writes nothing.</summary>
    public string InspectionConnectionString => Build(SqliteOpenMode.ReadOnly);

    /// <summary>
    /// Reads the explicit inputs, or returns <see langword="null"/> when the gate is not switched on.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The gate is on and an input is missing or unusable. The message names the setting only.
    /// </exception>
    public static ReferenceSqliteStartupOptions? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!bool.TryParse(configuration[EnabledKey], out var enabled) || !enabled)
        {
            return null;
        }

        if (!Enum.TryParse<DatabaseDeploymentMode>(
                configuration[DeploymentModeKey],
                ignoreCase: true,
                out var mode) ||
            mode != DatabaseDeploymentMode.SingleInstance)
        {
            throw Invalid(DeploymentModeKey);
        }

        var prepareValue = configuration[PrepareIfMissingKey];
        var prepareIfMissing = false;
        if (prepareValue is not null && !bool.TryParse(prepareValue, out prepareIfMissing))
        {
            throw Invalid(PrepareIfMissingKey);
        }

        var databasePath = configuration[DatabasePathKey];
        if (string.IsNullOrWhiteSpace(databasePath) ||
            !Path.IsPathFullyQualified(databasePath) ||
            Path.EndsInDirectorySeparator(databasePath))
        {
            throw Invalid(DatabasePathKey);
        }

        return new ReferenceSqliteStartupOptions(Path.GetFullPath(databasePath), prepareIfMissing);
    }

    private static InvalidOperationException Invalid(string key) => new(
        $"The reference SQLite startup deployment requires an explicit, usable '{key}'. " +
        "The configured value is not echoed.");

    private string Build(SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = mode,
        Cache = SqliteCacheMode.Private,
        Pooling = false,
    }.ConnectionString;
}

/// <summary>The finite outcome of the sample's SQLite startup deployment gate.</summary>
public enum ReferenceSqliteStartupOutcome
{
    /// <summary>The deployment mode was not authorized. No provider, file, or EF call was made.</summary>
    DeploymentModeRejected,

    /// <summary>The target is absent and creating it was not explicitly permitted.</summary>
    TargetMissing,

    /// <summary>The target could not be observed, or was observed as unusable.</summary>
    TargetUnavailable,

    /// <summary>The explicit preparation of the missing target failed.</summary>
    PreparationFailed,

    /// <summary>Migration orchestration did not report success.</summary>
    MigrationFailed,

    /// <summary>The target is present and at the current migration version.</summary>
    Ready,
}

/// <summary>
/// The safe result of the startup gate. It carries a finite outcome and whether the consumer's
/// migration executor ran - never a path, a connection string, or a provider message.
/// </summary>
public sealed class ReferenceSqliteStartupResult
{
    /// <summary>Creates a result.</summary>
    public ReferenceSqliteStartupResult(ReferenceSqliteStartupOutcome outcome, bool executorWasCalled)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Outcome = outcome;
        ExecutorWasCalled = executorWasCalled;
    }

    /// <summary>Gets the finite outcome.</summary>
    public ReferenceSqliteStartupOutcome Outcome { get; }

    /// <summary>Gets whether the consumer's migration executor was invoked.</summary>
    public bool ExecutorWasCalled { get; }

    /// <summary>Gets whether the host may finish starting.</summary>
    public bool IsReady => Outcome == ReferenceSqliteStartupOutcome.Ready;

    /// <summary>Returns only the finite outcome.</summary>
    public override string ToString() =>
        $"ReferenceSqliteStartupResult(Outcome={Outcome}, ExecutorWasCalled={ExecutorWasCalled})";
}
