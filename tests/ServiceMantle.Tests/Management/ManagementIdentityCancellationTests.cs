using System.Security.Claims;
using ServiceMantle.Audit;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.Tests.Management;

/// <summary>
/// Drives the entry and completion checkpoints of
/// <see cref="ManagementIdentityProviderInvoker.InvokeAsync"/> with self-contained provider doubles.
/// </summary>
public sealed class ManagementIdentityCancellationTests
{
    private const string Canary = "injected-credential-canary";

    [Fact]
    public async Task Pre_cancelled_caller_never_calls_the_provider()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var provider = new TrackingProvider(_ => ValueTask.FromResult(ManagementIdentityResult.Unauthenticated()));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ManagementIdentityProviderInvoker.InvokeAsync(provider, cts.Token).AsTask());

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Null_provider_still_fails_parameter_validation_first()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ManagementIdentityProviderInvoker.InvokeAsync(null!, cts.Token).AsTask());
    }

    public static TheoryData<Func<CancellationTokenSource, TrackingProvider>> CancelledCompletions =>
        new()
        {
            // Authenticated result after cancellation.
            cts => new TrackingProvider(_ =>
            {
                cts.Cancel();
                return ValueTask.FromResult(Authenticated());
            }),
            // Unauthenticated result after cancellation.
            cts => new TrackingProvider(_ =>
            {
                cts.Cancel();
                return ValueTask.FromResult(ManagementIdentityResult.Unauthenticated());
            }),
            // Failed result with a legal provider code after cancellation.
            cts => new TrackingProvider(_ =>
            {
                cts.Cancel();
                return ValueTask.FromResult(ManagementIdentityResult.Failed("upstream.unavailable"));
            }),
            // Null result after cancellation.
            cts => new TrackingProvider(_ =>
            {
                cts.Cancel();
                return ValueTask.FromResult<ManagementIdentityResult>(null!);
            }),
            // Ordinary exception thrown synchronously after cancellation.
            cts => new TrackingProvider(_ =>
            {
                cts.Cancel();
                throw new InvalidOperationException(Canary);
            }),
            // Ordinary exception settling asynchronously after cancellation.
            cts => new TrackingProvider(async _ =>
            {
                await Task.Yield();
                cts.Cancel();
                throw new InvalidOperationException(Canary);
            }),
            // Internal cancellation with a foreign token after the caller cancelled.
            cts => new TrackingProvider(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(Canary, new CancellationTokenSource().Token);
            }),
            // Internal cancellation with a foreign token and a synthetic inner exception.
            cts => new TrackingProvider(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(
                    Canary,
                    new InvalidOperationException(Canary),
                    new CancellationTokenSource().Token);
            })
        };

    [Theory]
    [MemberData(nameof(CancelledCompletions))]
    public async Task Any_completion_observing_caller_cancellation_ends_in_a_safe_OCE(
        Func<CancellationTokenSource, TrackingProvider> providerFactory)
    {
        using var cts = new CancellationTokenSource();
        var provider = providerFactory(cts);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ManagementIdentityProviderInvoker.InvokeAsync(provider, cts.Token).AsTask());

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Canary, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Uncancelled_calls_keep_three_states_codes_and_the_original_exception_is_dropped()
    {
        var identity = Authenticated().Identity!;
        var authenticated = await ManagementIdentityProviderInvoker.InvokeAsync(
            new TrackingProvider(_ => ValueTask.FromResult(ManagementIdentityResult.Authenticated(identity))));
        var unauthenticated = await ManagementIdentityProviderInvoker.InvokeAsync(
            new TrackingProvider(_ => ValueTask.FromResult(ManagementIdentityResult.Unauthenticated())));
        var failed = await ManagementIdentityProviderInvoker.InvokeAsync(
            new TrackingProvider(_ => ValueTask.FromResult(ManagementIdentityResult.Failed("upstream.unavailable"))));
        var nullResult = await ManagementIdentityProviderInvoker.InvokeAsync(
            new TrackingProvider(_ => ValueTask.FromResult<ManagementIdentityResult>(null!)));
        var thrown = await ManagementIdentityProviderInvoker.InvokeAsync(
            new TrackingProvider(_ => throw new InvalidOperationException(Canary)));
        using var internalCts = new CancellationTokenSource();
        var internalCancel = await ManagementIdentityProviderInvoker.InvokeAsync(
            new TrackingProvider(_ => throw new OperationCanceledException(internalCts.Token)));

        Assert.Equal(ManagementIdentityStatus.Authenticated, authenticated.Status);
        Assert.Same(identity, authenticated.Identity);
        Assert.Equal(ManagementIdentityStatus.Unauthenticated, unauthenticated.Status);
        Assert.Equal(ManagementIdentityStatus.Failed, failed.Status);
        Assert.Equal("upstream.unavailable", failed.ErrorCode);
        Assert.Equal(WellKnownManagementIdentityErrorCodes.ProviderFailed, nullResult.ErrorCode);
        Assert.Equal(WellKnownManagementIdentityErrorCodes.ProviderFailed, thrown.ErrorCode);
        Assert.Equal(WellKnownManagementIdentityErrorCodes.ProviderFailed, internalCancel.ErrorCode);
    }

    [Fact]
    public async Task Caller_cancelled_provider_exception_does_not_reuse_the_original_exception_object()
    {
        using var cts = new CancellationTokenSource();
        var original = new OperationCanceledException(Canary, new CancellationTokenSource().Token);
        var provider = new TrackingProvider(_ =>
        {
            cts.Cancel();
            throw original;
        });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ManagementIdentityProviderInvoker.InvokeAsync(provider, cts.Token).AsTask());

        Assert.NotSame(original, exception);
        Assert.Null(exception.InnerException);
        Assert.Equal(cts.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Provider_is_called_once_with_the_caller_token_and_may_settle_asynchronously()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken observed = default;
        var provider = new TrackingProvider(async token =>
        {
            observed = token;
            await Task.Yield();
            return ManagementIdentityResult.Unauthenticated();
        });

        var result = await ManagementIdentityProviderInvoker.InvokeAsync(provider, cts.Token);

        Assert.Equal(ManagementIdentityStatus.Unauthenticated, result.Status);
        Assert.Equal(cts.Token, observed);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Two_independent_concurrent_calls_only_cancel_the_affected_one()
    {
        using var cancelled = new CancellationTokenSource();
        var cancelledProvider = new TrackingProvider(_ =>
        {
            cancelled.Cancel();
            return ValueTask.FromResult(Authenticated());
        });
        var healthyProvider = new TrackingProvider(_ =>
            ValueTask.FromResult(Authenticated()));

        var cancelledTask = Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ManagementIdentityProviderInvoker.InvokeAsync(cancelledProvider, cancelled.Token).AsTask());
        var healthyTask = ManagementIdentityProviderInvoker.InvokeAsync(
            healthyProvider, TestContext.Current.CancellationToken).AsTask();

        Assert.Equal(cancelled.Token, (await cancelledTask).CancellationToken);
        Assert.Equal(ManagementIdentityStatus.Authenticated, (await healthyTask).Status);
        Assert.Equal(1, healthyProvider.Calls);
    }

    private static ManagementIdentityResult Authenticated() => ManagementIdentityResult.Authenticated(
        ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Admin]));

    public sealed class TrackingProvider(
        Func<CancellationToken, ValueTask<ManagementIdentityResult>> callback)
        : IManagementIdentityProvider
    {
        public int Calls { get; private set; }

        public ValueTask<ManagementIdentityResult> GetIdentityAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return callback(cancellationToken);
        }
    }
}
