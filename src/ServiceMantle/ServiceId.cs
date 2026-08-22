namespace ServiceMantle;

/// <summary>
/// Identifies a service deployment shared by all of its running instances.
/// </summary>
public sealed record ServiceId
{
    /// <summary>
    /// Gets the normalized service identifier.
    /// </summary>
    public string Value { get; }

    private ServiceId(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Parses and normalizes a service identifier.
    /// </summary>
    /// <param name="value">The service identifier to parse.</param>
    /// <returns>A validated service identifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> is not a valid service identifier.</exception>
    public static ServiceId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryNormalize(value, out var normalizedValue))
        {
            throw new FormatException("The service identifier has an invalid format.");
        }

        return new ServiceId(normalizedValue);
    }

    /// <summary>
    /// Attempts to parse and normalize a service identifier.
    /// </summary>
    /// <param name="value">The service identifier to parse.</param>
    /// <param name="serviceId">The parsed service identifier, or null when parsing fails.</param>
    /// <returns>true when the value is valid; otherwise, false.</returns>
    public static bool TryParse(string? value, out ServiceId? serviceId)
    {
        if (value is null || !TryNormalize(value, out var normalizedValue))
        {
            serviceId = null;
            return false;
        }

        serviceId = new ServiceId(normalizedValue);
        return true;
    }

    /// <summary>
    /// Returns the normalized service identifier.
    /// </summary>
    public override string ToString() => Value;

    private static bool TryNormalize(string value, out string normalizedValue)
    {
        normalizedValue = string.Empty;
        var candidate = value.Trim().ToLowerInvariant();

        if (candidate.Length is < 1 or > 128 || !IsAsciiLetterOrDigit(candidate[0]))
        {
            return false;
        }

        for (var index = 1; index < candidate.Length; index++)
        {
            var character = candidate[index];
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        normalizedValue = candidate;
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
