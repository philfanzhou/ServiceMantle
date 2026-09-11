using Microsoft.AspNetCore.Http;
using ServiceMantle.Installation;

namespace ServiceMantle.AspNetCore.ManagementApi.Setup;

/// <summary>The finite outcome of one consumer-owned Setup completion transaction.</summary>
public enum SetupCompletionStatus
{
    /// <summary>The consumer transaction committed. Setup is complete and durable.</summary>
    Committed,

    /// <summary>The Setup Code did not validate, or lost its race, and nothing was committed.</summary>
    CredentialInvalid,

    /// <summary>The installation was already completed, or a concurrent completion or version conflict won.</summary>
    Conflict,

    /// <summary>A contributor rejected the completion before anything was committed.</summary>
    ValidationFailed,

    /// <summary>Staging, saving, committing, or cleanup failed, or the executor timed out internally.</summary>
    Unavailable,
}

/// <summary>
/// The closed result of one Setup completion attempt.
/// </summary>
/// <remarks>
/// The result carries a finite status and nothing else: no Setup Code, digest, generation, expiry,
/// installation version, contributor detail, store error code, or exception. Only
/// <see cref="SetupCompletionStatus.Committed"/> states that the consumer's shared
/// transaction committed; the core <c>ServiceSetupOrchestrator</c> success and
/// <c>SetupCodeConsumptionResult.IsStaged</c> continue to mean staged, not committed.
/// </remarks>
public sealed class SetupCompletionResult
{
    private static readonly SetupCompletionResult CommittedResult =
        new(SetupCompletionStatus.Committed);

    private static readonly SetupCompletionResult CredentialInvalidResult =
        new(SetupCompletionStatus.CredentialInvalid);

    private static readonly SetupCompletionResult ConflictResult =
        new(SetupCompletionStatus.Conflict);

    private static readonly SetupCompletionResult ValidationFailedResult =
        new(SetupCompletionStatus.ValidationFailed);

    private static readonly SetupCompletionResult UnavailableResult =
        new(SetupCompletionStatus.Unavailable);

    private SetupCompletionResult(SetupCompletionStatus status)
    {
        Status = status;
    }

    /// <summary>Gets the finite outcome.</summary>
    public SetupCompletionStatus Status { get; }

    /// <summary>States that the consumer transaction committed. Never return this before commit.</summary>
    public static SetupCompletionResult Committed() => CommittedResult;

    /// <summary>States that the Setup Code was missing, invalid, expired, replayed, or lost its race.</summary>
    public static SetupCompletionResult CredentialInvalid() => CredentialInvalidResult;

    /// <summary>States that the installation was already completed or a concurrent completion won.</summary>
    public static SetupCompletionResult Conflict() => ConflictResult;

    /// <summary>States that a contributor rejected the completion.</summary>
    public static SetupCompletionResult ValidationFailed() => ValidationFailedResult;

    /// <summary>States that staging, saving, committing, or cleanup failed.</summary>
    public static SetupCompletionResult Unavailable() => UnavailableResult;

    /// <summary>Returns only the finite status.</summary>
    public override string ToString() => $"SetupCompletionResult(Status={Status})";
}

/// <summary>
/// Executes one Setup completion inside a consumer-owned transaction and returns only after it has
/// committed.
/// </summary>
/// <param name="httpContext">The current management HTTP request.</param>
/// <param name="setupCode">The strictly parsed candidate Setup Code. It is never logged or echoed.</param>
/// <param name="cancellationToken">The request cancellation token.</param>
/// <returns>The closed completion result, produced only after the transaction settled.</returns>
/// <remarks>
/// <para>
/// The delegate must create a fresh asynchronous scope from
/// <c>httpContext.RequestServices</c> with a clean DbContext, begin its transaction, and then, in
/// this order: call <c>IServiceSetupCodeStore.ValidateAsync</c> read-only; invoke
/// <c>ServiceSetupOrchestrator</c> on that clean staging scope; call <c>StageConsumeAsync</c>; call
/// <c>SaveChangesAsync</c> exactly once; and commit. It may return
/// <see cref="SetupCompletionStatus.Committed"/> only after the commit completed.
/// </para>
/// <para>
/// Any code race after staging, contributor failure, staging, save, commit, cancellation, or cleanup
/// failure must roll back and discard the whole scope without retrying, and must not reuse that
/// DbContext. ServiceMantle never implicitly commits an existing consumer unit of work, and a
/// malformed or malicious delegate is outside the endpoint guarantee.
/// </para>
/// </remarks>
public delegate ValueTask<SetupCompletionResult> SetupExecutor(
    HttpContext httpContext,
    SetupCode setupCode,
    CancellationToken cancellationToken);
