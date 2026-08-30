using ServiceMantle.Testing;
using Xunit;

namespace ServiceMantle.Tests.Testing;

public sealed class RealDatabaseTestHarnessTests
{
    [Fact]
    public void Classification_exposes_shared_category_and_provider_traits()
    {
        var attribute = new RealDatabaseTestAttribute(RealDatabaseProvider.SqlServer);

        var traits = attribute.GetTraits();

        Assert.Contains(
            new KeyValuePair<string, string>(
                RealDatabaseTestAttribute.CategoryTraitName,
                RealDatabaseTestAttribute.CategoryTraitValue),
            traits);
        Assert.Contains(
            new KeyValuePair<string, string>(
                RealDatabaseTestAttribute.ProviderTraitName,
                nameof(RealDatabaseProvider.SqlServer)),
            traits);
    }

    [Theory]
    [InlineData(false, true, RealDatabaseAvailability.Available)]
    [InlineData(true, true, RealDatabaseAvailability.Available)]
    [InlineData(false, false, RealDatabaseAvailability.OptionalUnavailable)]
    [InlineData(true, false, RealDatabaseAvailability.RequiredUnavailable)]
    public void Availability_policy_distinguishes_optional_skip_from_required_failure(
        bool isRequired,
        bool isAvailable,
        RealDatabaseAvailability expected)
    {
        Assert.Equal(expected, RealDatabaseTestEnvironment.Evaluate(isRequired, isAvailable));
    }

    [Theory]
    [InlineData(RealDatabaseProvider.PostgreSql, "RUN_SERVICEMANTLE_POSTGRES_TESTS")]
    [InlineData(RealDatabaseProvider.MySql, "RUN_SERVICEMANTLE_MYSQL_TESTS")]
    [InlineData(RealDatabaseProvider.MariaDb, "RUN_SERVICEMANTLE_MARIADB_TESTS")]
    [InlineData(RealDatabaseProvider.Oracle, "RUN_SERVICEMANTLE_ORACLE_TESTS")]
    [InlineData(RealDatabaseProvider.SqlServer, "RUN_SERVICEMANTLE_SQLSERVER_TESTS")]
    public void Providers_have_one_fixed_required_environment_variable(
        RealDatabaseProvider provider,
        string expected)
    {
        Assert.Equal(expected, RealDatabaseTestEnvironment.GetRequirementVariable(provider));
    }

    [Fact]
    public void Required_environment_unavailable_throws_safe_failure()
    {
        var variable = RealDatabaseTestEnvironment.GetRequirementVariable(RealDatabaseProvider.Oracle);
        var original = Environment.GetEnvironmentVariable(variable);
        const string credential = "oracle-test-password";

        try
        {
            Environment.SetEnvironmentVariable(variable, "true");

            var exception = Assert.Throws<RealDatabaseTestEnvironmentException>(() =>
                RealDatabaseTestEnvironment.RequireAvailable(RealDatabaseProvider.Oracle, isAvailable: false));

            Assert.Equal(RealDatabaseProvider.Oracle, exception.Provider);
            Assert.DoesNotContain(credential, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public void Credential_source_injects_values_without_rendering_them()
    {
        const string username = "database-admin";
        const string password = "database-password";
        IRealDatabaseCredentialSource source = new FakeCredentialSource(
            new RealDatabaseCredentials(username, password));

        var credentials = source.GetCredentials(RealDatabaseProvider.MySql);
        var text = credentials.ToString();

        Assert.Equal(username, credentials.Username);
        Assert.Equal(password, credentials.Password);
        Assert.DoesNotContain(username, text, StringComparison.Ordinal);
        Assert.DoesNotContain(password, text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Barrier_releases_exactly_two_actors_at_the_same_rendezvous()
    {
        var barrier = new TwoActorBarrier(TimeSpan.FromSeconds(1));

        var first = barrier.FirstActorAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(first.IsCompleted);
        var second = barrier.SecondActorAsync(TestContext.Current.CancellationToken).AsTask();

        await Task.WhenAll(first, second);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Barrier_timeout_reports_fixed_safe_diagnostics()
    {
        const string credential = "barrier-diagnostic-password";
        var timeout = TimeSpan.FromMilliseconds(50);
        var barrier = new TwoActorBarrier(timeout);

        var exception = await Assert.ThrowsAsync<TwoActorBarrierTimeoutException>(() =>
            barrier.FirstActorAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(timeout, exception.Timeout);
        Assert.Equal(1, exception.ArrivedActorCount);
        Assert.Contains("1 of 2 actors", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Barrier_timeout_is_reported_to_the_late_second_actor()
    {
        var barrier = new TwoActorBarrier(TimeSpan.FromMilliseconds(50));

        var firstFailure = await Assert.ThrowsAsync<TwoActorBarrierTimeoutException>(() =>
            barrier.FirstActorAsync(TestContext.Current.CancellationToken).AsTask());
        var secondFailure = await Assert.ThrowsAsync<TwoActorBarrierTimeoutException>(() =>
            barrier.SecondActorAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Same(firstFailure, secondFailure);
    }

    [Fact]
    public async Task Barrier_propagates_caller_cancellation_instead_of_timeout()
    {
        var barrier = new TwoActorBarrier(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        var first = barrier.FirstActorAsync(cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task Barrier_rejects_a_third_arrival_from_an_existing_actor()
    {
        var barrier = new TwoActorBarrier(TimeSpan.FromSeconds(1));
        var first = barrier.FirstActorAsync(TestContext.Current.CancellationToken).AsTask();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            barrier.FirstActorAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
        await barrier.SecondActorAsync(TestContext.Current.CancellationToken);
        await first;
    }

    private sealed class FakeCredentialSource(RealDatabaseCredentials credentials)
        : IRealDatabaseCredentialSource
    {
        public RealDatabaseCredentials GetCredentials(RealDatabaseProvider provider)
        {
            _ = provider;
            return credentials;
        }
    }
}
