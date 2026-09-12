using ServiceMantle.Configuration;

namespace ServiceMantle.Consul;

/// <summary>Defines the restart-bound Consul catalog and enabled-only combination validation.</summary>
public sealed class ConsulSettingDefinitions : IServiceSettingDefinitionProvider, IServiceSettingCompositeValidator
{
    /// <summary>Enables explicit client creation; defaults to false.</summary>
    public const string Enabled = "discovery.enabled";
    /// <summary>The root HTTPS agent URI, or a loopback HTTP URI.</summary>
    public const string Endpoint = "discovery.endpoint";
    /// <summary>
    /// The optional encrypted provider-defined credential string; never has a plaintext default.
    /// This Consul adapter uses it as the ACL token.
    /// </summary>
    public const string Token = "discovery.credential";
    /// <summary>The DNS-compatible Consul service name.</summary>
    public const string ServiceName = "discovery.service-name";
    /// <summary>The advertised DNS name or IP address.</summary>
    public const string Address = "discovery.address";
    /// <summary>The advertised integer port from 1 through 65535.</summary>
    public const string Port = "discovery.port";
    /// <summary>The root-relative health path, defaulting to /health/ready.</summary>
    public const string HealthPath = "discovery.health-path";
    /// <summary>The health URL scheme, http or https; defaults to http.</summary>
    public const string HealthScheme = "discovery.health-scheme";

    /// <inheritdoc />
    public IEnumerable<ServiceSettingDefinition> GetDefinitions() =>
    [
        new(Enabled, ServiceSettingValueType.Boolean, defaultValue: "false", requiresRestart: true),
        new(Endpoint, ServiceSettingValueType.String, requiresRestart: true),
        new(Token, ServiceSettingValueType.String, isSensitive: true, requiresRestart: true),
        new(ServiceName, ServiceSettingValueType.String, requiresRestart: true),
        new(Address, ServiceSettingValueType.String, requiresRestart: true),
        new(Port, ServiceSettingValueType.Number, requiresRestart: true),
        new(HealthPath, ServiceSettingValueType.String, defaultValue: "/health/ready", requiresRestart: true),
        new(HealthScheme, ServiceSettingValueType.String, defaultValue: "http", requiresRestart: true)
    ];

    /// <inheritdoc />
    public IEnumerable<ServiceSettingValidationError> Validate(ServiceSettingValidationContext context)
    {
        try
        {
            ConsulSnapshotBinding.Read(context.Values);
            return [];
        }
        catch
        {
            return [new ServiceSettingValidationError(null, "consul.invalid_configuration")];
        }
    }
}
