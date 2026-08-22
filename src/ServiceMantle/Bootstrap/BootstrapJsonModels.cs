using System.Text.Json.Serialization;

namespace ServiceMantle.Bootstrap;

internal sealed class BootstrapJsonDocument
{
    public int? FormatVersion { get; set; }

    public string? ServiceId { get; set; }

    public BootstrapJsonDatabase? Database { get; set; }

    public string? MasterKey { get; set; }
}

internal sealed class BootstrapJsonDatabase
{
    public string? Provider { get; set; }

    public string? ServerVersion { get; set; }

    public string? ConnectionString { get; set; }
}
