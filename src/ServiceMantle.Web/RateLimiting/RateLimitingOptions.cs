namespace ServiceMantle.Web.RateLimiting;

/// <summary>Configures the two ServiceMantle named rate-limit policies.</summary>
public sealed class RateLimitingOptions
{
    /// <summary>Gets the setup-endpoint policy settings.</summary>
    public RateLimitPolicyOptions Setup { get; } = new(
        RateLimitingDefaults.DefaultSetupPermitLimit);

    /// <summary>Gets the management-endpoint policy settings.</summary>
    public RateLimitPolicyOptions Management { get; } = new(
        RateLimitingDefaults.DefaultManagementPermitLimit);

    /// <summary>
    /// Gets the consumer-owned named policies keyed by policy name, each reusing the trusted
    /// client partition, the bounded sliding-window validation, and the safe rejection response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A policy name must be 1-128 characters over ASCII letters, digits, dots, hyphens, and
    /// underscores, and must not use the reserved <c>servicemantle.</c> prefix; duplicate,
    /// reserved, or malformed names and out-of-range values fail when the host starts. Endpoints
    /// enable a policy explicitly by name; no global limiter is introduced.
    /// </para>
    /// <para>
    /// A new <see cref="RateLimitPolicyOptions"/> starts with a zero permit limit, which is
    /// invalid on purpose: the permit limit is always set explicitly.
    /// </para>
    /// </remarks>
    public IDictionary<string, RateLimitPolicyOptions> ConsumerPolicies { get; } =
        new Dictionary<string, RateLimitPolicyOptions>(StringComparer.Ordinal);
}

/// <summary>Configures one ServiceMantle sliding-window rate-limit policy.</summary>
public sealed class RateLimitPolicyOptions
{
    internal RateLimitPolicyOptions(int permitLimit)
    {
        PermitLimit = permitLimit;
    }

    /// <summary>
    /// Initializes a policy whose permit limit is explicitly set afterwards; the initial zero
    /// value is invalid on purpose so a policy is never registered with an implicit limit.
    /// </summary>
    public RateLimitPolicyOptions()
    {
    }

    /// <summary>Gets or sets the number of requests allowed in each window.</summary>
    public int PermitLimit { get; set; }

    /// <summary>Gets or sets the sliding-window duration.</summary>
    public TimeSpan Window { get; set; } = RateLimitingDefaults.DefaultWindow;

    /// <summary>Gets or sets the number of segments in the sliding window.</summary>
    public int SegmentsPerWindow { get; set; } =
        RateLimitingDefaults.DefaultSegmentsPerWindow;
}
