using System.Collections.Frozen;
using System.Net;

namespace ServiceMantle.Audit;

/// <summary>
/// A validated, sanitized management audit event ready to be written. Instances are only created
/// through <see cref="Create"/>, which enforces the sensitive-content policy: metadata keys naming a
/// secret are rejected, and supported secret-shaped formats in the description or metadata values
/// are redacted before the event is constructed. Callers must never provide opaque secrets as free
/// text; sanitization is a defense-in-depth boundary rather than a general-purpose DLP engine.
/// </summary>
public sealed record ManagementAuditEvent
{
    /// <summary>
    /// The maximum length allowed for a security description.
    /// </summary>
    public const int MaxDescriptionLength = 4000;

    /// <summary>
    /// The maximum length allowed for a client IP value.
    /// </summary>
    public const int MaxClientIpLength = 64;

    /// <summary>
    /// The maximum length allowed for a correlation identifier.
    /// </summary>
    public const int MaxCorrelationIdLength = 128;

    /// <summary>
    /// The maximum number of structured metadata entries allowed.
    /// </summary>
    public const int MaxMetadataEntries = 32;

    /// <summary>
    /// The maximum length allowed for a metadata key.
    /// </summary>
    public const int MaxMetadataKeyLength = 64;

    /// <summary>
    /// The maximum length allowed for a metadata value.
    /// </summary>
    public const int MaxMetadataValueLength = 1024;

    /// <summary>
    /// Gets who or what performed the action.
    /// </summary>
    public ManagementAuditOperator Operator { get; }

    /// <summary>
    /// Gets the action performed.
    /// </summary>
    public ManagementAuditAction Action { get; }

    /// <summary>
    /// Gets the resource the action affected.
    /// </summary>
    public ManagementAuditTarget Target { get; }

    /// <summary>
    /// Gets the security-relevant outcome of the action.
    /// </summary>
    public ManagementAuditOutcome Outcome { get; }

    /// <summary>
    /// Gets the UTC time the action occurred.
    /// </summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>
    /// Gets the client IP the action was attributed to, or null when unavailable.
    /// </summary>
    public string? ClientIp { get; }

    /// <summary>
    /// Gets the correlation identifier linking this event to a broader operation, or null.
    /// </summary>
    public string? CorrelationId { get; }

    /// <summary>
    /// Gets a sanitized, human-readable description of the security-relevant event, or null.
    /// </summary>
    public string? SecurityDescription { get; }

