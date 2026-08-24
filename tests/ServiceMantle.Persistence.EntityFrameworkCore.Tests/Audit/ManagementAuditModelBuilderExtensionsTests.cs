using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ServiceMantle.Audit;
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
        Assert.Equal(
            ManagementAuditEntityMapper.MaxMetadataJsonByteLength,
            entityType.FindProperty(nameof(ManagementAuditLogEntity.MetadataJson))!.GetMaxLength());
        var designTimeEntityType = context.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(ManagementAuditLogEntity));
        Assert.NotNull(designTimeEntityType);
        Assert.Contains(
            designTimeEntityType.GetCheckConstraints(),
            constraint => constraint.Name == "ck_service_audit_logs_id_not_empty");
        Assert.Contains(
            designTimeEntityType.GetCheckConstraints(),
            constraint => constraint.Name == "ck_service_audit_logs_metadata_json_length");
        Assert.Contains(
            designTimeEntityType.GetCheckConstraints(),
            constraint => constraint.Name == "ck_service_audit_logs_security_description_length");
        Assert.Contains(
            designTimeEntityType.GetCheckConstraints(),
            constraint => constraint.Name == "ck_service_audit_logs_target_id_length");
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

        Assert.Contains("ix_service_audit_logs_occurred_at_utc_id", indexNames);
        Assert.Contains("ix_service_audit_logs_action_occurred_at_utc_id", indexNames);
        Assert.Contains("ix_service_audit_logs_target_occurred_at_utc_id", indexNames);
        Assert.Contains("ix_service_audit_logs_target_type_occurred_at_utc_id", indexNames);
        Assert.Contains("ix_service_audit_logs_target_id_occurred_at_utc_id", indexNames);
        Assert.Contains("ix_service_audit_logs_operator_id_occurred_at_utc_id", indexNames);

        var targetTypeIndex = Assert.Single(
            entityType.GetIndexes(),
            index => index.GetDatabaseName() == "ix_service_audit_logs_target_type_occurred_at_utc_id");
        Assert.Equal(
            [
                nameof(ManagementAuditLogEntity.TargetType),
                nameof(ManagementAuditLogEntity.OccurredAtUtc),
                nameof(ManagementAuditLogEntity.Id)
            ],
            targetTypeIndex.Properties.Select(property => property.Name));
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

    [Fact]
    public void ModelBuilder_generates_sql_server_compatible_metadata_length_constraint()
    {
        using var context = new SqlServerAuditDbContext(
            new DbContextOptionsBuilder<SqlServerAuditDbContext>()
                .UseSqlServer("Server=localhost;Database=servicemantle_model_test;Integrated Security=true;TrustServerCertificate=true")
                .Options);

        var createScript = context.Database.GenerateCreateScript();
        var querySql = context.Set<ManagementAuditLogEntity>()
            .Where(item => ManagementAuditDatabaseFunctions.TextByteLength(item.MetadataJson) > 0)
            .ToQueryString();

        Assert.Contains("DATALENGTH(metadata_json)", createScript, StringComparison.Ordinal);
        Assert.Contains("DATALENGTH(security_description)", createScript, StringComparison.Ordinal);
        Assert.DoesNotContain(" OR length(metadata_json)", createScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATALENGTH", querySql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sqlite_constraints_use_encoded_bytes_and_reject_empty_ids_or_oversized_text()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var emptyIdException = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO service_audit_logs
                    (id, operator_source, action, target_type, target_id, outcome, occurred_at_utc)
                VALUES
                    ('00000000-0000-0000-0000-000000000000', 'system', 'configuration.changed',
                     'configuration', 'smtp', 1, '2026-01-01 00:00:00');
                """,
                TestContext.Current.CancellationToken));

        var astralMetadata = "\"" + string.Concat(Enumerable.Repeat(
            "\U0001F600",
            (ManagementAuditEntityMapper.MaxMetadataJsonByteLength / 4) + 1)) + "\"";
        context.Set<ManagementAuditLogEntity>().Add(new ManagementAuditLogEntity
        {
            Id = Guid.NewGuid(),
            OperatorSource = "system",
            Action = "configuration.changed",
            TargetType = "configuration",
            TargetId = "smtp",
            Outcome = ManagementAuditOutcome.Success,
            OccurredAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MetadataJson = astralMetadata
        });
        var byteLengthException = await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.SaveChangesAsync(TestContext.Current.CancellationToken));
        context.ChangeTracker.Clear();

        await using var oversizedDescriptionCommand = connection.CreateCommand();
        oversizedDescriptionCommand.CommandText =
            """
            INSERT INTO service_audit_logs
                (id, operator_source, action, target_type, target_id, outcome, occurred_at_utc,
                 security_description)
            VALUES
                ($id, 'system', 'configuration.changed', 'configuration', 'smtp', 1,
                 '2026-01-01 00:00:00', char(0) || printf('%.*c', $length, 'x'));
            """;
        oversizedDescriptionCommand.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        oversizedDescriptionCommand.Parameters.AddWithValue(
            "$length",
            ManagementAuditEntityMapper.MaxPersistedTextByteLength(
                ManagementAuditEvent.MaxDescriptionLength));
        var descriptionLengthException = await Assert.ThrowsAsync<SqliteException>(() =>
            oversizedDescriptionCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ck_service_audit_logs_id_not_empty", emptyIdException.Message, StringComparison.Ordinal);
        Assert.Contains("ck_service_audit_logs_metadata_json_length", byteLengthException.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "ck_service_audit_logs_security_description_length",
            descriptionLengthException.Message,
            StringComparison.Ordinal);
        Assert.True(astralMetadata.Length < ManagementAuditEntityMapper.MaxMetadataJsonByteLength);
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

    private sealed class RepeatedMappingDbContext : DbContext
    {
        public RepeatedMappingDbContext(DbContextOptions<RepeatedMappingDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.Sqlite);
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.Sqlite);
        }
    }

    private sealed class SqlServerAuditDbContext : DbContext
    {
        public SqlServerAuditDbContext(DbContextOptions<SqlServerAuditDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.SqlServer);
    }
}
