namespace ServiceMantle.AspNetCore;

/// <summary>Configures the two ServiceMantle named rate-limit policies.</summary>
public sealed class ServiceMantleRateLimitingOptions
{
    /// <summary>Gets the setup-endpoint policy settings.</summary>
    public ServiceMantleRateLimitPolicyOptions Setup { get; } = new(
        ServiceMantleRateLimitingDefaults.DefaultSetupPermitLimit);

    /// <summary>Gets the management-endpoint policy settings.</summary>
    public ServiceMantleRateLimitPolicyOptions Management { get; } = new(
        ServiceMantleRateLimitingDefaults.DefaultManagementPermitLimit);
}

/// <summary>Configures one ServiceMantle sliding-window rate-limit policy.</summary>
public sealed class ServiceMantleRateLimitPolicyOptions
{
    internal ServiceMantleRateLimitPolicyOptions(int permitLimit)
    {
        PermitLimit = permitLimit;
    }

    /// <summary>Gets or sets the number of requests allowed in each window.</summary>
    public int PermitLimit { get; set; }

    /// <summary>Gets or sets the sliding-window duration.</summary>
    public TimeSpan Window { get; set; } = ServiceMantleRateLimitingDefaults.DefaultWindow;

    /// <summary>Gets or sets the number of segments in the sliding window.</summary>
    public int SegmentsPerWindow { get; set; } =
        ServiceMantleRateLimitingDefaults.DefaultSegmentsPerWindow;
}
