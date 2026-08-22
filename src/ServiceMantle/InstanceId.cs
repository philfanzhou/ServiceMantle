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
