using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Consul.Tests;

using Harness = ConsulLifecycleHarness;

public sealed class ConsulRegistrationLifecycleTests
{
    [Fact]
    public async Task A_disabled_configuration_creates_no_client_sampler_timer_or_remote_call()
    {
        await using var harness = await Harness.CreateAsync(enabled: false);

        await harness.StartAsync();
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        await harness.StopAsync();

        Assert.Equal(ConsulLifecycleState.Disabled, harness.Lifecycle.State);
        Assert.Equal(ConsulRemotePresence.Absent, harness.Lifecycle.Presence);
        Assert.Equal(0, harness.Fixture.ClientFactory.Calls);
        Assert.Equal(0, harness.Decisions.Calls);
        Assert.Equal(0, harness.Decisions.Resolutions);
        Assert.Empty(harness.Client.Operations);
        Assert.False(harness.Client.Disposed);
        Assert.Empty(harness.Observer.Diagnostics);
    }

    [Fact]
    public async Task A_ready_service_registers_exactly_once_and_a_repeated_ready_adds_nothing()
    {
        await using var harness = await Harness.CreateAsync();

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        // Several more readiness samples, all Ready.
        for (var index = 0; index < 5; index++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(1));
        }

        await Harness.WaitAsync(() => harness.Decisions.Calls >= 4, "the sampler stopped polling");

