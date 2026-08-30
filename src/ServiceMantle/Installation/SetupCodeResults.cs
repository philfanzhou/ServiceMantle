namespace ServiceMantle.Installation;

/// <summary>
/// The closed result of a Setup Code Create or Rotate operation.
/// </summary>
/// <remarks>
/// The plaintext is present only on the successful result that produced it, and only after the
/// underlying save succeeded. A rejected operation never carries a plaintext, and neither
/// <see cref="ToString"/> nor the error code reveals the candidate, the digest, or stored values.
/// </remarks>
public sealed class SetupCodeIssueResult
{
    private SetupCodeIssueResult(
        SetupCode? setupCode,
        int generation,
        DateTime? expiresAtUtc,
        string? errorCode)
    {
        SetupCode = setupCode;
        Generation = generation;
        ExpiresAtUtc = expiresAtUtc;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets a value indicating whether a Setup Code was issued and saved.
    /// </summary>
    public bool IsIssued => SetupCode is not null;

    /// <summary>
    /// Gets the plaintext Setup Code, returned only by this successful result.
    /// </summary>
    public SetupCode? SetupCode { get; }

    /// <summary>
    /// Gets the issuance generation the operation stored, or 0 when it was rejected.
    /// </summary>
    public int Generation { get; }

    /// <summary>
    /// Gets the expiry of the issued Setup Code.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>
    /// Gets the safe error code of a rejected operation.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Creates an issued result.
    /// </summary>
    public static SetupCodeIssueResult Issued(
        SetupCode setupCode,
        int generation,
        DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(setupCode);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        return new SetupCodeIssueResult(setupCode, generation, expiresAtUtc, errorCode: null);
    }

    /// <summary>
    /// Creates a rejected result.
    /// </summary>
    /// <param name="errorCode">
    /// A classification declared by <see cref="WellKnownSetupCodeErrorCodes"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The error code is not one of the declared classifications. A shape rule alone would accept a
    /// candidate Setup Code verbatim and publish it through <see cref="ErrorCode"/> and
    /// <see cref="ToString"/>, so the closed set is what keeps a rejection value-free.
    /// </exception>
    public static SetupCodeIssueResult Rejected(string errorCode)
    {
        WellKnownSetupCodeErrorCodes.EnsureDefined(errorCode, nameof(errorCode));
        return new SetupCodeIssueResult(
            setupCode: null,
            generation: 0,
            expiresAtUtc: null,
            errorCode);
    }

    /// <summary>
    /// Returns a safe projection that never includes the plaintext Setup Code.
    /// </summary>
    public override string ToString() =>
        $"SetupCodeIssueResult(IsIssued={IsIssued}, Generation={Generation}, ErrorCode={ErrorCode})";
}

/// <summary>
/// The closed result of a read-only Setup Code validation.
/// </summary>
public sealed class SetupCodeValidationResult
{
    private static readonly SetupCodeValidationResult ValidResult = new(errorCode: null);

    private SetupCodeValidationResult(string? errorCode)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets a value indicating whether the candidate is currently valid.
    /// </summary>
    public bool IsValid => ErrorCode is null;

    /// <summary>
    /// Gets the safe error code of a rejected validation.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Creates a valid result.
    /// </summary>
    public static SetupCodeValidationResult Valid() => ValidResult;

    /// <summary>
    /// Creates a rejected result.
    /// </summary>
    /// <param name="errorCode">
    /// A classification declared by <see cref="WellKnownSetupCodeErrorCodes"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The error code is not one of the declared classifications. A shape rule alone would accept a
    /// candidate Setup Code verbatim and publish it through <see cref="ErrorCode"/> and
    /// <see cref="ToString"/>, so the closed set is what keeps a rejection value-free.
    /// </exception>
    public static SetupCodeValidationResult Rejected(string errorCode)
    {
        WellKnownSetupCodeErrorCodes.EnsureDefined(errorCode, nameof(errorCode));
        return new SetupCodeValidationResult(errorCode);
    }

    /// <summary>
    /// Returns a safe projection that never includes the candidate or the digest.
    /// </summary>
    public override string ToString() =>
        $"SetupCodeValidationResult(IsValid={IsValid}, ErrorCode={ErrorCode})";
}

/// <summary>
/// The closed result of staging a Setup Code consumption into the caller's unit of work.
/// </summary>
/// <remarks>
/// A staged result means the material clearing, the completed status, the completion timestamp, and
/// the version increment are pending on the caller's DbContext. Nothing has been saved or committed:
/// the caller owns that transaction, and a rollback leaves the installation pending in the database.
/// </remarks>
public sealed class SetupCodeConsumptionResult
{
    private SetupCodeConsumptionResult(ServiceInstallationState? state, string? errorCode)
    {
        State = state;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets a value indicating whether the consumption was staged.
    /// </summary>
    public bool IsStaged => State is not null;

    /// <summary>
    /// Gets the staged installation state.
    /// </summary>
    public ServiceInstallationState? State { get; }

    /// <summary>
    /// Gets the safe error code of a rejected consumption.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Creates a staged result.
    /// </summary>
    public static SetupCodeConsumptionResult Staged(ServiceInstallationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new SetupCodeConsumptionResult(state, errorCode: null);
    }

    /// <summary>
    /// Creates a rejected result.
    /// </summary>
    /// <param name="errorCode">
    /// A classification declared by <see cref="WellKnownSetupCodeErrorCodes"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The error code is not one of the declared classifications. A shape rule alone would accept a
    /// candidate Setup Code verbatim and publish it through <see cref="ErrorCode"/> and
    /// <see cref="ToString"/>, so the closed set is what keeps a rejection value-free.
    /// </exception>
    public static SetupCodeConsumptionResult Rejected(string errorCode)
    {
        WellKnownSetupCodeErrorCodes.EnsureDefined(errorCode, nameof(errorCode));
        return new SetupCodeConsumptionResult(state: null, errorCode);
    }

    /// <summary>
    /// Returns a safe projection that never includes the candidate or the digest.
    /// </summary>
    public override string ToString() =>
        $"SetupCodeConsumptionResult(IsStaged={IsStaged}, ErrorCode={ErrorCode})";
}
