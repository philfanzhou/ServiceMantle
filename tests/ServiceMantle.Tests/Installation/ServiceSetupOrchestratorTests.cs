using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Tests.Installation;

public sealed class ServiceSetupOrchestratorTests
{
    [Fact]
    public async Task Explicit_order_controls_both_phases_and_all_validation_precedes_registration()
    {
        var calls = new List<string>();
        var scope = new FakeStagingScope();
        var contributors = new IServiceSetupContributor[]
        {
            new FakeContributor(30, calls, "third"),
            new FakeContributor(-10, calls, "first"),
            new FakeContributor(5, calls, "second"),
        };
        var orchestrator = new ServiceSetupOrchestrator(contributors, scope);

        var result = await orchestrator.OrchestrateAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Equal(
            ["validate:first", "validate:second", "validate:third", "register:first", "register:second", "register:third"],
            calls);
        Assert.Equal(0, scope.DiscardCount);
    }

    [Fact]
    public void Duplicate_order_fails_at_construction_without_exposing_contributor_values()
    {
        const string secret = "setup-code-and-connection-string";
        var contributors = new IServiceSetupContributor[]
        {
            new FakeContributor(1, [], secret),
            new FakeContributor(1, [], secret),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceSetupOrchestrator(contributors, new FakeStagingScope()));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_rejection_prevents_every_registration()
    {
        var calls = new List<string>();
        var contributors = new IServiceSetupContributor[]
        {
            new FakeContributor(1, calls, "one"),
            new FakeContributor(2, calls, "two")
            {
                ValidationResult = ServiceSetupContributorResult.Rejected("product.setup_rejected"),
            },
            new FakeContributor(3, calls, "three"),
        };

        var result = await new ServiceSetupOrchestrator(contributors, new FakeStagingScope())
            .OrchestrateAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("product.setup_rejected", result.ErrorCode);
        Assert.Equal(["validate:one", "validate:two"], calls);
    }

    [Fact]
    public async Task Validation_side_effect_is_discarded_and_has_precedence_over_rejection()
    {
        var scope = new FakeStagingScope();
        var contributor = new FakeContributor(1, [], "secret")
        {
            ValidationResult = ServiceSetupContributorResult.Rejected("product.rejected"),
            OnValidate = () => scope.HasPendingChanges = true,
        };

        var result = await new ServiceSetupOrchestrator([contributor], scope)
            .OrchestrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownServiceSetupErrorCodes.ValidationSideEffect, result.ErrorCode);
        Assert.Equal(1, scope.DiscardCount);
        Assert.False(scope.HasPendingChanges);
        Assert.Equal(0, contributor.RegistrationCount);
    }

