namespace ServiceMantle.Installation;

/// <summary>
/// The validated lifetime applied to a newly issued Setup Code.
/// </summary>
/// <remarks>
/// The configurable range is the closed interval from 5 minutes to 24 hours, and the default is
/// 30 minutes. An out-of-range configuration is a programming error, not a domain rejection.
/// </remarks>
public sealed class SetupCodeLifetime
{
    /// <summary>
    /// The smallest configurable lifetime.
    /// </summary>
    public static readonly TimeSpan MinimumValue = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The largest configurable lifetime.
    /// </summary>
    public static readonly TimeSpan MaximumValue = TimeSpan.FromHours(24);

    /// <summary>
    /// The lifetime applied when none is configured.
    /// </summary>
    public static readonly TimeSpan DefaultValue = TimeSpan.FromMinutes(30);

    private SetupCodeLifetime(TimeSpan value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the default lifetime.
    /// </summary>
    public static SetupCodeLifetime Default { get; } = new(DefaultValue);

    /// <summary>
    /// Gets the configured lifetime.
    /// </summary>
    public TimeSpan Value { get; }

    /// <summary>
    /// Creates a validated lifetime.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is outside the closed interval from <see cref="MinimumValue"/> to
    /// <see cref="MaximumValue"/>.
    /// </exception>
    public static SetupCodeLifetime Create(TimeSpan value)
    {
        if (value < MinimumValue || value > MaximumValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"A Setup Code lifetime must be between {MinimumValue} and {MaximumValue}.");
        }

        return new SetupCodeLifetime(value);
    }

    /// <summary>
    /// Returns the configured lifetime.
    /// </summary>
    public override string ToString() => $"SetupCodeLifetime({Value})";
}
