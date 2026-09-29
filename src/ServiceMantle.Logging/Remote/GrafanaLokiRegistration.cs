using ServiceMantle.Logging.Pipeline;

namespace ServiceMantle.Logging.Remote;

internal sealed record GrafanaLokiRegistration(GrafanaLokiOptions Options);

internal sealed record GrafanaLokiConfiguration(
    bool Enabled,
    Uri? Endpoint,
    string? AuthorizationHeaderResolverName,
    int BatchSize,
    int QueueLimit,
    TimeSpan FlushPeriod,
    TimeSpan ShutdownDrainTimeout,
    IReadOnlyDictionary<string, string> Labels)
{
    // Record equality compares collection members by reference, so equivalent registrations with
    // the same label set in a different insertion order would falsely conflict. Equality here is
    // key-order independent; equal label sets always have an equal member count, which keeps the
    // hash code consistent.
    public bool Equals(GrafanaLokiConfiguration? other) =>
        other is not null &&
        Enabled == other.Enabled &&
        EqualityComparer<Uri?>.Default.Equals(Endpoint, other.Endpoint) &&
        string.Equals(AuthorizationHeaderResolverName, other.AuthorizationHeaderResolverName, StringComparison.Ordinal) &&
        BatchSize == other.BatchSize &&
        QueueLimit == other.QueueLimit &&
        FlushPeriod == other.FlushPeriod &&
        ShutdownDrainTimeout == other.ShutdownDrainTimeout &&
        Labels.Count == other.Labels.Count &&
        Labels.All(pair =>
            other.Labels.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

    public override int GetHashCode() => HashCode.Combine(
        Enabled,
        Endpoint,
        AuthorizationHeaderResolverName,
        BatchSize,
        QueueLimit,
        FlushPeriod,
        ShutdownDrainTimeout,
        Labels.Count);

    // The default record ToString would render the label dictionary; diagnostics must not echo
    // configured label values.
    public override string ToString() =>
        $"GrafanaLokiConfiguration {{ Enabled = {Enabled}, LabelCount = {Labels.Count} }}";
}

internal sealed class GrafanaLokiConfigurationProvider(
    IEnumerable<GrafanaLokiRegistration> registrations)
{
    private readonly object sync = new();
    private GrafanaLokiConfiguration? snapshot;

    internal GrafanaLokiConfiguration GetRequiredConfiguration()
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

            GrafanaLokiConfiguration? baseline = null;
            foreach (var registration in registrations)
            {
                var candidate = Normalize(registration.Options);
                if (baseline is not null && baseline != candidate)
                {
                    throw Failure("Registration", WellKnownGrafanaLokiErrorCodes.ConflictingRegistration);
                }

                baseline = candidate;
            }

            snapshot = baseline ?? DisabledConfiguration();
            return snapshot;
        }
    }

    private static GrafanaLokiConfiguration Normalize(
        GrafanaLokiOptions options)
    {
        if (!options.Enabled)
        {
            return DisabledConfiguration();
        }

        var endpoint = options.Endpoint;
        if (endpoint is null ||
            !endpoint.IsAbsoluteUri ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
                !(endpoint.Scheme == Uri.UriSchemeHttp &&
                  (options.AllowInsecureHttp ||
                    (options.AllowInsecureLoopbackForTesting && endpoint.IsLoopback)))))
        {
            throw Failure(nameof(options.Endpoint), WellKnownGrafanaLokiErrorCodes.InvalidEndpoint);
        }

        // An unset resolver name means no authentication: no name validation, no resolver
        // registration, and no Authorization header. Any explicitly set value - including
        // whitespace or invalid characters - keeps the strict format validation below.
        var resolverName = options.AuthorizationHeaderResolverName?.Trim();
        if (resolverName is not null &&
            (resolverName is not { Length: >= 1 and <= 128 } ||
             resolverName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))))
        {
            throw Failure(
                nameof(options.AuthorizationHeaderResolverName),
                WellKnownGrafanaLokiErrorCodes.InvalidAuthorizationResolverName);
        }

        // Labels are copied into an isolated snapshot: later mutation of the options dictionary
        // cannot change the running sink. Duplicate keys are structurally unrepresentable in an
        // IDictionary<string, string>; validation covers key syntax, reserved keys, value shape,
        // and the total count, without echoing any key or value in the failure.
        var labels = NormalizeLabels(options.Labels);

        if (options.BatchSize is < 1 or > 1_000)
        {
            throw Failure(nameof(options.BatchSize), WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting);
        }

        if (options.QueueLimit is < 100 or > 50_000)
        {
            throw Failure(nameof(options.QueueLimit), WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting);
        }

        if (options.FlushPeriod < TimeSpan.FromSeconds(1) ||
            options.FlushPeriod > TimeSpan.FromSeconds(30))
        {
            throw Failure(nameof(options.FlushPeriod), WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting);
        }

        if (options.ShutdownDrainTimeout < TimeSpan.FromSeconds(1) ||
            options.ShutdownDrainTimeout > TimeSpan.FromSeconds(30))
        {
            throw Failure(
                nameof(options.ShutdownDrainTimeout),
                WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting);
        }

        return new(
            true,
            new Uri(endpoint.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped)),
            resolverName,
            options.BatchSize,
            options.QueueLimit,
            options.FlushPeriod,
            options.ShutdownDrainTimeout,
            labels);
    }

    private static IReadOnlyDictionary<string, string> NormalizeLabels(
        IDictionary<string, string>? labels)
    {
        if (labels is null)
        {
            return new Dictionary<string, string>(0, StringComparer.Ordinal);
        }

        if (labels.Count is < 1 or > GrafanaLokiDefaults.MaxLabelCount)
        {
            throw Failure(nameof(GrafanaLokiOptions.Labels), WellKnownGrafanaLokiErrorCodes.InvalidLabels);
        }

        var snapshot = new Dictionary<string, string>(labels.Count, StringComparer.Ordinal);
        foreach (var (key, value) in labels)
        {
            if (key is not { Length: >= 1 and <= GrafanaLokiDefaults.MaxLabelKeyLength } ||
                key.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character == '_')) ||
                char.IsDigit(key[0]) ||
                GrafanaLokiDefaults.ReservedLabelKeys.Contains(key))
            {
                throw Failure(
                    nameof(GrafanaLokiOptions.Labels),
                    WellKnownGrafanaLokiErrorCodes.InvalidLabels);
            }

            if (value is not { Length: >= 1 and <= GrafanaLokiDefaults.MaxLabelValueLength } ||
                value.Any(char.IsControl))
            {
                throw Failure(
                    nameof(GrafanaLokiOptions.Labels),
                    WellKnownGrafanaLokiErrorCodes.InvalidLabels);
            }

            snapshot[key] = value;
        }

        return snapshot;
    }

    private static GrafanaLokiConfiguration DisabledConfiguration() => new(
        false,
        null,
        null,
        GrafanaLokiDefaults.BatchSize,
        GrafanaLokiDefaults.QueueLimit,
        GrafanaLokiDefaults.FlushPeriod,
        GrafanaLokiDefaults.ShutdownDrainTimeout,
        new Dictionary<string, string>(0, StringComparer.Ordinal));

    internal static SerilogConfigurationException Failure(
        string fieldName,
        string errorCode) => new(fieldName, errorCode);
}
