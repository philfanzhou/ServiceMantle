using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Persistence.EntityFrameworkCore;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests.Audit;

public sealed class ManagementAuditModelBuilderExtensionsTests
{
    [Fact]
    public async Task ModelBuilder_configures_expected_table_and_columns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var entityType = context.Model.FindEntityType(typeof(ManagementAuditLogEntity));
        Assert.NotNull(entityType);
        Assert.Equal("service_audit_logs", entityType!.GetTableName());
        Assert.Equal(nameof(ManagementAuditLogEntity.Id), entityType.FindPrimaryKey()!.Properties.Single().Name);

        AssertColumn(entityType, nameof(ManagementAuditLogEntity.Id), "id", isRequired: true);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.OperatorId), "operator_id", isRequired: false);
        AssertColumn(
            entityType, nameof(ManagementAuditLogEntity.OperatorDisplayName), "operator_display_name", isRequired: false);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.OperatorSource), "operator_source", isRequired: true);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.Action), "action", isRequired: true);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.TargetType), "target_type", isRequired: true);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.TargetId), "target_id", isRequired: true);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.Outcome), "outcome", isRequired: true);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.OccurredAtUtc), "occurred_at_utc", isRequired: true);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.ClientIp), "client_ip", isRequired: false);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.CorrelationId), "correlation_id", isRequired: false);
        AssertColumn(
            entityType, nameof(ManagementAuditLogEntity.SecurityDescription), "security_description", isRequired: false);
        AssertColumn(entityType, nameof(ManagementAuditLogEntity.MetadataJson), "metadata_json", isRequired: false);
    }

    [Fact]
    public async Task ModelBuilder_configures_expected_indexes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var entityType = context.Model.FindEntityType(typeof(ManagementAuditLogEntity));
        var indexNames = entityType!.GetIndexes().Select(index => index.GetDatabaseName()).ToHashSet();

        Assert.Contains("ix_service_audit_logs_occurred_at_utc", indexNames);
        Assert.Contains("ix_service_audit_logs_action", indexNames);
        Assert.Contains("ix_service_audit_logs_target", indexNames);
        Assert.Contains("ix_service_audit_logs_operator_id", indexNames);
    }

    [Fact]
    public async Task ModelBuilder_can_be_invoked_multiple_times_without_duplicate_mapping()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new RepeatedMappingDbContext(
            new DbContextOptionsBuilder<RepeatedMappingDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var entityType = context.Model.FindEntityType(typeof(ManagementAuditLogEntity));
        Assert.NotNull(entityType);
        Assert.Equal("service_audit_logs", entityType!.GetTableName());
    }

    private static void AssertColumn(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
        string propertyName,
        string expectedColumnName,
        bool isRequired)
    {
        var property = entityType.FindProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expectedColumnName, property!.GetColumnName());
        Assert.Equal(!isRequired, property.IsNullable);
    }

    private static AuditTestDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AuditTestDbContext>().UseSqlite(connection).Options);

    private sealed class RepeatedMappingDbContext : DbContext, IServiceMantleAuditDbContext
    {
        public RepeatedMappingDbContext(DbContextOptions<RepeatedMappingDbContext> options)
            : base(options)
        {
        }

        public DbSet<ManagementAuditLogEntity> ServiceAuditLogs { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddServiceMantleManagementAudit();
            modelBuilder.AddServiceMantleManagementAudit();
        }
    }
}
