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
    public void Create_normalizes_supplied_occurrence_time_to_utc()
    {
        var localTime = new DateTimeOffset(2026, 8, 1, 20, 0, 0, TimeSpan.FromHours(8));

        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            occurredAtUtc: localTime);

        Assert.Equal(TimeSpan.Zero, auditEvent.OccurredAtUtc.Offset);
        Assert.Equal(localTime.UtcDateTime, auditEvent.OccurredAtUtc.UtcDateTime);
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
    [InlineData("setup-code")]
    [InlineData("connection-string")]
    [InlineData("client-secret")]
    [InlineData("pass_word")]
    [InlineData("pass\u200Bword")]
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
    public void Create_rejects_non_ascii_metadata_key_that_could_hide_a_sensitive_name()
    {
        var metadata = new Dictionary<string, string>
        {
            ["p\u0430ssword"] = "clear-text"
        };

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

    [Theory]
    [InlineData("{\"password\":\"hunter2\"}", "hunter2")]
    [InlineData("password: hunter2", "hunter2")]
    [InlineData("rootKey: root-secret", "root-secret")]
    [InlineData("setupCode: setup-secret", "setup-secret")]
    [InlineData("connectionString: Host=db;Database=prod", "Host=db")]
    [InlineData("Password=\"abc def\"", "abc def")]
    [InlineData("Host=db;Database=prod;Username=admin", "Username=admin")]
    [InlineData("postgresql://admin:uri-secret@db/prod", "uri-secret")]
    [InlineData("setup code is natural-secret", "natural-secret")]
    [InlineData("{\\\"password\\\":\\\"escaped-secret\\\"}", "escaped-secret")]
    public void Create_redacts_common_sensitive_formats_in_description(string rawDescription, string secret)
    {
        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            securityDescription: rawDescription);

        Assert.DoesNotContain(secret, auditEvent.SecurityDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret.Replace(" ", string.Empty), auditEvent.SecurityDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("redis://:redis-password@cache.internal:6379/0", "redis-password")]
    [InlineData("mongodb://admin:mongo-password@db.internal/audit", "mongo-password")]
    [InlineData("amqp://worker:rabbit-password@queue.internal/vhost", "rabbit-password")]
    [InlineData("custom+tls://client:encoded%2Dsecret@service.internal/path", "encoded%2Dsecret")]
    public void Create_redacts_credentials_in_any_absolute_uri(string uri, string secret)
    {
        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            securityDescription: $"Updated endpoint {uri}");

        Assert.DoesNotContain(secret, auditEvent.SecurityDescription, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", auditEvent.SecurityDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_removes_format_and_line_separator_characters()
    {
        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            securityDescription: "before\u200B\u2028after");

        Assert.DoesNotContain('\u200B', auditEvent.SecurityDescription ?? string.Empty);
        Assert.DoesNotContain('\u2028', auditEvent.SecurityDescription ?? string.Empty);
    }

    [Fact]
    public void Create_returns_immutable_metadata_snapshot()
    {
        var input = new Dictionary<string, string> { ["note"] = "before" };
        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            metadata: input);

        input["note"] = "after";

        Assert.Equal("before", auditEvent.Metadata["note"]);
        var dictionary = Assert.IsAssignableFrom<IDictionary<string, string>>(auditEvent.Metadata);
        Assert.Throws<NotSupportedException>(() => dictionary["password"] = "clear-text");
    }

    [Fact]
    public void ToString_does_not_include_sensitive_or_free_text_fields()
    {
        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            clientIp: "203.0.113.7",
            correlationId: "corr-secret",
            securityDescription: "password: hunter2",
            metadata: new Dictionary<string, string> { ["note"] = "private note" });

        var text = auditEvent.ToString();

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private note", text, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("corr-secret", text, StringComparison.Ordinal);
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
    public void Create_rejects_metadata_keys_that_collide_after_cleaning()
    {
        var metadata = new Dictionary<string, string>
        {
            ["reason"] = "first",
            [" reason "] = "second"
        };

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

    [Theory]
    [InlineData("Bearer client-secret")]
    [InlineData("203.0.113.7 token=client-secret")]
    [InlineData("not-an-ip")]
    public void Create_rejects_non_ip_client_values(string clientIp)
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditEvent.Create(
                Operator, WellKnownManagementAuditActions.ConfigurationChanged, Target, clientIp: clientIp));

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

    [Theory]
    [InlineData("token=correlation-secret")]
    [InlineData("Bearer correlation-secret")]
    [InlineData("correlation/id")]
    public void Create_rejects_correlation_ids_outside_the_allowlist(string correlationId)
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditEvent.Create(
                Operator,
                WellKnownManagementAuditActions.ConfigurationChanged,
                Target,
                correlationId: correlationId));

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
