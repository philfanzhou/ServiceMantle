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

        Assert.Equal(
            0,
            await context.ServiceAuditLogs.CountAsync(TestContext.Current.CancellationToken));
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

        var persisted = await context.ServiceAuditLogs.AsNoTracking().SingleAsync(
            item => item.Id == staged.Id, TestContext.Current.CancellationToken);
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

        Assert.Equal(0, await context.ServiceAuditLogs.CountAsync(TestContext.Current.CancellationToken));
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

        Assert.Equal(1, await context.ServiceAuditLogs.CountAsync(TestContext.Current.CancellationToken));
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

        var persisted = await context.ServiceAuditLogs.AsNoTracking().SingleAsync(
            item => item.Id == staged.Id, TestContext.Current.CancellationToken);
        Assert.Equal("admin-1", persisted.OperatorId);
        Assert.Equal("Alex Admin", persisted.OperatorDisplayName);
        Assert.Equal("interactive_admin", persisted.OperatorSource);
        Assert.Equal("203.0.113.7", persisted.ClientIp);
        Assert.Equal("corr-abc-123", persisted.CorrelationId);
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

    private sealed class SaveChangesTrackingDbContext : DbContext, IServiceMantleAuditDbContext
    {
        public SaveChangesTrackingDbContext(DbContextOptions<SaveChangesTrackingDbContext> options)
            : base(options)
        {
        }

        public DbSet<ManagementAuditLogEntity> ServiceAuditLogs { get; set; } = null!;

        public int SaveChangesCallCount { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            return base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddServiceMantleManagementAudit();
        }
    }
}
