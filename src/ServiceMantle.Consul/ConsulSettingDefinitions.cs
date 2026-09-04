using ServiceMantle.Configuration;

namespace ServiceMantle.Consul;

/// <summary>Defines the restart-bound Consul catalog and enabled-only combination validation.</summary>
public sealed class ConsulSettingDefinitions : IServiceSettingDefinitionProvider, IServiceSettingCompositeValidator
{
    /// <summary>Enables explicit client creation; defaults to false.</summary>
    public const string Enabled = "consul.enabled";
    /// <summary>The root HTTPS agent URI, or a loopback HTTP URI.</summary>
    public const string Endpoint = "consul.endpoint";
    /// <summary>The optional encrypted ACL token; never has a plaintext default.</summary>
    public const string Token = "consul.token";
    /// <summary>The DNS-compatible Consul service name.</summary>
    public const string ServiceName = "consul.service-name";
    /// <summary>The advertised DNS name or IP address.</summary>
    public const string Address = "consul.address";
    /// <summary>The advertised integer port from 1 through 65535.</summary>
    public const string Port = "consul.port";
    /// <summary>The root-relative health path, defaulting to /health/ready.</summary>
    public const string HealthPath = "consul.health-path";
    /// <summary>The health URL scheme, http or https; defaults to http.</summary>
    public const string HealthScheme = "consul.health-scheme";

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
