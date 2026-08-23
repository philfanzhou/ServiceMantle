using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.EntityFrameworkCore;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests.Audit;

public sealed class EfCoreManagementAuditQueryServiceTests
{
    private static readonly ManagementAuditTarget ServiceTarget =
        ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Service, "signacore");
    private static readonly ManagementAuditTarget ConfigTarget =
        ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp");

    [Fact]
    public async Task QueryAsync_filters_by_action()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.ConfigurationChanged, ConfigTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(action: WellKnownManagementAuditActions.ConfigurationChanged),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("configuration.changed", result.Items.Single().Action.Value);
    }

    [Fact]
    public async Task QueryAsync_filters_by_target_type_and_target_id()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.ConfigurationChanged, ConfigTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.ConfigurationChanged, ServiceTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(
                targetType: WellKnownManagementAuditTargetTypes.Configuration, targetId: "smtp"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("smtp", result.Items.Single().Target.Id);
    }

    [Fact]
    public async Task QueryAsync_filters_by_operator_id()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-2", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(operatorId: "admin-2"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("admin-2", result.Items.Single().Operator.OperatorId);
    }

    [Fact]
    public async Task QueryAsync_filters_by_time_range()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(5)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(10)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(fromUtc: Day(4), toUtc: Day(6)),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(Day(5), result.Items.Single().OccurredAtUtc);
    }

    [Fact]
    public async Task QueryAsync_orders_newest_first_by_default()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(3)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken);

        Assert.Equal([Day(3), Day(2), Day(1)], result.Items.Select(item => item.OccurredAtUtc));
    }

    [Fact]
    public async Task QueryAsync_orders_oldest_first_when_requested()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(3)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(
            ManagementAuditQuery.Create(sortOrder: ManagementAuditSortOrder.Oldest),
            TestContext.Current.CancellationToken);

        Assert.Equal([Day(1), Day(2), Day(3)], result.Items.Select(item => item.OccurredAtUtc));
    }

    [Fact]
    public async Task QueryAsync_paginates_stably_across_pages()
    {
        var records = Enumerable.Range(0, 5)
            .Select(index => Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(index)))
            .ToArray();
        await using var context = await SeedAsync(records);

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 1, pageSize: 2), TestContext.Current.CancellationToken);
        var secondPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 2, pageSize: 2, cursor: firstPage.ContinuationCursor),
            TestContext.Current.CancellationToken);
        var thirdPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 3, pageSize: 2, cursor: secondPage.ContinuationCursor),
            TestContext.Current.CancellationToken);

        Assert.Equal(5, firstPage.TotalCount);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Single(thirdPage.Items);
        Assert.True(firstPage.HasNextPage);
        Assert.True(secondPage.HasNextPage);
        Assert.False(thirdPage.HasNextPage);

        var seenIds = firstPage.Items.Concat(secondPage.Items).Concat(thirdPage.Items)
            .Select(item => item.Id)
            .ToList();
        Assert.Equal(5, seenIds.Distinct().Count());
    }

    [Fact]
    public async Task QueryAsync_returns_a_usable_continuation_after_page_ten_thousand()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(3)));
        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var precedingQuery = ManagementAuditQuery.Create(
            page: ManagementAuditQuery.MaxPage - 1,
            pageSize: 1,
            cursor: "preceding-cursor");
        var pageTenThousandCursor = ManagementAuditContinuationCursor.Encode(
            ManagementAuditContinuationCursor.Create(precedingQuery, Day(4), Guid.NewGuid()));

        var pageTenThousand = await service.QueryAsync(
            ManagementAuditQuery.Create(
                page: ManagementAuditQuery.MaxPage,
                pageSize: 1,
                cursor: pageTenThousandCursor),
            TestContext.Current.CancellationToken);
        var nextPage = await service.QueryAsync(
            ManagementAuditQuery.Create(
                page: ManagementAuditQuery.MaxPage + 1,
                pageSize: 1,
                cursor: pageTenThousand.ContinuationCursor),
            TestContext.Current.CancellationToken);

        Assert.True(pageTenThousand.HasNextPage);
        Assert.NotNull(pageTenThousand.ContinuationCursor);
        Assert.Single(nextPage.Items);
        Assert.Equal(ManagementAuditQuery.MaxPage + 1, nextPage.Page);
    }

    [Fact]
    public void ContinuationCursor_reports_a_stable_error_when_page_number_cannot_advance()
    {
        var query = ManagementAuditQuery.Create(page: int.MaxValue, cursor: "opaque-continuation");

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditContinuationCursor.Create(query, Day(1), Guid.NewGuid()));

        Assert.Equal("audit.query_page_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_uses_id_as_stable_tiebreaker_for_equal_timestamps()
    {
        var sameInstant = Day(1);
        var records = Enumerable.Range(0, 4)
            .Select(_ => Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, sameInstant))
            .ToArray();
        await using var context = await SeedAsync(records);

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 1, pageSize: 2), TestContext.Current.CancellationToken);
        var secondPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 2, pageSize: 2, cursor: firstPage.ContinuationCursor),
            TestContext.Current.CancellationToken);

        var seenIds = firstPage.Items.Concat(secondPage.Items).Select(item => item.Id).ToList();
        Assert.Equal(4, seenIds.Distinct().Count());
    }

    [Fact]
    public async Task QueryAsync_cursor_excludes_records_inserted_after_first_page()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(3)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(pageSize: 1), TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        await writer.RecordAsync(
            Record("admin-new", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(4)),
            TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var secondPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 2, pageSize: 1, cursor: firstPage.ContinuationCursor),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(secondPage.Items, item => item.Operator.OperatorId == "admin-new");
        Assert.DoesNotContain(firstPage.Items.Select(item => item.Id), id => secondPage.Items.Any(item => item.Id == id));
    }

    [Fact]
    public async Task QueryAsync_uses_documented_keyset_semantics_for_backfilled_records()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(3)));

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(pageSize: 1), TestContext.Current.CancellationToken);

        var backfilled = Day(2).AddHours(12);
        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        await writer.RecordAsync(
            Record("admin-backfill", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, backfilled),
            TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var secondPage = await service.QueryAsync(
            ManagementAuditQuery.Create(page: 2, pageSize: 1, cursor: firstPage.ContinuationCursor),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, secondPage.TotalCount);
        Assert.Equal("admin-backfill", Assert.Single(secondPage.Items).Operator.OperatorId);
    }

    [Fact]
    public async Task QueryAsync_rejects_cursor_reused_with_a_different_action()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)),
            Record("admin-1", WellKnownManagementAuditActions.ConfigurationChanged, ConfigTarget, Day(3)),
            Record("admin-1", WellKnownManagementAuditActions.ConfigurationChanged, ConfigTarget, Day(4)));
        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(
                action: WellKnownManagementAuditActions.AdminLoginSucceeded,
                pageSize: 1),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            service.QueryAsync(
                ManagementAuditQuery.Create(
                    action: WellKnownManagementAuditActions.ConfigurationChanged,
                    page: 2,
                    pageSize: 1,
                    cursor: firstPage.ContinuationCursor),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("audit.query_cursor_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_rejects_cursor_reused_with_a_different_time_range()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)));
        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(fromUtc: Day(1), pageSize: 1),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            service.QueryAsync(
                ManagementAuditQuery.Create(
                    fromUtc: Day(0),
                    page: 2,
                    pageSize: 1,
                    cursor: firstPage.ContinuationCursor),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("audit.query_cursor_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_rejects_cursor_reused_with_a_different_page_size_or_page_number()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(2)),
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(3)));
        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var firstPage = await service.QueryAsync(
            ManagementAuditQuery.Create(pageSize: 1), TestContext.Current.CancellationToken);

        var changedSize = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            service.QueryAsync(
                ManagementAuditQuery.Create(page: 2, pageSize: 2, cursor: firstPage.ContinuationCursor),
                TestContext.Current.CancellationToken).AsTask());
        var skippedPage = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            service.QueryAsync(
                ManagementAuditQuery.Create(page: 3, pageSize: 1, cursor: firstPage.ContinuationCursor),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("audit.query_cursor_invalid", changedSize.ErrorCode);
        Assert.Equal("audit.query_cursor_invalid", skippedPage.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_rejects_offset_pages_without_a_continuation_cursor()
    {
        await using var context = await SeedAsync(
            Record("admin-1", WellKnownManagementAuditActions.AdminLoginSucceeded, ServiceTarget, Day(1)));
        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            service.QueryAsync(ManagementAuditQuery.Create(page: 2), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("audit.query_cursor_required", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_never_returns_secrets_that_were_redacted_at_write_time()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new AuditTestDbContext(
            new DbContextOptionsBuilder<AuditTestDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        var auditEvent = ManagementAuditEvent.Create(
            ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, "admin-1"),
            WellKnownManagementAuditActions.ConfigurationChanged,
            ConfigTarget,
            securityDescription: "Updated Server=db;Password=super-secret; for smtp relay.");
        await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new EfCoreManagementAuditQueryService<AuditTestDbContext>(context);
        var result = await service.QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken);

        var description = result.Items.Single().SecurityDescription;
        Assert.DoesNotContain("super-secret", description);
        Assert.Contains("[REDACTED]", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_revalidates_and_redacts_legacy_free_text_rows()
    {
        await using var context = await SeedAsync();
        context.ServiceAuditLogs.Add(new ManagementAuditLogEntity
        {
            Id = Guid.NewGuid(),
            OperatorId = "admin-1",
            OperatorSource = WellKnownManagementAuditOperatorSources.InteractiveAdmin.Value,
            Action = WellKnownManagementAuditActions.ConfigurationChanged.Value,
            TargetType = WellKnownManagementAuditTargetTypes.Configuration.Value,
            TargetId = "smtp",
            Outcome = ManagementAuditOutcome.Success,
            OccurredAtUtc = Day(1).UtcDateTime,
            SecurityDescription = "password: clear-text",
            MetadataJson = "{\"note\":\"token: clear-text\"}"
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new EfCoreManagementAuditQueryService<AuditTestDbContext>(context)
            .QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.DoesNotContain("clear-text", item.SecurityDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("clear-text", item.Metadata["note"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_rejects_legacy_sensitive_metadata_keys_with_stable_error()
    {
        await using var context = await SeedAsync();
        context.ServiceAuditLogs.Add(new ManagementAuditLogEntity
        {
            Id = Guid.NewGuid(),
            OperatorId = "admin-1",
            OperatorSource = WellKnownManagementAuditOperatorSources.InteractiveAdmin.Value,
            Action = WellKnownManagementAuditActions.ConfigurationChanged.Value,
            TargetType = WellKnownManagementAuditTargetTypes.Configuration.Value,
            TargetId = "smtp",
            Outcome = ManagementAuditOutcome.Success,
            OccurredAtUtc = Day(1).UtcDateTime,
            MetadataJson = "{\"password\":\"clear-text\"}"
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            new EfCoreManagementAuditQueryService<AuditTestDbContext>(context)
                .QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
        Assert.DoesNotContain("clear-text", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_rejects_legacy_metadata_keys_that_collide_after_cleaning()
    {
        await using var context = await SeedAsync();
        context.ServiceAuditLogs.Add(new ManagementAuditLogEntity
        {
            Id = Guid.NewGuid(),
            OperatorId = "admin-1",
            OperatorSource = WellKnownManagementAuditOperatorSources.InteractiveAdmin.Value,
            Action = WellKnownManagementAuditActions.ConfigurationChanged.Value,
            TargetType = WellKnownManagementAuditTargetTypes.Configuration.Value,
            TargetId = "smtp",
            Outcome = ManagementAuditOutcome.Success,
            OccurredAtUtc = Day(1).UtcDateTime,
            MetadataJson = "{\"reason\":\"first\",\" reason \":\"second\"}"
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            new EfCoreManagementAuditQueryService<AuditTestDbContext>(context)
                .QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_rejects_oversized_metadata_json_with_stable_error()
    {
        await using var context = await SeedAsync();
        await context.Database.ExecuteSqlRawAsync(
            "PRAGMA ignore_check_constraints = ON",
            TestContext.Current.CancellationToken);
        context.ServiceAuditLogs.Add(new ManagementAuditLogEntity
        {
            Id = Guid.NewGuid(),
            OperatorId = "admin-1",
            OperatorSource = WellKnownManagementAuditOperatorSources.InteractiveAdmin.Value,
            Action = WellKnownManagementAuditActions.ConfigurationChanged.Value,
            TargetType = WellKnownManagementAuditTargetTypes.Configuration.Value,
            TargetId = "smtp",
            Outcome = ManagementAuditOutcome.Success,
            OccurredAtUtc = Day(1).UtcDateTime,
            MetadataJson = "\0" + new string('x', ManagementAuditEntityMapper.MaxMetadataJsonByteLength)
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "PRAGMA ignore_check_constraints = OFF",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            new EfCoreManagementAuditQueryService<AuditTestDbContext>(context)
                .QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_preflights_oversized_non_metadata_text_with_stable_error()
    {
        await using var context = await SeedAsync();
        await context.Database.ExecuteSqlRawAsync(
            "PRAGMA ignore_check_constraints = ON",
            TestContext.Current.CancellationToken);
        var connection = Assert.IsType<SqliteConnection>(context.Database.GetDbConnection());
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO service_audit_logs
                (id, operator_id, operator_source, action, target_type, target_id, outcome,
                 occurred_at_utc, security_description)
            VALUES
                ($id, 'admin-1', 'interactive_admin', 'configuration.changed', 'configuration',
                 'smtp', 1, '2026-01-02 00:00:00', char(0) || printf('%.*c', 1000000, 'x'));
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "PRAGMA ignore_check_constraints = OFF",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            new EfCoreManagementAuditQueryService<AuditTestDbContext>(context)
                .QueryAsync(ManagementAuditQuery.Create(pageSize: 1), TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_rejects_empty_id_at_a_page_boundary_instead_of_emitting_an_invalid_cursor()
    {
        await using var context = await SeedAsync(
            Record("admin-valid", WellKnownManagementAuditActions.ConfigurationChanged, ConfigTarget, Day(1)));
        await context.Database.ExecuteSqlRawAsync(
            "PRAGMA ignore_check_constraints = ON",
            TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO service_audit_logs
                (id, operator_id, operator_source, action, target_type, target_id, outcome, occurred_at_utc)
            VALUES
                ('00000000-0000-0000-0000-000000000000', 'admin-invalid', 'interactive_admin',
                 'configuration.changed', 'configuration', 'smtp', 1, '2026-01-03 00:00:00');
            """,
            TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "PRAGMA ignore_check_constraints = OFF",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            new EfCoreManagementAuditQueryService<AuditTestDbContext>(context)
                .QueryAsync(
                    ManagementAuditQuery.Create(pageSize: 1),
                    TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_rejects_malformed_metadata_json_with_stable_error()
    {
        await using var context = await SeedAsync();
        context.ServiceAuditLogs.Add(new ManagementAuditLogEntity
        {
            Id = Guid.NewGuid(),
            OperatorId = "admin-1",
            OperatorSource = WellKnownManagementAuditOperatorSources.InteractiveAdmin.Value,
            Action = WellKnownManagementAuditActions.ConfigurationChanged.Value,
            TargetType = WellKnownManagementAuditTargetTypes.Configuration.Value,
            TargetId = "smtp",
            Outcome = ManagementAuditOutcome.Success,
            OccurredAtUtc = Day(1).UtcDateTime,
            MetadataJson = "{not-json"
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            new EfCoreManagementAuditQueryService<AuditTestDbContext>(context)
                .QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task QueryAsync_rejects_empty_metadata_json_with_stable_error()
    {
        await using var context = await SeedAsync();
        context.ServiceAuditLogs.Add(new ManagementAuditLogEntity
        {
            Id = Guid.NewGuid(),
            OperatorId = "admin-1",
            OperatorSource = WellKnownManagementAuditOperatorSources.InteractiveAdmin.Value,
            Action = WellKnownManagementAuditActions.ConfigurationChanged.Value,
            TargetType = WellKnownManagementAuditTargetTypes.Configuration.Value,
            TargetId = "smtp",
            Outcome = ManagementAuditOutcome.Success,
            OccurredAtUtc = Day(1).UtcDateTime,
            MetadataJson = string.Empty
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ManagementAuditException>(() =>
            new EfCoreManagementAuditQueryService<AuditTestDbContext>(context)
                .QueryAsync(ManagementAuditQuery.Create(), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("audit.entity_invalid", exception.ErrorCode);
    }

    private static DateTimeOffset Day(int offset) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(offset);

    private static ManagementAuditEvent Record(
        string operatorId,
        ManagementAuditAction action,
        ManagementAuditTarget target,
        DateTimeOffset occurredAtUtc) =>
        ManagementAuditEvent.Create(
            ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, operatorId),
            action,
            target,
            occurredAtUtc: occurredAtUtc);

    private static async Task<AuditTestDbContext> SeedAsync(params ManagementAuditEvent[] events)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var context = new AuditTestDbContext(
            new DbContextOptionsBuilder<AuditTestDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var writer = new EfCoreManagementAuditWriter<AuditTestDbContext>(context);
        foreach (var auditEvent in events)
        {
            await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return context;
    }
}
