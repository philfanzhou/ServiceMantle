using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.EntityFrameworkCore;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests.Audit;

public sealed class EfCoreManagementAuditWriterTests
{
    private static readonly ManagementAuditOperator Operator = ManagementAuditOperator.Create(
        WellKnownManagementAuditOperatorSources.InteractiveAdmin,
        operatorId: "admin-1",
        displayName: "Alex Admin");

    private static readonly ManagementAuditTarget Target =
        ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Service, "signacore");

    [Fact]
    public async Task RecordAsync_stages_entity_without_saving_changes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.AdminLoginSucceeded, Target);

        await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountAuditRowsAsync(context));
        var entry = context.ChangeTracker.Entries<ManagementAuditLogEntity>().Single();
        Assert.Equal(EntityState.Added, entry.State);
    }

    [Fact]
    public async Task RecordAsync_persists_only_after_caller_calls_save_changes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.AdminLoginSucceeded, Target);

        var staged = await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var persisted = await ReadAuditRowAsync(context, staged.Id);
        Assert.Equal("admin_login.succeeded", persisted.Action);
    }

    [Fact]
    public async Task RecordAsync_participates_in_callers_explicit_transaction_and_rolls_back_with_it()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.AdminLoginSucceeded, Target);

        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
            context.Widgets.Add(new BusinessWidgetEntity { Id = Guid.NewGuid(), Name = "widget-1" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await CountAuditRowsAsync(context));
        Assert.Equal(0, await context.Widgets.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordAsync_commits_alongside_business_write_in_same_transaction()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.AdminLoginSucceeded, Target);

        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
            context.Widgets.Add(new BusinessWidgetEntity { Id = Guid.NewGuid(), Name = "widget-1" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await CountAuditRowsAsync(context));
        Assert.Equal(1, await context.Widgets.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordAsync_never_calls_save_changes_or_commits_itself()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new SaveChangesTrackingDbContext(
            new DbContextOptionsBuilder<SaveChangesTrackingDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<SaveChangesTrackingDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.AdminLoginSucceeded, Target);

        await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);

        Assert.Equal(0, context.SaveChangesCallCount);
    }

    [Fact]
    public async Task RecordAsync_preserves_operator_and_correlation_information()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.AdminLoginSucceeded,
            Target,
            clientIp: "203.0.113.7",
            correlationId: "corr-abc-123");

        var staged = await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var persisted = await ReadAuditRowAsync(context, staged.Id);
        Assert.Equal("admin-1", persisted.OperatorId);
        Assert.Equal("Alex Admin", persisted.OperatorDisplayName);
        Assert.Equal("interactive_admin", persisted.OperatorSource);
        Assert.Equal("203.0.113.7", persisted.ClientIp);
        Assert.Equal("corr-abc-123", persisted.CorrelationId);
    }

    [Fact]
    public async Task RecordAsync_persists_only_sanitized_supported_content_at_database_boundary()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            securityDescription: "password: description-secret",
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = "token: metadata-secret"
            });

        var staged = await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var persisted = await ReadAuditRowAsync(context, staged.Id);
        Assert.DoesNotContain("description-secret", persisted.SecurityDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-secret", persisted.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", persisted.SecurityDescription, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", persisted.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordAsync_stages_maximum_unicode_metadata_without_partial_failure()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var metadata = Enumerable.Range(0, ManagementAuditEvent.MaxMetadataEntries)
            .ToDictionary(
                index => $"field_{index}",
                _ => new string('\u6570', ManagementAuditEvent.MaxMetadataValueLength),
                StringComparer.Ordinal);
        var auditEvent = ManagementAuditEvent.Create(
            Operator,
            WellKnownManagementAuditActions.ConfigurationChanged,
            Target,
            metadata: metadata);

        var record = await new EfCoreManagementAuditWriter<AuditTestDbContext>(context)
            .RecordAsync(auditEvent, TestContext.Current.CancellationToken);

        Assert.Equal(ManagementAuditEvent.MaxMetadataEntries, record.Metadata.Count);
        Assert.Single(context.ChangeTracker.Entries<ManagementAuditLogEntity>());
        Assert.Equal(EntityState.Added, context.ChangeTracker.Entries<ManagementAuditLogEntity>().Single().State);
    }

    [Fact]
    public async Task RecordAsync_respects_cancellation_token()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            Operator, WellKnownManagementAuditActions.AdminLoginSucceeded, Target);

        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            writer.RecordAsync(auditEvent, cancellation.Token).AsTask());
    }

    private static AuditTestDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AuditTestDbContext>().UseSqlite(connection).Options);

    private static async Task<long> CountAuditRowsAsync(AuditTestDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM service_audit_logs;";
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<PersistedAuditRow> ReadAuditRowAsync(AuditTestDbContext context, Guid id)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT action, operator_id, operator_display_name, operator_source, client_ip,
                   correlation_id, security_description, metadata_json
            FROM service_audit_logs
            WHERE id = $id;
            """;
        command.Parameters.Add(new SqliteParameter("$id", id.ToString("D")));
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new PersistedAuditRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private sealed record PersistedAuditRow(
        string Action,
        string? OperatorId,
        string? OperatorDisplayName,
        string OperatorSource,
        string? ClientIp,
        string? CorrelationId,
        string? SecurityDescription,
        string? MetadataJson);

    private sealed class SaveChangesTrackingDbContext : DbContext
    {
        public SaveChangesTrackingDbContext(DbContextOptions<SaveChangesTrackingDbContext> options)
            : base(options)
        {
        }

        public int SaveChangesCallCount { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            return base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.Sqlite);
        }
    }
}
