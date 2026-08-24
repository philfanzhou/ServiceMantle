using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class ManagementAuditRecordTests
{
    [Fact]
    public void ToString_never_includes_description_or_metadata()
    {
        var record = new ManagementAuditRecord(
            Guid.NewGuid(),
            ManagementAuditOperator.System(),
            WellKnownManagementAuditActions.ConfigurationChanged,
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp"),
            ManagementAuditOutcome.Success,
            DateTimeOffset.UtcNow,
            clientIp: "203.0.113.7",
            correlationId: "corr-1",
            securityDescription: "SecretDescriptionMarker",
            metadata: new Dictionary<string, string> { ["k"] = "SecretMetadataMarker" });

        var text = record.ToString();

        Assert.DoesNotContain("SecretDescriptionMarker", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretMetadataMarker", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_null_operator()
    {
        Assert.Throws<ArgumentNullException>(() => new ManagementAuditRecord(
            Guid.NewGuid(),
            null!,
            WellKnownManagementAuditActions.ConfigurationChanged,
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp"),
            ManagementAuditOutcome.Unknown,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new Dictionary<string, string>()));
    }
}
