namespace ServiceMantle.Audit;

/// <summary>
/// A validated, sanitized management audit event ready to be written. Instances are only created
/// through <see cref="Create"/>, which enforces the sensitive-content policy: metadata keys naming a
/// secret are rejected, and secret-shaped substrings in the description or metadata values are
/// redacted before the event is constructed.
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

        var cleanedDescription = AuditTextSanitizer.Clean(
            securityDescription, MaxDescriptionLength, "audit.description_invalid", "security description");
        if (cleanedDescription is not null)
        {
            cleanedDescription = ManagementAuditContentSanitizer.Redact(cleanedDescription);
        }

        var sanitizedMetadata = SanitizeMetadata(metadata);

        var resolvedOccurredAtUtc = occurredAtUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow();

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

    private static IReadOnlyDictionary<string, string> SanitizeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return new Dictionary<string, string>(0);
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
            sanitized[cleanedKey] = ManagementAuditContentSanitizer.Redact(cleanedValue);
        }

        return sanitized;
    }
}
