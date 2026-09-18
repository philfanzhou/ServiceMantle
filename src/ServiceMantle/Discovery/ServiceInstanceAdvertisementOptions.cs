namespace ServiceMantle.Discovery;

/// <summary>
/// Carries the explicit per-instance advertised address and port of a service registration.
/// Provider neutral data only; performs no validation.
/// </summary>
/// <remarks>
/// <para>
/// A service-level registration address is shared by every instance of the service, so instances
/// behind it would all advertise the same endpoint. A consumer that knows each instance's own
/// externally reachable address and port supplies them here: when both values are present the
/// provider adapter overrides the service-level advertisement for this process, and when both are
/// absent the service-level values keep their exact existing meaning.
/// </para>
/// <para>
/// This type holds values only. Validation and the immutable runtime copy are owned by the
/// provider adapter's registration entry (for example <c>AddServiceMantleConsul</c>), which
/// validates before any descriptor that could reach a registration is written. There is
/// deliberately no auto-detection of the local address or the actually listening port: the values
/// are the caller's explicit statement of what this instance advertises.
/// </para>
/// </remarks>
public sealed class ServiceInstanceAdvertisementOptions
{
    /// <summary>
    /// Gets or sets this instance's advertised address: an IP literal, or a DNS name of 1-253
    /// characters over ASCII letters, digits, dots, and hyphens. Null when unconfigured.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Gets or sets this instance's advertised port, from 1 through 65535. Null when unconfigured.
    /// </summary>
    /// <remarks>
    /// The address and the port must be supplied together: exactly one of the two is a
    /// configuration error the registration call rejects.
    /// </remarks>
    public int? Port { get; set; }
}
