using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Tests.Health;

public sealed class ServiceReadinessContributorCombinerTests
{
    private static readonly ServiceHealthSnapshot ReadySnapshot = new(
        ServiceStartupPhase.Completed,
        ServiceMigrationReadinessState.Succeeded,
        ServiceDatabaseReadinessState.Reachable);

    [Fact]
    public async Task No_contributors_and_one_ready_contributor_are_ready()
    {
        var empty = new ServiceReadinessContributorCombiner([]);
        var contributor = new TrackingContributor(
            order: 4,
            (_, _) => ValueTask.FromResult(ServiceReadinessContributorResult.Ready()));
        var single = new ServiceReadinessContributorCombiner([contributor]);

        var emptyResult = await empty.EvaluateAsync(
            ReadySnapshot,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        var singleResult = await single.EvaluateAsync(
            ReadySnapshot,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.True(emptyResult.IsReady);
        Assert.True(singleResult.IsReady);
        Assert.Same(ReadySnapshot, Assert.Single(contributor.Snapshots));
    }

    [Fact]
    public async Task Contributors_run_in_stable_order_and_continue_after_rejection()
    {
        var calls = new List<int>();
        var contributors = new[]
        {
            Contributor(30, "business.third", calls),
            Contributor(10, "business.first", calls),
            Contributor(20, null, calls),
        };
        var combiner = new ServiceReadinessContributorCombiner(contributors);

        var result = await combiner.EvaluateAsync(
            ReadySnapshot,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal([10, 20, 30], calls);
        Assert.False(result.IsReady);
        Assert.Equal("business.first", result.ErrorCode);
    }

    [Fact]
    public async Task Exceptions_and_null_results_fail_closed_and_use_lowest_order()
    {
        const string secret =
            "Server=db.internal;Port=5432;Username=admin;Password=token-secret;SELECT 1";
        var calls = new List<int>();
        var exception = new TrackingContributor(5, (_, _) =>
        {
            calls.Add(5);
            throw new InvalidOperationException(secret);
        });
        var nullResult = new TrackingContributor(10, (_, _) =>
        {
            calls.Add(10);
            return ValueTask.FromResult<ServiceReadinessContributorResult>(null!);
        });
        var rejection = Contributor(20, "business.rejected", calls);
        var combiner = new ServiceReadinessContributorCombiner(
            [rejection, nullResult, exception]);

        var result = await combiner.EvaluateAsync(
            ReadySnapshot,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal([5, 10, 20], calls);
        Assert.False(result.IsReady);
        Assert.Equal(
            WellKnownServiceReadinessContributorErrorCodes.ContributorFailed,
            result.ErrorCode);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(TrackingContributor), result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_contributors_share_one_total_budget()
    {
        var calls = new List<int>();
        var first = new TrackingContributor(1, async (_, cancellationToken) =>
        {
            calls.Add(1);
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            return ServiceReadinessContributorResult.Ready();
        });
        var second = new TrackingContributor(2, async (_, cancellationToken) =>
        {
            calls.Add(2);
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            return ServiceReadinessContributorResult.Ready();
        });
        var combiner = new ServiceReadinessContributorCombiner([first, second]);
        var started = DateTimeOffset.UtcNow;

        var result = await combiner.EvaluateAsync(
            ReadySnapshot,
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], calls);
        Assert.Equal(
            WellKnownServiceReadinessContributorErrorCodes.ContributorTimeout,
            result.ErrorCode);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_the_original_token()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contributor = new TrackingContributor(1, async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ServiceReadinessContributorResult.Ready();
        });
        var combiner = new ServiceReadinessContributorCombiner([contributor]);
        using var cancellation = new CancellationTokenSource();
        var evaluation = combiner.EvaluateAsync(
            ReadySnapshot,
            TimeSpan.FromSeconds(5),
            cancellation.Token).AsTask();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => evaluation);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Internal_cancellation_is_a_failure_and_does_not_stop_later_contributors()
    {
        var calls = new List<int>();
        var internalCancellation = new TrackingContributor(1, (_, _) =>
        {
            calls.Add(1);
            return ValueTask.FromException<ServiceReadinessContributorResult>(
                new OperationCanceledException("internal cancellation secret"));
        });
        var ready = new TrackingContributor(2, (_, _) =>
        {
            calls.Add(2);
            return ValueTask.FromResult(ServiceReadinessContributorResult.Ready());
        });
        var combiner = new ServiceReadinessContributorCombiner([ready, internalCancellation]);

        var result = await combiner.EvaluateAsync(
            ReadySnapshot,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], calls);
        Assert.Equal(
            WellKnownServiceReadinessContributorErrorCodes.ContributorFailed,
            result.ErrorCode);
        Assert.DoesNotContain("internal cancellation secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_registrations_fail_with_safe_public_text()
    {
        var duplicate = Assert.Throws<InvalidOperationException>(() =>
            new ServiceReadinessContributorCombiner(
            [
                new TrackingContributor(1, Ready),
                new TrackingContributor(1, Ready),
            ]));
        var nullContributor = Assert.Throws<InvalidOperationException>(() =>
            new ServiceReadinessContributorCombiner([null!]));
        var throwingOrder = Assert.Throws<InvalidOperationException>(() =>
            new ServiceReadinessContributorCombiner([new SecretNamedThrowingOrderContributor()]));

        Assert.Equal(duplicate.Message, nullContributor.Message);
        Assert.Equal(duplicate.Message, throwingOrder.Message);
        Assert.Null(duplicate.InnerException);
        Assert.Null(nullContributor.InnerException);
        Assert.Null(throwingOrder.InnerException);
        Assert.DoesNotContain(
            nameof(SecretNamedThrowingOrderContributor),
            throwingOrder.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("connection-secret", throwingOrder.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("health invalid")]
    [InlineData("Password=secret")]
    public void Contributor_results_reject_unsafe_error_codes(string errorCode)
    {
        Assert.Throws<ArgumentException>(() =>
            ServiceReadinessContributorResult.NotReady(errorCode));
    }

    private static TrackingContributor Contributor(
        int order,
        string? rejection,
        ICollection<int> calls) => new(order, (_, _) =>
    {
        calls.Add(order);
        return ValueTask.FromResult(rejection is null
            ? ServiceReadinessContributorResult.Ready()
            : ServiceReadinessContributorResult.NotReady(rejection));
    });

    private static ValueTask<ServiceReadinessContributorResult> Ready(
        ServiceHealthSnapshot snapshot,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ServiceReadinessContributorResult.Ready());

    private sealed class TrackingContributor(
        int order,
        Func<ServiceHealthSnapshot, CancellationToken, ValueTask<ServiceReadinessContributorResult>> evaluate)
        : IServiceReadinessContributor
    {
        public int Order => order;

        public List<ServiceHealthSnapshot> Snapshots { get; } = [];

        public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            return evaluate(snapshot, cancellationToken);
        }
    }

    private sealed class SecretNamedThrowingOrderContributor : IServiceReadinessContributor
    {
        public int Order => throw new InvalidOperationException("connection-secret");

        public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
            ServiceHealthSnapshot snapshot,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
