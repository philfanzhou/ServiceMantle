using System.Threading.RateLimiting;

namespace ServiceMantle.AspNetCore.RateLimiting;

internal sealed record RateLimitingRegistration(
    RateLimitingOptions Options);

internal sealed class RateLimitingSnapshotProvider(
    IEnumerable<RateLimitingRegistration> registrations)
{
    private readonly object sync = new();
    private RateLimitingSnapshot? snapshot;

    internal RateLimitingSnapshot GetRequiredSnapshot()
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

            RateLimitingSnapshot? baseline = null;
            foreach (var registration in registrations)
            {
                var candidate = Normalize(registration.Options);
                if (baseline is not null && baseline != candidate)
                {
                    throw new RateLimitingConfigurationException(
                        "Registration",
                        conflicting: true);
                }

                baseline = candidate;
            }

            snapshot = baseline ?? throw new RateLimitingConfigurationException(
                "Registration",
                conflicting: false);
            return snapshot;
        }
    }

    private static RateLimitingSnapshot Normalize(
        RateLimitingOptions options) => new(
            NormalizePolicy(options.Setup, 1, 60, "Setup"),
            NormalizePolicy(options.Management, 1, 10_000, "Management"));

    private static RateLimitPolicySnapshot NormalizePolicy(
        RateLimitPolicyOptions options,
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

        return new RateLimitPolicySnapshot(
            options.PermitLimit,
            options.Window,
            options.SegmentsPerWindow);
    }

    private static RateLimitingConfigurationException Invalid(string fieldName) =>
        new(fieldName, conflicting: false);
}

internal sealed record RateLimitingSnapshot(
    RateLimitPolicySnapshot Setup,
    RateLimitPolicySnapshot Management);

internal sealed record RateLimitPolicySnapshot(
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
