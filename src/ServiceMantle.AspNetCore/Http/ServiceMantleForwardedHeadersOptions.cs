namespace ServiceMantle.AspNetCore;

/// <summary>Defines the explicit trust boundary for ServiceMantle forwarded headers.</summary>
public sealed class ServiceMantleForwardedHeadersOptions
{
    /// <summary>Gets or sets trusted individual IPv4 or IPv6 proxy addresses.</summary>
    public IEnumerable<string>? KnownProxies { get; set; } = [];

    /// <summary>Gets or sets trusted IPv4 or IPv6 networks in CIDR notation.</summary>
    public IEnumerable<string>? KnownIPNetworks { get; set; } = [];

    /// <summary>Gets or sets concrete or <c>*.example.com</c> forwarded hosts.</summary>
    public IEnumerable<string>? AllowedHosts { get; set; } = [];

    /// <summary>Gets or sets the number of rightmost header entries to process, from 1 through 10.</summary>
    public int? ForwardLimit { get; set; } = 1;
}
