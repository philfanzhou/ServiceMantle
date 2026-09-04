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
    internal static ConsulSnapshotBinding? Read(IReadOnlyDictionary<string, ServiceSettingValue> values)
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

        var address = Text(values, ConsulSettingDefinitions.Address);
        if (address.Length is < 1 or > 253 ||
            (!IPAddress.TryParse(address, out _) &&
             (Uri.CheckHostName(address) != UriHostNameType.Dns ||
              address.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))))
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

        return new()
        {
            Endpoint = endpoint,
            Token = token,
            Name = name,
            Address = address,
            Port = (int)number,
            HealthUri = new UriBuilder(scheme, address, (int)number, path).Uri
        };
    }

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
