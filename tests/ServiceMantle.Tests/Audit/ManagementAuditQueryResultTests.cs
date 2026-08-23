using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class ManagementAuditQueryResultTests
{
    [Fact]
    public void HasNextPage_is_true_when_more_records_remain()
    {
        var result = new ManagementAuditQueryResult(items: [], page: 1, pageSize: 10, totalCount: 25);

        Assert.True(result.HasNextPage);
    }

    [Fact]
    public void HasNextPage_is_false_on_last_page()
    {
        var result = new ManagementAuditQueryResult(items: [], page: 3, pageSize: 10, totalCount: 25);

        Assert.False(result.HasNextPage);
    }

    [Fact]
    public void HasNextPage_is_false_when_total_count_is_zero()
    {
        var result = new ManagementAuditQueryResult(items: [], page: 1, pageSize: 10, totalCount: 0);

        Assert.False(result.HasNextPage);
    }
}
