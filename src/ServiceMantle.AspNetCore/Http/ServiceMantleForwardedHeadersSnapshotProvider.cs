using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace ServiceMantle.AspNetCore;

internal sealed record ServiceMantleForwardedHeadersRegistration(
    ServiceMantleForwardedHeadersOptions Options);

internal sealed class ServiceMantleForwardedHeadersSnapshotProvider(
    IEnumerable<ServiceMantleForwardedHeadersRegistration> registrations)
{
    private readonly object sync = new();
    private ForwardedHeadersOptions? snapshot;

    internal ForwardedHeadersOptions GetRequiredSnapshot()
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

            NormalizedConfiguration? baseline = null;
            foreach (var registration in registrations)
            {
                var candidate = Normalize(registration.Options);
                if (baseline is not null && !baseline.SemanticallyEquals(candidate))
                {
                    throw Failure(
                        WellKnownForwardedHeadersConfigurationErrorCodes.ConflictingRegistration,
                        "Registration");
                }

                baseline = candidate;
            }

            snapshot = CreateFrameworkOptions(baseline ?? throw Failure(
                WellKnownForwardedHeadersConfigurationErrorCodes.TrustedProxyRequired,
                "KnownProxies/KnownIPNetworks"));
            return snapshot;
        }
    }

    private static NormalizedConfiguration Normalize(ServiceMantleForwardedHeadersOptions options)
    {
        if (options.ForwardLimit is not (>= 1 and <= 10))
        {
            throw Failure(
                WellKnownForwardedHeadersConfigurationErrorCodes.InvalidForwardLimit,
                nameof(options.ForwardLimit));
        }

        var proxies = NormalizeDistinct(
            Materialize(options.KnownProxies, nameof(options.KnownProxies)),
            nameof(options.KnownProxies),
            value => IPAddress.TryParse(value, out var address) ? address.ToString() : null);
        var networks = NormalizeDistinct(
            Materialize(options.KnownIPNetworks, nameof(options.KnownIPNetworks)),
            nameof(options.KnownIPNetworks),
            value => IPNetwork.TryParse(value, out var network) ? network.ToString() : null);
        if (proxies.Length == 0 && networks.Length == 0)
        {
            throw Failure(
                WellKnownForwardedHeadersConfigurationErrorCodes.TrustedProxyRequired,
                "KnownProxies/KnownIPNetworks");
        }

        var hosts = NormalizeDistinct(
            Materialize(options.AllowedHosts, nameof(options.AllowedHosts)),
            nameof(options.AllowedHosts),
            NormalizeHost);
        return new NormalizedConfiguration(options.ForwardLimit.Value, proxies, networks, hosts);
    }

    private static string[] Materialize(IEnumerable<string>? values, string fieldName)
    {
        if (values is null)
        {
            throw Failure(WellKnownForwardedHeadersConfigurationErrorCodes.InvalidValue, fieldName);
        }

        try
        {
            return values.ToArray();
        }
        catch
        {
            throw Failure(WellKnownForwardedHeadersConfigurationErrorCodes.EnumerationFailed, fieldName);
        }
    }

    private static string[] NormalizeDistinct(
        string[] values,
        string fieldName,
        Func<string, string?> normalize)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw Failure(WellKnownForwardedHeadersConfigurationErrorCodes.InvalidValue, fieldName);
            }

            string? candidate;
            try
            {
                candidate = normalize(value.Trim());
            }
            catch
            {
                candidate = null;
            }

            if (candidate is null)
            {
                throw Failure(WellKnownForwardedHeadersConfigurationErrorCodes.InvalidValue, fieldName);
            }

            if (!normalized.Add(candidate))
            {
                throw Failure(WellKnownForwardedHeadersConfigurationErrorCodes.DuplicateValue, fieldName);
            }
        }

        return normalized.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? NormalizeHost(string value)
    {
        if (value is "*" or "[::]" or "0.0.0.0")
        {
            return null;
        }

        var wildcard = value.StartsWith("*.", StringComparison.Ordinal);
        var host = wildcard ? value[2..] : value;
        if (host.Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            if (!host.EndsWith("]", StringComparison.Ordinal) ||
                !IPAddress.TryParse(host[1..^1], out var ipv6) ||
                ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
                ipv6.Equals(IPAddress.IPv6Any) ||
                wildcard)
            {
                return null;
            }

            return $"[{ipv6}]";
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return wildcard || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                ? null
                : address.ToString();
        }

        if (host.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var ascii = new IdnMapping().GetAscii(host).ToLowerInvariant();
        if (Uri.CheckHostName(ascii) != UriHostNameType.Dns)
        {
            return null;
        }

        return wildcard ? $"*.{ascii}" : ascii;
    }

    private static ForwardedHeadersOptions CreateFrameworkOptions(NormalizedConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardLimit = configuration.ForwardLimit,
            RequireHeaderSymmetry = true,
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in configuration.KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (var network in configuration.KnownIPNetworks)
        {
            options.KnownIPNetworks.Add(IPNetwork.Parse(network));
        }

        if (configuration.AllowedHosts.Length > 0)
        {
            options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
            options.AllowedHosts = configuration.AllowedHosts.ToList();
        }

        return options;
    }

    private static ServiceMantleForwardedHeadersConfigurationException Failure(
        string errorCode,
        string fieldName) => new(errorCode, fieldName);

    private sealed class NormalizedConfiguration(
        int forwardLimit,
        string[] knownProxies,
        string[] knownIPNetworks,
        string[] allowedHosts)
    {
        internal int ForwardLimit { get; } = forwardLimit;
        internal string[] KnownProxies { get; } = knownProxies;
        internal string[] KnownIPNetworks { get; } = knownIPNetworks;
        internal string[] AllowedHosts { get; } = allowedHosts;

        internal bool SemanticallyEquals(NormalizedConfiguration other) =>
            ForwardLimit == other.ForwardLimit &&
            KnownProxies.SequenceEqual(other.KnownProxies, StringComparer.OrdinalIgnoreCase) &&
            KnownIPNetworks.SequenceEqual(other.KnownIPNetworks, StringComparer.OrdinalIgnoreCase) &&
            AllowedHosts.SequenceEqual(other.AllowedHosts, StringComparer.OrdinalIgnoreCase);
    }
}
