using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class ManagementAuditQueryTests
{
    [Fact]
    public void Create_applies_default_page_and_page_size()
    {
        var query = ManagementAuditQuery.Create();

        Assert.Equal(1, query.Page);
        Assert.Equal(ManagementAuditQuery.DefaultPageSize, query.PageSize);
        Assert.Equal(ManagementAuditSortOrder.Newest, query.SortOrder);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_non_positive_page(int page)
    {
        var exception = Assert.Throws<ManagementAuditException>(() => ManagementAuditQuery.Create(page: page));

        Assert.Equal("audit.query_page_invalid", exception.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_non_positive_page_size(int pageSize)
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditQuery.Create(pageSize: pageSize));

        Assert.Equal("audit.query_page_size_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_page_size_exceeding_maximum()
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditQuery.Create(pageSize: ManagementAuditQuery.MaxPageSize + 1));

        Assert.Equal("audit.query_page_size_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_page_exceeding_maximum()
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditQuery.Create(page: ManagementAuditQuery.MaxPage + 1));

        Assert.Equal("audit.query_page_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_accepts_page_exceeding_first_request_maximum_when_cursor_is_present()
    {
        var query = ManagementAuditQuery.Create(
            page: ManagementAuditQuery.MaxPage + 1,
            cursor: "opaque-continuation");

        Assert.Equal(ManagementAuditQuery.MaxPage + 1, query.Page);
        Assert.Equal("opaque-continuation", query.Cursor);
    }

    [Fact]
    public void Create_rejects_cursor_exceeding_maximum_length()
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditQuery.Create(cursor: new string('a', ManagementAuditQuery.MaxCursorLength + 1)));

        Assert.Equal("audit.query_cursor_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_accepts_maximum_page_size()
    {
        var query = ManagementAuditQuery.Create(pageSize: ManagementAuditQuery.MaxPageSize);

        Assert.Equal(ManagementAuditQuery.MaxPageSize, query.PageSize);
    }

    [Fact]
    public void Create_rejects_undefined_sort_order()
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditQuery.Create(sortOrder: (ManagementAuditSortOrder)99));

        Assert.Equal("audit.query_sort_order_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_from_after_to()
    {
        var from = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditQuery.Create(fromUtc: from, toUtc: to));

        Assert.Equal("audit.query_time_range_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_time_range_exceeding_maximum_span()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(ManagementAuditQuery.MaxQueryRangeDays + 1);

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditQuery.Create(fromUtc: from, toUtc: to));

        Assert.Equal("audit.query_time_range_too_wide", exception.ErrorCode);
    }

    [Fact]
    public void Create_accepts_time_range_at_maximum_span()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(ManagementAuditQuery.MaxQueryRangeDays);

        var query = ManagementAuditQuery.Create(fromUtc: from, toUtc: to);

        Assert.Equal(from, query.FromUtc);
        Assert.Equal(to, query.ToUtc);
    }

    [Fact]
    public void Create_accepts_only_from_without_maximum_span_check()
    {
        var from = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var query = ManagementAuditQuery.Create(fromUtc: from);

        Assert.Equal(from, query.FromUtc);
        Assert.Null(query.ToUtc);
    }

    [Fact]
    public void Create_rejects_target_id_filter_exceeding_max_length()
    {
        var tooLong = new string('a', ManagementAuditTarget.MaxTargetIdLength + 1);

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditQuery.Create(targetId: tooLong));

        Assert.Equal("audit.query_filter_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_operator_id_filter_exceeding_max_length()
    {
        var tooLong = new string('a', ManagementAuditOperator.MaxOperatorIdLength + 1);

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditQuery.Create(operatorId: tooLong));

        Assert.Equal("audit.query_filter_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_treats_whitespace_only_filters_as_absent()
    {
        var query = ManagementAuditQuery.Create(targetId: "   ", operatorId: "  ");

        Assert.Null(query.TargetId);
        Assert.Null(query.OperatorId);
    }

    [Fact]
    public void Create_retains_action_and_target_type_filters()
    {
        var query = ManagementAuditQuery.Create(
            action: WellKnownManagementAuditActions.ConfigurationChanged,
            targetType: WellKnownManagementAuditTargetTypes.Configuration);

        Assert.Equal(WellKnownManagementAuditActions.ConfigurationChanged, query.Action);
        Assert.Equal(WellKnownManagementAuditTargetTypes.Configuration, query.TargetType);
    }
}