    /// <summary>
    /// Gets sanitized structured metadata for the event.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    private ManagementAuditEvent(
        ManagementAuditOperator operatorInfo,
        ManagementAuditAction action,
        ManagementAuditTarget target,
        ManagementAuditOutcome outcome,
        DateTimeOffset occurredAtUtc,
        string? clientIp,
        string? correlationId,
        string? securityDescription,
        IReadOnlyDictionary<string, string> metadata)
    {
        Operator = operatorInfo;
        Action = action;
        Target = target;
        Outcome = outcome;
        OccurredAtUtc = occurredAtUtc;
        ClientIp = clientIp;
        CorrelationId = correlationId;
        SecurityDescription = securityDescription;
        Metadata = metadata;
    }

    /// <summary>
    /// Creates a validated, sanitized audit event.
    /// </summary>
    /// <param name="operatorInfo">Who or what performed the action.</param>
    /// <param name="action">The action performed.</param>
    /// <param name="target">The resource the action affected.</param>
    /// <param name="outcome">The security-relevant outcome. Defaults to <see cref="ManagementAuditOutcome.Unknown"/>.</param>
    /// <param name="occurredAtUtc">The UTC time the action occurred. Defaults to the current time via <paramref name="timeProvider"/>.</param>
    /// <param name="clientIp">The client IP the action was attributed to.</param>
    /// <param name="correlationId">A correlation identifier linking this event to a broader operation.</param>
    /// <param name="securityDescription">A human-readable description of the security-relevant event.</param>
    /// <param name="metadata">Structured metadata for the event.</param>
    /// <param name="timeProvider">Time provider used when <paramref name="occurredAtUtc"/> is not supplied.</param>
    /// <exception cref="ManagementAuditException">A field is invalid or names a sensitive value.</exception>
    public static ManagementAuditEvent Create(
        ManagementAuditOperator operatorInfo,
        ManagementAuditAction action,
        ManagementAuditTarget target,
        ManagementAuditOutcome outcome = ManagementAuditOutcome.Unknown,
        DateTimeOffset? occurredAtUtc = null,
        string? clientIp = null,
        string? correlationId = null,
        string? securityDescription = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(operatorInfo);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(target);

        if (!Enum.IsDefined(outcome))
        {
            throw new ManagementAuditException(
                "audit.outcome_invalid",
                "The audit outcome value is not defined.");
        }

        var cleanedClientIp = AuditTextSanitizer.Clean(
            clientIp, MaxClientIpLength, "audit.client_ip_invalid", "client IP");
        var cleanedCorrelationId = AuditTextSanitizer.Clean(
            correlationId, MaxCorrelationIdLength, "audit.correlation_id_invalid", "correlation identifier");

        if (cleanedClientIp is not null)
        {
            if (!IPAddress.TryParse(cleanedClientIp, out var parsedClientIp))
            {
                throw new ManagementAuditException(
                    "audit.client_ip_invalid",
                    "The audit client IP must be a valid IPv4 or IPv6 address.");
            }

            cleanedClientIp = parsedClientIp.ToString();
        }

        if (cleanedCorrelationId is not null && !IsSafeCorrelationId(cleanedCorrelationId))
        {
            throw new ManagementAuditException(
                "audit.correlation_id_invalid",
                "The audit correlation identifier contains unsupported characters.");
        }

        var cleanedDescription = AuditTextSanitizer.Clean(
            securityDescription, MaxDescriptionLength, "audit.description_invalid", "security description");
        if (cleanedDescription is not null)
        {
            cleanedDescription = ManagementAuditContentSanitizer.Redact(cleanedDescription);
            if (cleanedDescription.Length > MaxDescriptionLength)
            {
                // Redaction can expand text (for example "Bearer a" becomes "Bearer [REDACTED]"),
                // so the persisted length contract is enforced on the final sanitized value.
                throw new ManagementAuditException(
                    "audit.description_invalid",
                    $"The audit security description exceeds the maximum allowed length of {MaxDescriptionLength} characters after sensitive-content redaction.");
            }
        }

        var sanitizedMetadata = SanitizeMetadata(metadata);

        var resolvedOccurredAtUtc = (occurredAtUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow())
            .ToUniversalTime();

        return new ManagementAuditEvent(
            operatorInfo,
            action,
            target,
            outcome,
            resolvedOccurredAtUtc,
            cleanedClientIp,
            cleanedCorrelationId,
            cleanedDescription,
            sanitizedMetadata);
    }

    /// <summary>
    /// Returns a safe projection that excludes descriptions, metadata, client IPs, and correlation
    /// identifiers so interpolating an event into a log cannot disclose audit content.
    /// </summary>
    public override string ToString() =>
        $"ManagementAuditEvent(Action={Action}, Outcome={Outcome}, OccurredAtUtc={OccurredAtUtc:O})";

    /// <summary>
    /// Compares events by value, including structural comparison of metadata. The default record
    /// equality would fall back to reference comparison for <see cref="Metadata"/>, producing
    /// results that flip depending on whether metadata is empty.
    /// </summary>
    public bool Equals(ManagementAuditEvent? other)
    {
        if (other is null)
        {
            return false;
        }

        return Operator.Equals(other.Operator)
            && Action.Equals(other.Action)
            && Target.Equals(other.Target)
            && Outcome == other.Outcome
            && OccurredAtUtc.Equals(other.OccurredAtUtc)
            && string.Equals(ClientIp, other.ClientIp, StringComparison.Ordinal)
            && string.Equals(CorrelationId, other.CorrelationId, StringComparison.Ordinal)
            && string.Equals(SecurityDescription, other.SecurityDescription, StringComparison.Ordinal)
            && MetadataEquals(other.Metadata);
    }

    /// <summary>
    /// Computes a hash code that is independent of metadata enumeration order.
    /// </summary>
    public override int GetHashCode()
    {
        var metadataHash = 0;
        foreach (var (key, value) in Metadata)
        {
            metadataHash ^= HashCode.Combine(key, value);
        }

        return HashCode.Combine(
            HashCode.Combine(
                Operator,
                Action,
                Target,
                Outcome,
                OccurredAtUtc,
                ClientIp,
                CorrelationId,
                SecurityDescription),
            metadataHash);
    }

    private bool MetadataEquals(IReadOnlyDictionary<string, string> other)
    {
        if (Metadata.Count != other.Count)
        {
            return false;
        }

        foreach (var (key, value) in Metadata)
        {
            if (!other.TryGetValue(key, out var otherValue)
                || !string.Equals(value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> SanitizeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        if (metadata.Count > MaxMetadataEntries)
        {
            throw new ManagementAuditException(
                "audit.metadata_invalid",
                $"The audit metadata must not contain more than {MaxMetadataEntries} entries.");
        }

        var sanitized = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            var cleanedKey = AuditTextSanitizer.CleanRequired(
                key, MaxMetadataKeyLength, "audit.metadata_invalid", "metadata key");
            ManagementAuditContentSanitizer.EnsureMetadataKeyAllowed(cleanedKey);

            var cleanedValue = AuditTextSanitizer.CleanRequired(
                value, MaxMetadataValueLength, "audit.metadata_invalid", "metadata value");
            var redactedValue = ManagementAuditContentSanitizer.Redact(cleanedValue);
            if (redactedValue.Length > MaxMetadataValueLength)
            {
                throw new ManagementAuditException(
                    "audit.metadata_invalid",
                    $"The audit metadata value exceeds the maximum allowed length of {MaxMetadataValueLength} characters after sensitive-content redaction.");
            }

            if (!sanitized.TryAdd(cleanedKey, redactedValue))
            {
                throw new ManagementAuditException(
                    "audit.metadata_invalid",
                    "The audit metadata contains duplicate keys after normalization.");
            }
        }

        return sanitized.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static bool IsSafeCorrelationId(string value)
    {
        foreach (var character in value)
        {
            if (character is not (>= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or ':'))
            {
                return false;
            }
        }

        return true;
    }
}
