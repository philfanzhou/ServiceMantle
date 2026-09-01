namespace ServiceMantle.AspNetCore;

/// <summary>Defines the stable ServiceMantle rate-limiting policies and defaults.</summary>
public static class ServiceMantleRateLimitingDefaults
{
    /// <summary>The named policy for unauthenticated setup endpoints.</summary>
    public const string SetupPolicyName = "servicemantle.setup";

    /// <summary>The named policy for authenticated management endpoints.</summary>
    public const string ManagementPolicyName = "servicemantle.management";

    /// <summary>The stable error code returned when a request is rate limited.</summary>
    public const string RejectedErrorCode = "rate_limit.exceeded";

    /// <summary>The default setup permits per window.</summary>
    public const int DefaultSetupPermitLimit = 5;

    /// <summary>The default management permits per window.</summary>
    public const int DefaultManagementPermitLimit = 120;

    /// <summary>The default window used by both policies.</summary>
    public static TimeSpan DefaultWindow { get; } = TimeSpan.FromMinutes(1);

    /// <summary>The default number of sliding-window segments.</summary>
    public const int DefaultSegmentsPerWindow = 6;
}
