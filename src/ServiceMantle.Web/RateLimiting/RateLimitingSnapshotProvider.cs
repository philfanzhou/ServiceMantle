using System.Threading.RateLimiting;

namespace ServiceMantle.Web.RateLimiting;

internal sealed record RateLimitingRegistration(
    RateLimitingOptions Options);

internal sealed class RateLimitingSnapshotProvider(
    IEnumerable<RateLimitingRegistration> registrations)
{
    internal const string ReservedPolicyNamePrefix = "servicemantle.";
    private const int MaximumPolicyNameLength = 128;

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
                if (baseline is not null && !baseline.Equals(candidate))
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
            NormalizePolicy(options.Management, 1, 10_000, "Management"),
            NormalizeConsumerPolicies(options.ConsumerPolicies));

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

    /// <summary>
    /// Rejects reserved or malformed policy names synchronously at registration time, before any
    /// policy descriptor is written, so a name collision never surfaces as an upstream
    /// argument exception.
    /// </summary>
    internal static void ValidateConsumerPolicyNames(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            ValidatePolicyName(name);
        }
    }

    private static RateLimitConsumerPolicySnapshot[] NormalizeConsumerPolicies(
        IDictionary<string, RateLimitPolicyOptions> policies)
    {
        var normalized = new List<RateLimitConsumerPolicySnapshot>(policies.Count);
        foreach (var (name, options) in policies)
        {
            // The invalid-name error deliberately does not echo the submitted name: a malformed
            // name may contain unsafe characters, so the field name stays generic.
            ValidatePolicyName(name);
            const string fieldPrefix = "ConsumerPolicies";
            // The consumer policy bounds match the management policy: a public endpoint quota
            // may legitimately be large, but it stays bounded.
            normalized.Add(new RateLimitConsumerPolicySnapshot(
                name,
                NormalizePolicy(options, 1, 10_000, fieldPrefix)));
        }

        // A stable order keeps equivalent registrations comparable regardless of insertion order.
        normalized.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return [.. normalized];
    }

    private static void ValidatePolicyName(string name)
    {
        if (name.Length is < 1 or > MaximumPolicyNameLength ||
            name.StartsWith(ReservedPolicyNamePrefix, StringComparison.OrdinalIgnoreCase) ||
            name.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
        {
            throw Invalid("ConsumerPolicies.Name");
        }
    }

    private static RateLimitingConfigurationException Invalid(string fieldName) =>
        new(fieldName, conflicting: false);
}

internal sealed record RateLimitingSnapshot(
    RateLimitPolicySnapshot Setup,
    RateLimitPolicySnapshot Management,
    RateLimitConsumerPolicySnapshot[] Consumer)
{
    // Records compare collection members by reference, so two equivalent registrations that each
    // built their own consumer-policy dictionary would falsely conflict. Equality is order- and
    // instance-independent over the normalized, order-stable policy list.
    public bool Equals(RateLimitingSnapshot? other) =>
        other is not null &&
        Setup.Equals(other.Setup) &&
        Management.Equals(other.Management) &&
        Consumer.Length == other.Consumer.Length &&
        Consumer.Zip(other.Consumer, (left, right) =>
            string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            left.Policy.Equals(right.Policy))
            .All(equal => equal);

    public override int GetHashCode() => HashCode.Combine(Setup, Management, Consumer.Length);
}

internal sealed record RateLimitConsumerPolicySnapshot(
    string Name,
    RateLimitPolicySnapshot Policy);

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
