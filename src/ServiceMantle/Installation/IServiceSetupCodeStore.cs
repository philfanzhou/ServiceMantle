namespace ServiceMantle.Installation;

/// <summary>
/// Manages the one-time Setup Code attached to a pending service installation.
/// </summary>
/// <remarks>
/// <para>
/// Every expected domain rejection is a closed result: callers never identify
/// <c>not_found</c>, <c>completed</c>, <c>invalid</c>, <c>expired</c>, <c>storage_corrupt</c>,
/// <c>generation_exhausted</c>, or <c>concurrency_conflict</c> by catching an exception. Programming
/// errors such as null arguments throw standard argument exceptions, caller cancellation propagates
/// <see cref="OperationCanceledException"/>, and non-concurrency database, command, or provider
/// failures use the existing <see cref="ServiceInstallationStoreException"/> channel with the stable
/// <c>installation.storage_error</c> code.
/// </para>
/// <para>
/// <see cref="CreateAsync"/> and <see cref="RotateAsync"/> are standalone writes that call
/// <c>SaveChangesAsync</c> once and never create, commit, or take over a database transaction. They
/// require a clean DbContext: any pre-existing <c>Added</c>, <c>Modified</c>, or <c>Deleted</c> entry
/// makes them return <c>installation.dirty_context</c> without generating a code or saving. A
/// short-lived dedicated DbContext is the recommended shape.
/// </para>
/// <para>
/// <see cref="StageConsumeAsync"/> stages the material clearing, the completed status, the completion
/// timestamp, and the version increment without saving, so a caller can commit it together with its
/// own contributor, configuration, and audit work:
/// </para>
/// <code>
/// await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
/// var consumption = await setupCodeStore.StageConsumeAsync(serviceId, candidate, cancellationToken);
/// if (!consumption.IsStaged)
/// {
///     return consumption.ErrorCode;
/// }
///
/// await contributors.StageAsync(dbContext, cancellationToken);
/// await dbContext.SaveChangesAsync(cancellationToken);
/// await transaction.CommitAsync(cancellationToken);
/// </code>
/// <para>
/// A database rollback restores only the database. It does not restore the completed status and
/// current values already staged in the EF Core change tracker, so after a rollback the caller must
/// dispose that DbContext, or explicitly reload, restore, or detach the installation entry, before
/// reusing it. Never retry on, or read installation authority from, an unrestored tracker.
/// </para>
/// </remarks>
public interface IServiceSetupCodeStore
{
    /// <summary>
    /// Creates the first Setup Code for a pending installation and saves it.
    /// </summary>
    /// <remarks>
    /// The plaintext is returned only by a successful result, and only after the save succeeded. When
    /// the caller runs this inside a larger external transaction, success only means the save joined
    /// that transaction; ServiceMantle does not own or guarantee the caller's commit, and after an
    /// external rollback the plaintext simply no longer validates.
    /// </remarks>
    ValueTask<SetupCodeIssueResult> CreateAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the Setup Code of a pending installation atomically and saves it.
    /// </summary>
    /// <remarks>
    /// Rotation requires existing material - valid or expired - and increments the generation counter.
    /// The previous code stops validating as soon as the save succeeds.
    /// </remarks>
    ValueTask<SetupCodeIssueResult> RotateAsync(
        ServiceId serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a candidate without changing any state.
    /// </summary>
    ValueTask<SetupCodeValidationResult> ValidateAsync(
        ServiceId serviceId,
        string candidate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages consumption of a valid candidate into the caller's unit of work without saving.
    /// </summary>
    ValueTask<SetupCodeConsumptionResult> StageConsumeAsync(
        ServiceId serviceId,
        string candidate,
        CancellationToken cancellationToken = default);
}
