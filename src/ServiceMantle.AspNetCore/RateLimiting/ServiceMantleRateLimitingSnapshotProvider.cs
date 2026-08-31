using System.Threading.RateLimiting;

namespace ServiceMantle.AspNetCore;

internal sealed record ServiceMantleRateLimitingRegistration(
    ServiceMantleRateLimitingOptions Options);

internal sealed class ServiceMantleRateLimitingSnapshotProvider(
    IEnumerable<ServiceMantleRateLimitingRegistration> registrations)
{
    private readonly object sync = new();
    private ServiceMantleRateLimitingSnapshot? snapshot;

    internal ServiceMantleRateLimitingSnapshot GetRequiredSnapshot()
    {
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (sync)
        {
            if (snapshot is not null)
            {
                return snapshot;
            }

            ServiceMantleRateLimitingSnapshot? baseline = null;
            foreach (var registration in registrations)
            {
                var candidate = Normalize(registration.Options);
                if (baseline is not null && baseline != candidate)
                {
                    throw new ServiceMantleRateLimitingConfigurationException(
                        "Registration",
                        conflicting: true);
                }

                baseline = candidate;
            }

            snapshot = baseline ?? throw new ServiceMantleRateLimitingConfigurationException(
                "Registration",
                conflicting: false);
            return snapshot;
        }
    }

    private static ServiceMantleRateLimitingSnapshot Normalize(
        ServiceMantleRateLimitingOptions options) => new(
            NormalizePolicy(options.Setup, 1, 60, "Setup"),
            NormalizePolicy(options.Management, 1, 10_000, "Management"));

    private static ServiceMantleRateLimitPolicySnapshot NormalizePolicy(
        ServiceMantleRateLimitPolicyOptions options,
        int minimumPermitLimit,
        int maximumPermitLimit,
        string fieldPrefix)
    {
        if (options.PermitLimit < minimumPermitLimit || options.PermitLimit > maximumPermitLimit)
        {
            throw Invalid($"{fieldPrefix}.PermitLimit");
        }

        if (options.Window < TimeSpan.FromSeconds(10) ||
            options.Window > TimeSpan.FromMinutes(10))
        {
            throw Invalid($"{fieldPrefix}.Window");
        }

        if (options.SegmentsPerWindow is < 1 or > 60 ||
            options.SegmentsPerWindow > options.Window.TotalSeconds)
        {
            throw Invalid($"{fieldPrefix}.SegmentsPerWindow");
        }

        return new ServiceMantleRateLimitPolicySnapshot(
            options.PermitLimit,
            options.Window,
            options.SegmentsPerWindow);
    }

    private static ServiceMantleRateLimitingConfigurationException Invalid(string fieldName) =>
        new(fieldName, conflicting: false);
}

internal sealed record ServiceMantleRateLimitingSnapshot(
    ServiceMantleRateLimitPolicySnapshot Setup,
    ServiceMantleRateLimitPolicySnapshot Management);

internal sealed record ServiceMantleRateLimitPolicySnapshot(
    int PermitLimit,
    TimeSpan Window,
    int SegmentsPerWindow)
{
    internal SlidingWindowRateLimiterOptions CreateLimiterOptions() => new()
    {
        AutoReplenishment = true,
        PermitLimit = PermitLimit,
        Window = Window,
        SegmentsPerWindow = SegmentsPerWindow,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    };
}
