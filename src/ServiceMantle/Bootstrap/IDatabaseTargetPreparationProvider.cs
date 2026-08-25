namespace ServiceMantle.Bootstrap;

/// <summary>
/// Optional provider capability that observes and, when explicitly requested, prepares a missing
/// database target (a <see cref="BootstrapDatabaseTargetKind.ServerDatabase"/>,
/// <see cref="BootstrapDatabaseTargetKind.File"/>, or <see cref="BootstrapDatabaseTargetKind.ServerSchema"/>).
/// </summary>
/// <remarks>
/// This capability is intentionally separate from <see cref="IBootstrapDatabaseProvider"/>. Not every
/// database provider supports preparing a missing target, and registering
/// <see cref="IBootstrapDatabaseProvider"/> must not imply that it does. Callers resolve this
/// capability through <see cref="DatabaseTargetPreparationProviderRegistry"/> and must fail closed
/// with <see cref="WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported"/> when no
/// provider is registered for a given database provider id, rather than silently treating the
/// target as already prepared.
/// </remarks>
public interface IDatabaseTargetPreparationProvider
{
    /// <summary>
    /// Gets the database provider identifier that this preparation provider supports. This must
    /// match the corresponding <see cref="IBootstrapDatabaseProvider"/>'s canonical provider id.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets the kind of target this provider prepares.
    /// </summary>
    BootstrapDatabaseTargetKind TargetKind { get; }

    /// <summary>
    /// Inspects the target without modifying it, distinguishing whether the server can be reached,
    /// whether the target exists, and whether the target itself can be connected to.
    /// </summary>
    /// <param name="target">The target database configuration to observe.</param>
    /// <param name="cancellationToken">Cancellation token for the observation call.</param>
    /// <exception cref="OperationCanceledException">The caller requested cancellation.</exception>
    ValueTask<DatabaseTargetObservation> ObserveAsync(
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken);

    /// <summary>
    /// Prepares the target when explicitly requested. Implementations must never overwrite, drop,
    /// recreate, or otherwise destructively modify a target that already exists; an existing target
    /// is reported as <see cref="DatabaseTargetPreparationOutcome.AlreadyExists"/>.
    /// </summary>
    /// <param name="request">
    /// The target to prepare and the administrative connection information to prepare it with. The
    /// administrative connection information must be used only for the duration of this call.
    /// </param>
    /// <param name="timeout">The maximum time to spend preparing the target. Cannot be infinite.</param>
    /// <param name="cancellationToken">Cancellation token. Cancellation takes precedence over timeout.</param>
    /// <exception cref="OperationCanceledException">The caller requested cancellation.</exception>
    ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
        DatabaseTargetPreparationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