    [Fact]
    public async Task Dirty_scope_is_rejected_without_calling_or_cleaning_contributors()
    {
        var scope = new FakeStagingScope { HasPendingChanges = true };
        var contributor = new FakeContributor(1, [], "secret");

        var result = await new ServiceSetupOrchestrator([contributor], scope)
            .OrchestrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownSetupCodeErrorCodes.DirtyContext, result.ErrorCode);
        Assert.Equal(0, contributor.ValidationCount);
        Assert.Equal(0, contributor.RegistrationCount);
        Assert.Equal(0, scope.DiscardCount);
        Assert.True(scope.HasPendingChanges);
    }

    [Theory]
    [InlineData("rejection", "product.registration_rejected")]
    [InlineData("exception", WellKnownServiceSetupErrorCodes.ContributorFailed)]
    [InlineData("internal-cancellation", WellKnownServiceSetupErrorCodes.ContributorFailed)]
    [InlineData("null", WellKnownServiceSetupErrorCodes.ContributorFailed)]
    public async Task Registration_failures_are_safely_classified_and_discard_once(
        string failure,
        string expectedCode)
    {
        const string secret = "Server=db;Password=setup-code;";
        var scope = new FakeStagingScope();
        var contributor = new FakeContributor(1, [], secret)
        {
            OnRegister = () => scope.HasPendingChanges = true,
        };
        contributor.Registration = failure switch
        {
            "rejection" => _ => ValueTask.FromResult<ServiceSetupContributorResult?>(
                ServiceSetupContributorResult.Rejected(expectedCode)),
            "exception" => _ => ValueTask.FromException<ServiceSetupContributorResult?>(
                new InvalidOperationException(secret)),
            "internal-cancellation" => _ => ValueTask.FromException<ServiceSetupContributorResult?>(
                new OperationCanceledException(secret, new Exception(secret), new CancellationToken(true))),
            _ => _ => ValueTask.FromResult<ServiceSetupContributorResult?>(null),
        };

        var result = await new ServiceSetupOrchestrator([contributor], scope)
            .OrchestrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal(1, scope.DiscardCount);
        Assert.False(scope.HasPendingChanges);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleanup_failure_replaces_the_primary_safe_classification()
    {
        var scope = new FakeStagingScope { DiscardException = new InvalidOperationException("secret") };
        var contributor = new FakeContributor(1, [], "secret")
        {
            RegistrationResult = ServiceSetupContributorResult.Rejected("product.rejected"),
        };

        var result = await new ServiceSetupOrchestrator([contributor], scope)
            .OrchestrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownServiceSetupErrorCodes.CleanupFailed, result.ErrorCode);
        Assert.Equal(1, scope.DiscardCount);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_discards_with_none_and_propagates_the_original_token()
    {
        using var cancellation = new CancellationTokenSource();
        var scope = new FakeStagingScope();
        var contributor = new FakeContributor(1, [], "secret")
        {
            Registration = token =>
            {
                scope.HasPendingChanges = true;
                cancellation.Cancel();
                return ValueTask.FromCanceled<ServiceSetupContributorResult?>(token);
            },
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new ServiceSetupOrchestrator([contributor], scope).OrchestrateAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, scope.DiscardCount);
        Assert.Equal(CancellationToken.None, scope.LastDiscardToken);
        Assert.False(scope.HasPendingChanges);
    }

    [Fact]
    public async Task Cleanup_failure_does_not_replace_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var scope = new FakeStagingScope { DiscardException = new InvalidOperationException("secret") };
        var contributor = new FakeContributor(1, [], "secret")
        {
            Registration = token =>
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<ServiceSetupContributorResult?>(token);
            },
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new ServiceSetupOrchestrator([contributor], scope).OrchestrateAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, scope.DiscardCount);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("line\nbreak")]
    [InlineData("秘密")]
    public void Contributor_rejection_requires_the_existing_safe_error_code_shape(string errorCode)
    {
        Assert.Throws<ArgumentException>(() => ServiceSetupContributorResult.Rejected(errorCode));
    }

    [Fact]
    public void Staging_scope_exposes_no_save_or_transaction_capability()
    {
        var members = typeof(IServiceSetupStagingScope).GetMembers()
            .Where(member => member.DeclaringType == typeof(IServiceSetupStagingScope))
            .Select(member => member.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "get_HasPendingChanges",
                nameof(IServiceSetupStagingScope.HasPendingChanges),
                nameof(IServiceSetupStagingScope.DiscardPendingChangesAsync),
            },
            members);
    }

    private sealed class FakeContributor(int order, List<string> calls, string value)
        : IServiceSetupContributor
    {
        public int Order { get; } = order;

        public int ValidationCount { get; private set; }

        public int RegistrationCount { get; private set; }

        public ServiceSetupContributorResult ValidationResult { get; set; } =
            ServiceSetupContributorResult.Success();

        public ServiceSetupContributorResult RegistrationResult { get; set; } =
            ServiceSetupContributorResult.Success();

        public Action? OnValidate { get; set; }

        public Action? OnRegister { get; set; }

        public Func<CancellationToken, ValueTask<ServiceSetupContributorResult?>>? Registration { get; set; }

        public ValueTask<ServiceSetupContributorResult> ValidateAsync(CancellationToken cancellationToken = default)
        {
            ValidationCount++;
            calls.Add($"validate:{value}");
            OnValidate?.Invoke();
            return ValueTask.FromResult(ValidationResult);
        }

        public async ValueTask<ServiceSetupContributorResult> RegisterAsync(
            CancellationToken cancellationToken = default)
        {
            RegistrationCount++;
            calls.Add($"register:{value}");
            OnRegister?.Invoke();
            return Registration is null
                ? RegistrationResult
                : (await Registration(cancellationToken))!;
        }

        public override string ToString() => value;
    }

    private sealed class FakeStagingScope : IServiceSetupStagingScope
    {
        public bool HasPendingChanges { get; set; }

        public int DiscardCount { get; private set; }

        public CancellationToken LastDiscardToken { get; private set; }

        public Exception? DiscardException { get; set; }

        public ValueTask DiscardPendingChangesAsync(CancellationToken cancellationToken = default)
        {
            DiscardCount++;
            LastDiscardToken = cancellationToken;
            if (DiscardException is not null)
            {
                return ValueTask.FromException(DiscardException);
            }

            HasPendingChanges = false;
            return ValueTask.CompletedTask;
        }
    }
}
