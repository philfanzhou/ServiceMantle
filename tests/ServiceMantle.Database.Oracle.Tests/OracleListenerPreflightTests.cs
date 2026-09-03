using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

public sealed class OracleListenerPreflightTests
{
    [Fact]
    public async Task Successful_listener_probe_is_executed_once()
    {
        var calls = 0;
        await OracleListenerPreflight.VerifyAsync(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Failure_is_not_retried_and_drops_all_underlying_details()
    {
        var calls = 0;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OracleListenerPreflight.VerifyAsync(_ =>
            {
                calls++;
                throw new InvalidOperationException("User Id=private-user;Password=private-secret;Data Source=private-host");
            }, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        Assert.Equal("Oracle listener preflight failed: unexpected_probe_failure.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("private-", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1017, "credentials_rejected")]
    [InlineData(28000, "account_locked")]
    [InlineData(28001, "password_expired")]
    [InlineData(1045, "permission_denied")]
    [InlineData(12514, "listener_or_service_unavailable")]
    [InlineData(12541, "listener_or_service_unavailable")]
    [InlineData(12170, "connection_timeout")]
    [InlineData(9999, "unexpected_probe_failure")]
    [InlineData(null, "unexpected_probe_failure")]
    public void Known_failures_have_safe_diagnostic_categories(int? number, string expected) =>
        Assert.StartsWith(expected, OracleListenerPreflight.Classify(number), StringComparison.Ordinal);

    [Fact]
    public async Task Caller_cancellation_takes_precedence_and_drops_underlying_details()
    {
        using var cancellation = new CancellationTokenSource();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OracleListenerPreflight.VerifyAsync(_ =>
            {
                cancellation.Cancel();
                throw new Exception("private-secret");
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("private-secret", exception.ToString(), StringComparison.Ordinal);
    }
}
