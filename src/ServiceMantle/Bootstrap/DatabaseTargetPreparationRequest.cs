namespace ServiceMantle.Bootstrap;

/// <summary>
/// Describes what to prepare and, temporarily, the administrative credentials to prepare it with.
/// </summary>
/// <remarks>
/// <see cref="AdministrativeConnectionString"/> is used only for the duration of the preparation
/// call. Providers must not persist it, write it into a Bootstrap projection or business database,
/// log it, include it in diagnostics, or return it in any result.
/// Server-database preparation verifies both endpoints using the target credentials and a
/// provider-defined maintenance database. The caller must trust the endpoints under its
/// authentication/TLS policy and use stable single-server routes independent of database or user.
/// Unknown proxy routing, session migration, and transparent failover are outside this contract.
/// Verification failure does not authorize falling back to administrative credentials on the target.
/// </remarks>
public sealed class DatabaseTargetPreparationRequest
{
    /// <summary>
    /// Initializes a database target preparation request.
    /// </summary>
    /// <param name="target">The target database configuration to prepare.</param>
    /// <param name="administrativeConnectionString">
    /// Server-level or administrative connection information used only to perform this
    /// preparation call. This value is never persisted, logged, or echoed back by ServiceMantle.
    /// </param>
    public DatabaseTargetPreparationRequest(
        BootstrapDatabaseConfiguration target,
        string administrativeConnectionString)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(administrativeConnectionString);

        Target = target;
        AdministrativeConnectionString = administrativeConnectionString.Trim();
    }

    /// <summary>
    /// Gets the target database configuration to prepare.
    /// </summary>
    public BootstrapDatabaseConfiguration Target { get; }

    /// <summary>
    /// Gets the administrative connection string to use only for this preparation call.
    /// </summary>
    public string AdministrativeConnectionString { get; }

    /// <summary>
    /// Returns a representation that excludes both connection strings.
    /// </summary>
    public override string ToString() =>
        $"DatabaseTargetPreparationRequest(Provider={Target.Provider})";
}
