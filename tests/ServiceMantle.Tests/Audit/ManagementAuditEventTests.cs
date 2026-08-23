using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class ManagementAuditEventTests
{
    private static readonly ManagementAuditOperator Operator = ManagementAuditOperator.Create(
        WellKnownManagementAuditOperatorSources.InteractiveAdmin,
        operatorId: "admin-1",
        displayName: "Alex Admin");

    private static readonly ManagementAuditTarget Target =
        ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Service, "signacore");

    [Fact]
    public void Create_populates_all_fields()
    {
        var occurredAt = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.AdminLoginSucceeded,
            Target,
            ManagementAuditOutcome.Success,
            occurredAt,
            clientIp: "203.0.113.7",
            correlationId: "corr-123",
            securityDescription: "Admin signed in from a trusted network.",
            metadata: new Dictionary<string, string> { ["reason"] = "scheduled-review" });

        Assert.Equal(Operator, auditEvent.Operator);
        Assert.Equal(WellKnownManagementAuditActions.AdminLoginSucceeded, auditEvent.Action);
        Assert.Equal(Target, auditEvent.Target);
        Assert.Equal(ManagementAuditOutcome.Success, auditEvent.Outcome);
        Assert.Equal(occurredAt, auditEvent.OccurredAtUtc);
        Assert.Equal("203.0.113.7", auditEvent.ClientIp);
        Assert.Equal("corr-123", auditEvent.CorrelationId);
        Assert.Equal("Admin signed in from a trusted network.", auditEvent.SecurityDescription);
        Assert.Equal("scheduled-review", auditEvent.Metadata["reason"]);
    }

    [Fact]
    public void Create_defaults_occurred_at_to_time_provider_now()
    {
        var fixedTime = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(fixedTime);

        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            timeProvider: timeProvider);

        Assert.Equal(fixedTime, auditEvent.OccurredAtUtc);
    }

    [Fact]
    public void Create_defaults_outcome_to_unknown()
    {
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target);

        Assert.Equal(ManagementAuditOutcome.Unknown, auditEvent.Outcome);
    }

    [Fact]
    public void Create_rejects_undefined_outcome()
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditEvent.Create(
                Operator,
                WellKnownManagementAuditActions.ConfigurationChanged,
                Target,
                (ManagementAuditOutcome)99));

        Assert.Equal("audit.outcome_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_strips_control_characters_from_description()
    {
        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            securityDescription: "line one\r\nline two\ttabbed");

        Assert.NotNull(auditEvent.SecurityDescription);
        Assert.DoesNotContain('\r', auditEvent.SecurityDescription);
        Assert.DoesNotContain('\n', auditEvent.SecurityDescription);
    }

    [Fact]
    public void Create_treats_whitespace_only_description_as_absent()
    {
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target, securityDescription: "   ");

        Assert.Null(auditEvent.SecurityDescription);
    }

    [Fact]
    public void Create_rejects_description_exceeding_max_length()
    {
        var tooLong = new string('a', ManagementAuditEvent.MaxDescriptionLength + 1);

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditEvent.Create(
                Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target,
                securityDescription: tooLong));

        Assert.Equal("audit.description_invalid", exception.ErrorCode);
    }

    [Theory]
    [InlineData("Server=db;Password=super-secret;")]
    [InlineData("pwd=hunter2 for the admin account")]
    [InlineData("token=abcdefghijklmnop was issued")]
    public void Create_redacts_secret_shaped_substrings_in_description(string rawDescription)
    {
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target,
            securityDescription: rawDescription);

        Assert.DoesNotContain("super-secret", auditEvent.SecurityDescription);
        Assert.DoesNotContain("hunter2", auditEvent.SecurityDescription);
        Assert.DoesNotContain("abcdefghijklmnop", auditEvent.SecurityDescription);
        Assert.Contains("[REDACTED]", auditEvent.SecurityDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_redacts_jwt_like_token_in_description()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";

        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target,
            securityDescription: $"Rotated exchange token {jwt} for downstream client");

        Assert.DoesNotContain(jwt, auditEvent.SecurityDescription);
        Assert.Contains("[REDACTED_TOKEN]", auditEvent.SecurityDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_redacts_pem_private_key_block_in_description()
    {
        const string pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJ...\n-----END RSA PRIVATE KEY-----";

        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target,
            securityDescription: $"Uploaded key: {pem}");

        Assert.DoesNotContain("MIIBOgIBAAJ", auditEvent.SecurityDescription);
        Assert.Contains("[REDACTED_KEY]", auditEvent.SecurityDescription, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("db_password")]
    [InlineData("apiKey")]
    [InlineData("connectionString")]
    [InlineData("SetupCode")]
    [InlineData("client_secret")]
    [InlineData("Authorization")]
    public void Create_rejects_metadata_with_sensitive_key(string sensitiveKey)
    {
        var metadata = new Dictionary<string, string> { [sensitiveKey] = "anything" };

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditEvent.Create(
                Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target, metadata: metadata));

        Assert.Equal("audit.metadata_key_rejected", exception.ErrorCode);
    }

    [Fact]
    public void Create_redacts_secret_shaped_substrings_in_metadata_values()
    {
        var metadata = new Dictionary<string, string>
        {
            ["connection_note"] = "Server=db;Password=super-secret;"
        };

        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target, metadata: metadata);

        Assert.DoesNotContain("super-secret", auditEvent.Metadata["connection_note"]);
    }

    [Fact]
    public void Create_rejects_metadata_exceeding_max_entry_count()
    {
        var metadata = Enumerable.Range(0, ManagementAuditEvent.MaxMetadataEntries + 1)
            .ToDictionary(i => $"key{i}", i => "value");

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditEvent.Create(
                Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target, metadata: metadata));

        Assert.Equal("audit.metadata_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_metadata_value_exceeding_max_length()
    {
        var metadata = new Dictionary<string, string>
        {
            ["note"] = new string('a', ManagementAuditEvent.MaxMetadataValueLength + 1)
        };

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditEvent.Create(
                Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target, metadata: metadata));

        Assert.Equal("audit.metadata_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_with_no_metadata_yields_empty_dictionary()
    {
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target);

        Assert.Empty(auditEvent.Metadata);
    }

    [Fact]
    public void Create_rejects_client_ip_exceeding_max_length()
    {
        var tooLong = new string('1', ManagementAuditEvent.MaxClientIpLength + 1);

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditEvent.Create(
                Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target, clientIp: tooLong));

        Assert.Equal("audit.client_ip_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_correlation_id_exceeding_max_length()
    {
        var tooLong = new string('a', ManagementAuditEvent.MaxCorrelationIdLength + 1);

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditEvent.Create(
                Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target, correlationId: tooLong));

        Assert.Equal("audit.correlation_id_invalid", exception.ErrorCode);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
