using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class ManagementAuditOperatorSourceTests
{
    [Fact]
    public void Parse_trims_and_normalizes_to_lowercase()
    {
        var source = ManagementAuditOperatorSource.Parse("  Interactive_Admin  ");

        Assert.Equal("interactive_admin", source.Value);
    }

    [Fact]
    public void TryParse_rejects_value_exceeding_max_length()
    {
        var tooLong = new string('a', ManagementAuditOperatorSource.MaxLength + 1);

        Assert.False(ManagementAuditOperatorSource.TryParse(tooLong, out var source));
        Assert.Null(source);
    }

    [Fact]
    public void Parse_throws_management_audit_exception_with_stable_error_code()
    {
        var exception = Assert.Throws<ManagementAuditException>(() => ManagementAuditOperatorSource.Parse(" "));

        Assert.Equal("audit.operator_source_invalid", exception.ErrorCode);
    }
}
