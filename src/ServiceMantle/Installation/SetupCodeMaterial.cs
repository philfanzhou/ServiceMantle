namespace ServiceMantle.Installation;

/// <summary>
/// The evaluated state of the Setup Code material attached to a pending installation row.
/// </summary>
public enum SetupCodeMaterialStatus
{
    /// <summary>
    /// No Setup Code has ever been issued for this installation.
    /// </summary>
    NeverIssued = 0,

    /// <summary>
    /// A Setup Code has been issued; it may be valid or expired.
    /// </summary>
    Issued = 1,

    /// <summary>
    /// The stored material violates the generation invariant and must not be repaired automatically.
    /// </summary>
    Corrupt = 2,
}

/// <summary>
/// Evaluates the persisted Setup Code material of one installation row.
/// </summary>
/// <remarks>
/// <see cref="ServiceInstallationState.Status"/> stays the only durable authority for whether an
/// installation is complete; the Setup Code material is attached state of a pending row. The
/// non-negative generation counter is the invariant that keeps a fresh pending row distinguishable
/// from a row whose material was deleted after it had been issued: without it, both would present as
/// one set of nulls.
/// </remarks>
public sealed class SetupCodeMaterial
{
    private SetupCodeMaterial(
        SetupCodeMaterialStatus status,
        int generation,
        SetupCodeDigest? digest,
        DateTime? issuedAtUtc,
        DateTime? expiresAtUtc)
    {
        Status = status;
        Generation = generation;
        Digest = digest;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>
    /// Gets the evaluated material status.
    /// </summary>
    public SetupCodeMaterialStatus Status { get; }

    /// <summary>
    /// Gets the stored issuance generation.
    /// </summary>
    public int Generation { get; }

    /// <summary>
    /// Gets the parsed digest when <see cref="Status"/> is <see cref="SetupCodeMaterialStatus.Issued"/>.
    /// </summary>
    public SetupCodeDigest? Digest { get; }

    /// <summary>
    /// Gets the issuance timestamp when <see cref="Status"/> is
    /// <see cref="SetupCodeMaterialStatus.Issued"/>.
    /// </summary>
    public DateTime? IssuedAtUtc { get; }

    /// <summary>
    /// Gets the expiry timestamp when <see cref="Status"/> is
    /// <see cref="SetupCodeMaterialStatus.Issued"/>.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>
    /// Evaluates stored material against the generation invariant.
    /// </summary>
    /// <param name="generation">The stored non-negative generation counter.</param>
    /// <param name="digest">The stored versioned digest, if any.</param>
    /// <param name="issuedAtUtc">The stored issuance timestamp, if any.</param>
    /// <param name="expiresAtUtc">The stored expiry timestamp, if any.</param>
    /// <param name="installationCreatedAtUtc">The installation row creation timestamp.</param>
    /// <remarks>
    /// Generation 0 with all three nullable fields empty means never issued. A positive generation
    /// with complete, well-ordered material means issued. Every other combination - a negative
    /// generation, a positive generation missing any field, generation 0 carrying material, an
    /// unknown or malformed digest version, inverted times, or issuance before the installation was
    /// created - is corrupt and fails closed.
    /// </remarks>
    public static SetupCodeMaterial Evaluate(
        int generation,
        string? digest,
        DateTime? issuedAtUtc,
        DateTime? expiresAtUtc,
        DateTime installationCreatedAtUtc)
    {
        var hasAnyMaterial = digest is not null || issuedAtUtc.HasValue || expiresAtUtc.HasValue;

        if (generation < 0)
        {
            return Corrupt(generation);
        }

        if (generation == 0)
        {
            return hasAnyMaterial ? Corrupt(generation) : new SetupCodeMaterial(
                SetupCodeMaterialStatus.NeverIssued,
                generation,
                digest: null,
                issuedAtUtc: null,
                expiresAtUtc: null);
        }

        if (digest is null ||
            !issuedAtUtc.HasValue ||
            !expiresAtUtc.HasValue ||
            !SetupCodeDigest.TryParse(digest, out var parsedDigest) ||
            parsedDigest is null)
        {
            return Corrupt(generation);
        }

        var issuedAt = issuedAtUtc.Value;
        var expiresAt = expiresAtUtc.Value;
        if (expiresAt <= issuedAt || issuedAt < installationCreatedAtUtc)
        {
            return Corrupt(generation);
        }

        return new SetupCodeMaterial(
            SetupCodeMaterialStatus.Issued,
            generation,
            parsedDigest,
            issuedAt,
            expiresAt);
    }

    /// <summary>
    /// Determines whether issued material has expired at the supplied time.
    /// </summary>
    public bool IsExpired(DateTime nowUtc) =>
        Status == SetupCodeMaterialStatus.Issued && nowUtc >= ExpiresAtUtc!.Value;

    /// <summary>
    /// Returns a safe projection that never includes the digest or stored timestamps.
    /// </summary>
    public override string ToString() =>
        $"SetupCodeMaterial(Status={Status}, Generation={Generation})";

    private static SetupCodeMaterial Corrupt(int generation) => new(
        SetupCodeMaterialStatus.Corrupt,
        generation,
        digest: null,
        issuedAtUtc: null,
        expiresAtUtc: null);
}