        Assert.Equal(ConsulRemotePresence.Present, harness.Lifecycle.Presence);
        Assert.Equal(1, harness.Client.Registers);
        Assert.Equal(0, harness.Client.Deregisters);
        Assert.Equal(1, harness.Fixture.ClientFactory.Calls);
        // Each sample resolves the scoped decision source in its own fresh scope.
        Assert.Equal(harness.Decisions.Calls, harness.Decisions.Resolutions);
        Assert.Empty(harness.Observer.Diagnostics);
    }

    [Theory]
    [InlineData(ServiceStartupPhase.BootstrapConfiguration, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.PendingSetup, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Running, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Failed, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.NotStarted, ServiceDatabaseReadinessState.Reachable)]
    [InlineData(ServiceStartupPhase.Completed, ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Unreachable)]
    public async Task No_base_matrix_rejection_ever_registers(
        ServiceStartupPhase phase,
        ServiceMigrationReadinessState migration,
        ServiceDatabaseReadinessState database)
    {
        await using var harness = await Harness.CreateAsync();
        harness.Decisions.Current = Harness.NotReady(phase, migration, database);

        await harness.StartAsync();
        await Harness.WaitAsync(() => harness.Decisions.Calls >= 1, "the sampler never ran");
        harness.Time.Advance(TimeSpan.FromSeconds(3));
        await harness.StopAsync();

        Assert.Empty(harness.Client.Operations);
        Assert.Equal(ConsulRemotePresence.Absent, harness.Lifecycle.Presence);
    }

    [Fact]
    public async Task A_contributor_rejection_a_source_failure_and_a_null_decision_all_fail_closed()
    {
        await using var rejected = await Harness.CreateAsync();
        rejected.Decisions.Current = Harness.NotReady(
            errorCode: WellKnownServiceReadinessContributorErrorCodes.ContributorFailed);
        await using var failing = await Harness.CreateAsync();
        failing.Decisions.Failure = new InvalidOperationException(ConsulFixture.Secret);
        await using var invalid = await Harness.CreateAsync();
        invalid.Decisions.Current = null;

        foreach (var harness in new[] { rejected, failing, invalid })
        {
            await harness.StartAsync();
            await harness.AdvanceUntilAsync(
                TimeSpan.FromSeconds(1),
                () => harness.Decisions.Calls >= 2,
                "the sampler never sampled twice");
            Assert.Empty(harness.Client.Operations);
            Assert.Equal(ConsulRemotePresence.Absent, harness.Lifecycle.Presence);
        }

        // A source failure and an unusable decision are classified safely and value-free.
        Assert.All(
            failing.Observer.Diagnostics.Concat(invalid.Observer.Diagnostics),
            diagnostic =>
            {
                Assert.Equal(ConsulLifecycleDiagnostics.ReadinessUnavailable, diagnostic.Classification);
                Assert.DoesNotContain(ConsulFixture.Secret, diagnostic.ToString(), StringComparison.Ordinal);
            });
        Assert.Empty(rejected.Observer.Diagnostics);
    }

    [Fact]
    public async Task Losing_readiness_deregisters_the_same_registration_id()
    {
        await using var harness = await Harness.CreateAsync();

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        var registrationId = harness.Client.LastRegistrationId;
        harness.Decisions.Current = Harness.NotReady();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.NotReady,
            "the lifecycle never deregistered");

        Assert.Equal(ConsulRemotePresence.Absent, harness.Lifecycle.Presence);
        Assert.Equal(1, harness.Client.Registers);
        Assert.Equal(1, harness.Client.Deregisters);
        Assert.Equal(registrationId, harness.Client.LastRegistrationId);
        Assert.Equal(1, harness.Client.MaximumConcurrent);
    }

    [Fact]
    public async Task Losing_readiness_during_a_register_settles_it_before_deregistering()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Harness.ScriptedClient { Gate = gate };
        await using var harness = await Harness.CreateAsync(client: client);

        await harness.StartAsync();
        await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConsulLifecycleState.Registering, harness.Lifecycle.State);

        // The desire flips while the register is still in flight.
        harness.Decisions.Current = Harness.NotReady();
        await harness.AdvanceUntilAsync(
            TimeSpan.FromSeconds(1),
            () => harness.Lifecycle.Presence == ConsulRemotePresence.Unknown,
            "the cancelled register never settled");
        gate.SetResult();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.NotReady,
            "the cleanup deregistration never completed");

        // The register was cancelled, its outcome is Unknown, and the cleanup never overlapped it.
        Assert.Equal(["register", "deregister"], harness.Client.Operations);
        Assert.Equal(1, harness.Client.MaximumConcurrent);
        Assert.Equal(ConsulRemotePresence.Absent, harness.Lifecycle.Presence);
        Assert.Contains(
            harness.Observer.Diagnostics,
            diagnostic => diagnostic.Classification == ConsulLifecycleDiagnostics.RegisterUnavailable);
    }

    [Fact]
    public async Task Regaining_readiness_during_a_deregister_never_overlaps_the_register()
    {
        var client = new Harness.ScriptedClient();
        await using var harness = await Harness.CreateAsync(client: client);

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        client.Arm();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Gate = gate;
        harness.Decisions.Current = Harness.NotReady();
        await harness.AdvanceUntilAsync(
            TimeSpan.FromSeconds(1),
            () => client.Deregisters == 1,
            "the deregister never started");
        Assert.Equal(ConsulLifecycleState.Deregistering, harness.Lifecycle.State);

        harness.Decisions.Current = Harness.Ready();
        await harness.AdvanceUntilAsync(
            TimeSpan.FromSeconds(1),
            () => harness.Lifecycle.Presence == ConsulRemotePresence.Unknown,
            "the interrupted deregister never settled");
        gate.SetResult();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never re-registered");

        Assert.Equal(1, harness.Client.MaximumConcurrent);
        Assert.Equal(ConsulRemotePresence.Present, harness.Lifecycle.Presence);
    }

    [Theory]
    [InlineData(ConsulClientResult.Rejected, ConsulLifecycleDiagnostics.RegisterRejected)]
    [InlineData(ConsulClientResult.Unavailable, ConsulLifecycleDiagnostics.RegisterUnavailable)]
    [InlineData((ConsulClientResult)999, ConsulLifecycleDiagnostics.RegisterUnavailable)]
    public async Task Every_unsuccessful_register_result_is_unknown_and_retried(
        ConsulClientResult result,
        string classification)
    {
        var client = new Harness.ScriptedClient { Outcome = (_, _) => result };
        await using var harness = await Harness.CreateAsync(client: client);

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Backoff,
            "the failed register never entered backoff");

        // A register that did not succeed never claims absence.
        Assert.Equal(ConsulRemotePresence.Unknown, harness.Lifecycle.Presence);
        Assert.Equal(classification, harness.Observer.Diagnostics[0].Classification);
        Assert.Equal(1, client.Registers);
    }

    [Fact]
    public async Task A_register_timeout_is_unknown_and_never_asserts_absence()
    {
        var client = new Harness.ScriptedClient
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options => options.ConsulOperationBudget = TimeSpan.FromSeconds(2));

        await harness.StartAsync();
        await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Backoff,
            "the register budget never expired");

        Assert.Equal(ConsulRemotePresence.Unknown, harness.Lifecycle.Presence);
        Assert.Equal(
            ConsulLifecycleDiagnostics.RegisterTimeout,
            harness.Observer.Diagnostics[0].Classification);
    }

    [Fact]
    public async Task The_retry_delay_is_exact_exponential_without_jitter_and_saturates()
    {
        var client = new Harness.ScriptedClient { Outcome = (_, _) => ConsulClientResult.Unavailable };
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options =>
            {
                options.InitialRetryDelay = TimeSpan.FromMilliseconds(100);
                options.MaximumRetryDelay = TimeSpan.FromMilliseconds(400);
                options.ReadinessPollInterval = TimeSpan.FromSeconds(30);
            });

        await harness.StartAsync();
        // 100 ms, 200 ms, 400 ms, then saturated at 400 ms.
        foreach (var delay in new[] { 100, 200, 400, 400 })
        {
            await Harness.WaitAsync(
                () => harness.Lifecycle.State == ConsulLifecycleState.Backoff,
                "the failed register never entered backoff");
            // Captured only once the attempt has settled into its backoff.
            var attempt = client.Registers;
            var due = harness.Time.GetUtcNow() + TimeSpan.FromMilliseconds(delay);
            await harness.Time.WhenTimerScheduledAsync(due)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            harness.Time.Advance(TimeSpan.FromMilliseconds(delay - 1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.Equal(attempt, client.Registers);

            harness.Time.Advance(TimeSpan.FromMilliseconds(1));
            await Harness.WaitAsync(
                () => client.Registers == attempt + 1,
                "the retry never started after its exact delay");
        }

        // Every attempt so far recorded exactly one safe classification.
        Assert.True(harness.Observer.Diagnostics.Count >= 4, "four failures were not classified");
        Assert.All(
            harness.Observer.Diagnostics,
            diagnostic => Assert.Equal(
                ConsulLifecycleDiagnostics.RegisterUnavailable,
                diagnostic.Classification));
    }

    [Fact]
    public async Task A_success_and_a_desire_change_both_reset_the_failure_count()
    {
        var client = new Harness.ScriptedClient
        {
            Outcome = (kind, call) => kind == "register" && call <= 2
                ? ConsulClientResult.Unavailable
                : ConsulClientResult.Success,
        };
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options =>
            {
                options.InitialRetryDelay = TimeSpan.FromMilliseconds(100);
                options.MaximumRetryDelay = TimeSpan.FromSeconds(5);
                options.ReadinessPollInterval = TimeSpan.FromSeconds(30);
            });

        await harness.StartAsync();
        await AdvanceToNextAttemptAsync(harness, TimeSpan.FromMilliseconds(100));
        await Harness.WaitAsync(() => client.Registers == 2, "the first retry never started");
        await AdvanceToNextAttemptAsync(harness, TimeSpan.FromMilliseconds(200));
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the third register never succeeded");

        // A later failure starts again at the initial delay because the success reset the counter.
        client.Outcome = (_, _) => ConsulClientResult.Unavailable;
        harness.Decisions.Current = Harness.NotReady();
        await harness.AdvanceUntilAsync(
            TimeSpan.FromSeconds(30),
            () => client.Deregisters == 1,
            "the deregister never ran");
        await AdvanceToNextAttemptAsync(harness, TimeSpan.FromMilliseconds(100));
        await Harness.WaitAsync(() => client.Deregisters == 2, "the deregister retry never started");
    }

    [Fact]
    public async Task Stop_from_a_registered_state_deregisters_and_disposes_the_session()
    {
        await using var harness = await Harness.CreateAsync();

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        await harness.StopAsync();

        Assert.Equal(ConsulRemotePresence.Absent, harness.Lifecycle.Presence);
        Assert.Equal(1, harness.Client.Deregisters);
        Assert.True(harness.Client.Disposed);
        Assert.Equal(1, harness.Client.Disposals);
    }

    [Fact]
    public async Task Stop_from_not_ready_and_absent_disposes_without_a_remote_call()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Decisions.Current = Harness.NotReady();

        await harness.StartAsync();
        await Harness.WaitAsync(() => harness.Decisions.Calls >= 1, "the sampler never ran");
        await harness.StopAsync();

        Assert.Empty(harness.Client.Operations);
        Assert.True(harness.Client.Disposed);
    }

    [Fact]
    public async Task Stop_during_a_register_settles_it_and_then_cleans_up()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Harness.ScriptedClient { Gate = gate };
        await using var harness = await Harness.CreateAsync(client: client);

        await harness.StartAsync();
        await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stop = harness.StopAsync();
        await Harness.WaitAsync(
            () => client.Operations.Count >= 2,
            "the cleanup deregistration never started");
        gate.SetResult();
        await stop;

        Assert.Equal(["register", "deregister"], harness.Client.Operations);
        Assert.Equal(1, harness.Client.MaximumConcurrent);
        Assert.Equal(ConsulRemotePresence.Absent, harness.Lifecycle.Presence);
        Assert.True(harness.Client.Disposed);
    }

    [Fact]
    public async Task An_exhausted_shutdown_budget_never_claims_remote_absence()
    {
        var client = new Harness.ScriptedClient { Outcome = (_, _) => ConsulClientResult.Unavailable };
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options =>
            {
                options.ShutdownBudget = TimeSpan.FromSeconds(1);
                options.InitialRetryDelay = TimeSpan.FromMilliseconds(500);
                options.MaximumRetryDelay = TimeSpan.FromMilliseconds(500);
                options.ReadinessPollInterval = TimeSpan.FromSeconds(30);
            });

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Backoff,
            "the failed register never entered backoff");
        var stop = harness.StopAsync();
        await Harness.WaitAsync(() => client.Deregisters >= 1, "the cleanup never started");
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await stop;

        Assert.Equal(ConsulRemotePresence.Unknown, harness.Lifecycle.Presence);
        Assert.Contains(
            harness.Observer.Diagnostics,
            diagnostic => diagnostic.Classification == ConsulLifecycleDiagnostics.ShutdownTimeout);
        Assert.True(harness.Client.Disposed);
    }

    [Fact]
    public async Task Stop_caller_cancellation_takes_precedence_and_starts_no_new_work()
    {
        var client = new Harness.ScriptedClient { Outcome = (_, _) => ConsulClientResult.Unavailable };
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options => options.ReadinessPollInterval = TimeSpan.FromSeconds(30));

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Backoff,
            "the failed register never entered backoff");
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        await harness.StopWithTokenAsync(abort.Token);

        // No absence is claimed, no register is started, and the session is still released.
        Assert.Equal(ConsulRemotePresence.Unknown, harness.Lifecycle.Presence);
        Assert.Equal(1, client.Registers);
        Assert.True(harness.Client.Disposed);
    }

    [Fact]
    public async Task Stop_cancels_an_in_flight_operation_even_without_a_consumable_desire_signal()
    {
        // The owner is parked outside the desire semaphore while a spare signal is left pending, so
        // the Signal() in StopAsync is a no-op and the only wake-up the owner can get is the
        // cancelled lifetime token. The stop still has to cancel the attempt it is waiting on.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hold = new ManualResetEventSlim(false);
        var client = new Harness.ScriptedClient { Gate = gate, Hold = hold };
        await using var harness = await Harness.CreateAsync(
            client: client,
            configure: options =>
            {
                options.ReadinessPollInterval = TimeSpan.FromMilliseconds(100);
                options.ConsulOperationBudget = TimeSpan.FromSeconds(30);
                options.ShutdownBudget = TimeSpan.FromSeconds(60);
            });

        await harness.StartAsync();
        await client.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var startedAt = harness.Time.GetUtcNow();

        // Sample 2 flips the desire and fills the semaphore; sample 3 flips it back but finds the
        // signal already pending, so its Release is skipped. Sample 4 only proves 3 has settled.
        harness.Decisions.Current = Harness.NotReady();
        await harness.AdvanceUntilAsync(
            TimeSpan.FromMilliseconds(100),
            () => harness.Decisions.Calls >= 2,
            "the sampler never published the not-ready desire");
        harness.Decisions.Current = Harness.Ready();
        await harness.AdvanceUntilAsync(
            TimeSpan.FromMilliseconds(100),
            () => harness.Decisions.Calls >= 4,
            "the sampler never published the restored ready desire");

        // From here the clock never moves, so neither the operation budget nor the shutdown budget
        // can end this: only cancellation can.
        client.Gate = null;
        var stop = harness.StopAsync();
        hold.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // The register ended because the stop cancelled it, not because a budget elapsed: the gate
        // it was waiting on is still unreleased and the clock never reached the operation budget.
        Assert.False(gate.Task.IsCompleted);
        Assert.True(
            harness.Time.GetUtcNow() - startedAt < TimeSpan.FromSeconds(30),
            "the operation budget elapsed, so the cancellation is not what ended the register");
        Assert.Equal(["register", "deregister"], client.Operations);
        Assert.Equal(1, client.MaximumConcurrent);
        Assert.Equal(ConsulRemotePresence.Absent, harness.Lifecycle.Presence);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task The_observer_retains_a_bounded_window_of_the_diagnostics_it_records()
    {
        // The fail-closed readiness path records once per poll for the life of the process, so the
        // retention has to be bounded even though the count keeps rising.
        var capacity = ConsulLifecycleObserver.Capacity;
        await using var harness = await Harness.CreateAsync(
            configure: options => options.ReadinessPollInterval = TimeSpan.FromMilliseconds(100));
        harness.Decisions.Failure = new InvalidOperationException(ConsulFixture.Secret);

        await harness.StartAsync();
        await harness.AdvanceUntilAsync(
            TimeSpan.FromMilliseconds(100),
            () => harness.Observer.RecordedCount > capacity + 5,
            "the fail-closed sampler never recorded past the retention capacity");
        await harness.StopAsync();

        var retained = harness.Observer.Diagnostics;
        Assert.Equal(capacity, retained.Count);
        Assert.True(
            harness.Observer.RecordedCount > retained.Count,
            "the recorded count stopped rising with the retained window");
        Assert.All(retained, diagnostic => Assert.Equal(
            ConsulLifecycleDiagnostics.ReadinessUnavailable,
            diagnostic.Classification));
        Assert.Empty(harness.Client.Operations);
    }

    [Fact]
    public async Task A_disposal_failure_is_a_safe_classification_and_is_not_retried()
    {
        var client = new Harness.ScriptedClient { FailDisposal = true };
        await using var harness = await Harness.CreateAsync(client: client);
        harness.Decisions.Current = Harness.NotReady();

        await harness.StartAsync();
        await Harness.WaitAsync(() => harness.Decisions.Calls >= 1, "the sampler never ran");
        await harness.StopAsync();

        Assert.Equal(1, client.Disposals);
        var diagnostic = Assert.Single(
            harness.Observer.Diagnostics,
            item => item.Classification == ConsulLifecycleDiagnostics.SessionDisposalFailed);
        Assert.DoesNotContain(ConsulFixture.Secret, diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_failures_and_caller_cancellation_keep_the_ownership_boundary()
    {
        await using var unavailable = await Harness.CreateAsync(activate: false);
        var snapshotFailure = await Assert.ThrowsAsync<ConsulConfigurationException>(
            () => unavailable.StartAsync());

        await using var broken = await Harness.CreateAsync();
        broken.Fixture.ClientFactory.CreateClient = _ =>
            throw new InvalidOperationException(ConsulFixture.Secret);
        var creationFailure = await Assert.ThrowsAsync<ConsulConfigurationException>(
            () => broken.StartAsync());

        await using var cancelled = await Harness.CreateAsync();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled.StartWithTokenAsync(abort.Token));

        Assert.Equal(ConsulConfigurationError.SnapshotUnavailable, snapshotFailure.Error);
        Assert.Equal(ConsulConfigurationError.ClientCreationFailed, creationFailure.Error);
        Assert.DoesNotContain(ConsulFixture.Secret, creationFailure.ToString(), StringComparison.Ordinal);
        Assert.Equal(abort.Token, cancellation.CancellationToken);
        foreach (var harness in new[] { unavailable, broken, cancelled })
        {
            Assert.Empty(harness.Client.Operations);
        }
    }

    [Fact]
    public async Task A_newer_active_snapshot_does_not_rebind_the_session_or_the_registration()
    {
        await using var harness = await Harness.CreateAsync();

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Registered,
            "the lifecycle never registered");
        var version = harness.Lifecycle.SnapshotVersion;
        var registrationId = harness.Client.LastRegistrationId;

        var changed = ConsulFixture.Enabled("rotated-token-value");
        changed[ConsulSettingDefinitions.ServiceName] = "orders-api-v2";
        await harness.Fixture.ActivateAsync(changed, version: 2);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.Decisions.Current = Harness.NotReady();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.NotReady,
            "the lifecycle never deregistered");

        // The lifecycle never calls CreateClient again, so the session, version, and ID are fixed.
        Assert.Equal(1, harness.Fixture.ClientFactory.Calls);
        Assert.Equal(version, harness.Lifecycle.SnapshotVersion);
        Assert.Equal(registrationId, harness.Client.LastRegistrationId);
    }

    [Fact]
    public async Task The_sentinel_token_never_reaches_a_diagnostic_result_or_projection()
    {
        // Register is rejected so a failure classification is recorded; the cleanup deregistration
        // then succeeds so the stop settles without needing the clock advanced.
        var client = new Harness.ScriptedClient
        {
            Outcome = (kind, _) => kind == "register"
                ? ConsulClientResult.Rejected
                : ConsulClientResult.Success,
        };
        await using var harness = await Harness.CreateAsync(client: client);

        await harness.StartAsync();
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Backoff,
            "the failed register never entered backoff");
        await harness.StopAsync();

        var projections = harness.Observer.Diagnostics
            .Select(diagnostic => diagnostic.ToString())
            .Append(harness.Lifecycle.State.ToString())
            .Append(harness.Lifecycle.Presence.ToString());
        Assert.All(projections, projection =>
        {
            Assert.DoesNotContain(ConsulFixture.Secret, projection, StringComparison.Ordinal);
            Assert.DoesNotContain("agent.example", projection, StringComparison.Ordinal);
            Assert.DoesNotContain("orders-api", projection, StringComparison.Ordinal);
            Assert.DoesNotContain("orders.example", projection, StringComparison.Ordinal);
        });
        Assert.NotEmpty(harness.Observer.Diagnostics);
    }

    [Theory]
    [InlineData("ReadinessPollInterval", 99)]
    [InlineData("ReadinessPollInterval", 30_001)]
    [InlineData("ReadinessCallBudget", 99)]
    [InlineData("ReadinessCallBudget", 60_001)]
    [InlineData("ConsulOperationBudget", 99)]
    [InlineData("ConsulOperationBudget", 30_001)]
    [InlineData("InitialRetryDelay", 49)]
    [InlineData("InitialRetryDelay", 5_001)]
    [InlineData("MaximumRetryDelay", 30_001)]
    [InlineData("ShutdownBudget", 999)]
    [InlineData("ShutdownBudget", 60_001)]
    public void An_out_of_range_timing_value_fails_registration_before_the_host_is_built(
        string field,
        int milliseconds)
    {
        var services = new ServiceCollection();

        var failure = Assert.Throws<ConsulConfigurationException>(() =>
            services.AddServiceMantleConsul(options => Apply(options, field, milliseconds)));

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, failure.Error);
        Assert.Empty(services);
    }

    [Fact]
    public void A_maximum_below_the_initial_delay_and_a_conflicting_repeat_are_rejected()
    {
        var services = new ServiceCollection();
        var conflicting = new ServiceCollection();

        var inverted = Assert.Throws<ConsulConfigurationException>(() =>
            services.AddServiceMantleConsul(options =>
            {
                options.InitialRetryDelay = TimeSpan.FromSeconds(2);
                options.MaximumRetryDelay = TimeSpan.FromSeconds(1);
            }));
        conflicting.AddSingleton(ServiceId.Parse("orders"));
        conflicting.AddSingleton(InstanceId.Parse("orders-01"));
        conflicting.AddSingleton<IServiceSettingCurrentSnapshotAccessor>(
            new ServiceSettingCurrentSnapshotAccessor());
        conflicting.AddServiceMantleConsul();
        conflicting.AddServiceMantleConsul(options =>
            options.ShutdownBudget = TimeSpan.FromSeconds(20));

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, inverted.Error);
        // Two disagreeing registrations fail when the lifecycle is resolved, not silently.
        using var provider = conflicting.BuildServiceProvider();
        var conflict = Assert.Throws<ConsulConfigurationException>(
            provider.GetRequiredService<ConsulRegistrationLifecycle>);
        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, conflict.Error);
    }

    [Fact]
    public void The_default_and_boundary_timing_values_are_accepted()
    {
        var defaults = new ConsulLifecycleOptions();

        var settings = defaults.Validate();
        var boundaries = new ConsulLifecycleOptions
        {
            ReadinessPollInterval = TimeSpan.FromMilliseconds(100),
            ReadinessCallBudget = TimeSpan.FromSeconds(60),
            ConsulOperationBudget = TimeSpan.FromMilliseconds(100),
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaximumRetryDelay = TimeSpan.FromMilliseconds(50),
            ShutdownBudget = TimeSpan.FromSeconds(60),
        }.Validate();

        Assert.Equal(TimeSpan.FromSeconds(1), settings.ReadinessPollInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.ReadinessCallBudget);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.ConsulOperationBudget);
        Assert.Equal(TimeSpan.FromMilliseconds(250), settings.InitialRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.MaximumRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), settings.ShutdownBudget);
        // The exponential ladder saturates without overflowing.
        Assert.Equal(TimeSpan.FromMilliseconds(250), settings.RetryDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(500), settings.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(5), settings.RetryDelay(10));
        Assert.Equal(TimeSpan.FromSeconds(5), settings.RetryDelay(int.MaxValue));
        Assert.Equal(TimeSpan.FromMilliseconds(50), boundaries.MaximumRetryDelay);
    }

    /// <summary>Advances exactly one retry delay, proving the attempt does not start early.</summary>
    private static async Task AdvanceToNextAttemptAsync(Harness harness, TimeSpan delay)
    {
        await Harness.WaitAsync(
            () => harness.Lifecycle.State == ConsulLifecycleState.Backoff,
            "the failure never entered backoff");
        var due = harness.Time.GetUtcNow() + delay;
        await harness.Time.WhenTimerScheduledAsync(due)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        harness.Time.Advance(delay - TimeSpan.FromMilliseconds(1));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromMilliseconds(1));
    }

    private static void Apply(ConsulLifecycleOptions options, string field, int milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(milliseconds);
        switch (field)
        {
            case "ReadinessPollInterval":
                options.ReadinessPollInterval = value;
                break;
            case "ReadinessCallBudget":
                options.ReadinessCallBudget = value;
                break;
            case "ConsulOperationBudget":
                options.ConsulOperationBudget = value;
                break;
            case "InitialRetryDelay":
                options.InitialRetryDelay = value;
                break;
            case "MaximumRetryDelay":
                options.MaximumRetryDelay = value;
                break;
            default:
                options.ShutdownBudget = value;
                break;
        }
    }
}
