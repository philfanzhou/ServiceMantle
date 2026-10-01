namespace ServiceMantle;

/// <summary>
/// Identifies a running service instance for diagnostics and runtime telemetry.
/// </summary>
public sealed record InstanceId
{
    /// <summary>
    /// Gets the instance identifier.
    /// </summary>
    public string Value { get; }

    private InstanceId(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Parses an instance identifier.
    /// </summary>
    /// <param name="value">The instance identifier to parse.</param>
    /// <returns>A validated instance identifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> is not a valid instance identifier.</exception>
    public static InstanceId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryNormalize(value, out var normalizedValue))
        {
            throw new FormatException("The instance identifier has an invalid format.");
        }

        return new InstanceId(normalizedValue);
    }

    /// <summary>
    /// Attempts to parse an instance identifier.
    /// </summary>
    /// <param name="value">The instance identifier to parse.</param>
    /// <param name="instanceId">The parsed instance identifier, or null when parsing fails.</param>
    /// <returns>true when the value is valid; otherwise, false.</returns>
    public static bool TryParse(string? value, out InstanceId? instanceId)
    {
        if (value is null || !TryNormalize(value, out var normalizedValue))
        {
            instanceId = null;
            return false;
        }

        instanceId = new InstanceId(normalizedValue);
        return true;
    }

    /// <summary>
    /// Creates a fresh random instance identifier for the given service: the normalized service
    /// identifier, a hyphen, and 32 lowercase hexadecimal characters from a
    /// <see cref="Guid.NewGuid()"/>-grade random source.
    /// </summary>
    /// <param name="serviceId">The identity shared by all instances of the service.</param>
    /// <returns>A new instance identifier distinct on every call.</returns>
    /// <remarks>
    /// <para>
    /// The generated value always satisfies <see cref="Parse"/> and round-trips through it; its
    /// prefix is exactly <see cref="ServiceId.Value"/> after normalization. Deployments that need
    /// a stable instance identity across restarts supply their own <see cref="InstanceId"/>
    /// instead: a generated value is new on every process start and guarantees uniqueness beyond
    /// collision probability only.
    /// </para>
    /// <para>
    /// The random part contains no machine name, address, process id, or other environment
    /// information.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="serviceId"/> is null.</exception>
    public static InstanceId CreateRandom(ServiceId serviceId)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        return new InstanceId($"{serviceId.Value}-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Returns the instance identifier.
    /// </summary>
    public override string ToString() => Value;

    private static bool TryNormalize(string value, out string normalizedValue)
    {
        normalizedValue = string.Empty;

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        var candidate = value.Trim();
        if (candidate.Length is < 1 or > 256)
        {
            return false;
        }

        normalizedValue = candidate;
        return true;
    }
}
