using System.Net;
using ServiceMantle.Configuration;

namespace ServiceMantle.Consul;

internal sealed class ConsulSnapshotBinding
{
    internal required Uri Endpoint { get; init; }
    internal required string? Token { get; init; }
    internal required string Name { get; init; }
    internal required string Address { get; init; }
    internal required int Port { get; init; }
    internal required Uri HealthUri { get; init; }

    // The same invariant is checked before activation and at the consumer boundary. The latter
    // also rejects snapshots made with a replacement catalog that weakens the token definition.
    // The optional instance-level advertisement overrides only the final Address, Port, and
    // HealthUri: the service-level values are still read and validated, because the combination
    // validation must not depend on whether this process configured an advertisement.
    internal static ConsulSnapshotBinding? Read(
        IReadOnlyDictionary<string, ServiceSettingValue> values,
        string? instanceAddress = null,
        int? instancePort = null)
    {
        var enabled = Required(values, ConsulSettingDefinitions.Enabled, ServiceSettingValueType.Boolean);
        if (!enabled.GetBoolean())
        {
            return null;
        }

        foreach (var definition in new ConsulSettingDefinitions().GetDefinitions())
        {
            if (!values.TryGetValue(definition.Key, out var value) ||
                value.ValueType != definition.ValueType ||
                value.IsSensitive != definition.IsSensitive ||
                (definition.IsSensitive && value.Definition.DefaultValue is not null))
            {
                throw Invalid();
            }
        }

        var endpointText = Text(values, ConsulSettingDefinitions.Endpoint);
        if (endpointText.Length > 2048 || endpointText.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath != "/" ||
            (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback)))
        {
            throw Invalid();
        }

        var tokenValue = values[ConsulSettingDefinitions.Token];
        var token = tokenValue.HasValue ? tokenValue.GetString() : null;
        if (token is not null && (token.Length is < 1 or > 4096 || token.Any(c => c < '!' || c > '~')))
        {
            throw Invalid();
        }

        var name = Text(values, ConsulSettingDefinitions.ServiceName);
        if (name.Length is < 1 or > 63 || !char.IsAsciiLetterOrDigit(name[0]) ||
            !char.IsAsciiLetterOrDigit(name[^1]) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw Invalid();
        }

        var serviceAddress = Text(values, ConsulSettingDefinitions.Address);
        if (!IsValidAdvertisedAddress(serviceAddress))
        {
            throw Invalid();
        }

        var number = Required(values, ConsulSettingDefinitions.Port, ServiceSettingValueType.Number).GetNumber();
        if (number < 1 || number > 65535 || decimal.Truncate(number) != number)
        {
            throw Invalid();
        }

        var path = Text(values, ConsulSettingDefinitions.HealthPath);
        var scheme = Text(values, ConsulSettingDefinitions.HealthScheme);
        if (path.Length is < 1 or > 512 || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '-' and not '_') ||
            scheme is not "http" and not "https")
        {
            throw Invalid();
        }

        // The instance-level advertisement, when configured, wins over the service-level fallback
        // for exactly these three fields; Id, Name, endpoint, and token never change.
        var address = instanceAddress ?? serviceAddress;
        var port = instancePort ?? (int)number;
        return new()
        {
            Endpoint = endpoint,
            Token = token,
            Name = name,
            Address = address,
            Port = port,
            HealthUri = new UriBuilder(scheme, address, port, path).Uri
        };
    }

    /// <summary>
    /// The one address rule the service-level setting and the instance-level advertisement share:
    /// an IP literal, or a DNS name of 1-253 characters over ASCII letters, digits, dots, and
    /// hyphens.
    /// </summary>
    internal static bool IsValidAdvertisedAddress(string address) =>
        address.Length is >= 1 and <= 253 &&
        (IPAddress.TryParse(address, out _) ||
            (Uri.CheckHostName(address) == UriHostNameType.Dns &&
                address.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')));

    /// <summary>The one port rule both levels share: 1 through 65535.</summary>
    internal static bool IsValidAdvertisedPort(int port) => port is >= 1 and <= 65535;

    private static string Text(IReadOnlyDictionary<string, ServiceSettingValue> values, string key) =>
        Required(values, key, ServiceSettingValueType.String).GetString();

    private static ServiceSettingValue Required(
        IReadOnlyDictionary<string, ServiceSettingValue> values, string key, ServiceSettingValueType type)
    {
        if (!values.TryGetValue(key, out var value) || !value.HasValue || value.ValueType != type || value.IsSensitive)
        {
            throw Invalid();
        }
        return value;
    }

    internal static ConsulConfigurationException Invalid() => new(ConsulConfigurationError.InvalidConfiguration);
}
