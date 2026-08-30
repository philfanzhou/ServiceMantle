namespace ServiceMantle.Persistence.EntityFrameworkCore;

internal sealed class ServiceSettingEntity
{
    public string ServiceId { get; set; } = string.Empty;

    public string ValuesJson { get; set; } = "{}";

    public long Version { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public bool RestartRequired { get; set; }
}
