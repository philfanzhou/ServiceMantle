namespace ServiceMantle.AspNetCore.RateLimiting;

/// <summary>Configures the two ServiceMantle named rate-limit policies.</summary>
public sealed class RateLimitingOptions
{
    /// <summary>Gets the setup-endpoint policy settings.</summary>
    public RateLimitPolicyOptions Setup { get; } = new(
        RateLimitingDefaults.DefaultSetupPermitLimit);

    /// <summary>Gets the management-endpoint policy settings.</summary>
    public RateLimitPolicyOptions Management { get; } = new(
        RateLimitingDefaults.DefaultManagementPermitLimit);
}

/// <summary>Configures one ServiceMantle sliding-window rate-limit policy.</summary>
public sealed class RateLimitPolicyOptions
{
    internal RateLimitPolicyOptions(int permitLimit)
    {
        PermitLimit = permitLimit;
    }

    /// <summary>Gets or sets the number of requests allowed in each window.</summary>
    public int PermitLimit { get; set; }

    /// <summary>Gets or sets the sliding-window duration.</summary>
    public TimeSpan Window { get; set; } = RateLimitingDefaults.DefaultWindow;

    /// <summary>Gets or sets the number of segments in the sliding window.</summary>
    public int SegmentsPerWindow { get; set; } =
        RateLimitingDefaults.DefaultSegmentsPerWindow;
}
