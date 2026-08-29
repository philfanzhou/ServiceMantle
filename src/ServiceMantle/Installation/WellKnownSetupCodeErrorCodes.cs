namespace ServiceMantle.Installation;

/// <summary>
/// Well-known safe error codes for Setup Code operations.
/// </summary>
/// <remarks>
/// No code or public message ever carries the candidate, the digest, raw stored timestamps,
/// connection information, or a provider exception.
/// </remarks>
public static class WellKnownSetupCodeErrorCodes
{
    /// <summary>
    /// The installation row does not exist.
    /// </summary>
    public const string InstallationNotFound = "installation.not_found";

    /// <summary>
    /// The installation row base state is corrupt, or its version cannot be incremented safely.
    /// </summary>
    public const string StateInvariantViolation = "installation.state_invariant_violation";

    /// <summary>
    /// The installation is already completed; a Setup Code is never created or restored afterwards.
    /// </summary>
    public const string InstallationCompleted = "installation.completed";

    /// <summary>
    /// The operation refused to run because the caller's DbContext carries disallowed pending changes.
    /// </summary>
    public const string DirtyContext = "installation.dirty_context";

    /// <summary>
    /// The optimistic concurrency baseline no longer matched at save time.
    /// </summary>
    public const string ConcurrencyConflict = "installation.concurrency_conflict";

    /// <summary>
    /// A pending installation must be completed by consuming its Setup Code.
    /// </summary>
    public const string SetupCodeRequired = "installation.setup_code_required";

    /// <summary>
    /// A Setup Code has already been issued for this pending installation.
    /// </summary>
    public const string AlreadyExists = "setup_code.already_exists";

    /// <summary>
    /// No Setup Code has been issued yet, so there is nothing to rotate.
    /// </summary>
    public const string NotCreated = "setup_code.not_created";

    /// <summary>
    /// The stored Setup Code material violates the generation invariant.
    /// </summary>
    public const string StorageCorrupt = "setup_code.storage_corrupt";

    /// <summary>
    /// The 32-bit issuance generation counter is exhausted.
    /// </summary>
    public const string GenerationExhausted = "setup_code.generation_exhausted";

    /// <summary>
    /// The candidate has an invalid format or does not match the stored digest.
    /// </summary>
    public const string Invalid = "setup_code.invalid";

    /// <summary>
    /// The stored material has expired.
    /// </summary>
    public const string Expired = "setup_code.expired";
}
