using ServiceMantle.Audit;
using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

/// <summary>
/// Drives the completion checkpoints of <see cref="ServiceSettingUpdateService.UpdateAsync"/>
/// with self-contained transaction, root-key and validator doubles.
/// </summary>
public sealed class ServiceSettingUpdateCancellationTests
{
    private const string Canary = "injected-secret-canary";
    private static readonly ServiceId OtherService = ServiceId.Parse("other-service");

    [Fact]
    public async Task Pre_cancelled_caller_throws_before_Load_without_calling_Apply()
    {
        var transaction = new FakeTransaction();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = CreateService(transaction);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateAsync(Command(0, ("product.name", "B")), cts.Token).AsTask());

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(0, transaction.LoadCalls);
        Assert.Equal(0, transaction.ApplyCalls);
    }

    [Fact]
    public async Task Null_command_still_fails_parameter_validation_first()
    {
        var transaction = new FakeTransaction();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = CreateService(transaction);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.UpdateAsync(null!, cts.Token).AsTask());

        Assert.Equal(0, transaction.LoadCalls);
        Assert.Equal(0, transaction.ApplyCalls);
    }

    public static TheoryData<ServiceSettingUpdateResult> ApplyResults => new()
    {
        ServiceSettingUpdateResult.Applied(1),
        ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.ValidationFailed),
        ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.VersionConflict),
        ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.VersionExhausted),
        ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.ProtectionFailed),
        ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.StorageFailed),
        ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.TransactionRequired),
        ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.ContextNotClean)
    };

    [Theory]
    [MemberData(nameof(ApplyResults))]
    public async Task Apply_normal_completion_after_cancellation_is_never_delivered(
        ServiceSettingUpdateResult result)
    {
        using var cts = new CancellationTokenSource();
        var transaction = new FakeTransaction
        {
            OnApply = () =>
            {
                cts.Cancel();
                return result;
            }
        };
        var service = CreateService(transaction);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateAsync(Command(0, ("product.name", "B")), cts.Token).AsTask());

        AssertSafeCallerCancellation(exception, cts.Token);
        Assert.Equal(1, transaction.LoadCalls);
        Assert.Equal(1, transaction.ApplyCalls);
    }

    [Fact]
    public async Task Apply_ordinary_exception_after_cancellation_hides_original_details()
    {
        using var cts = new CancellationTokenSource();
        var transaction = new FakeTransaction
        {
            OnApply = () =>
            {
                cts.Cancel();
                throw new InvalidOperationException(Canary);
            }
        };
        var service = CreateService(transaction);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateAsync(Command(0, ("product.name", "B")), cts.Token).AsTask());

        AssertSafeCallerCancellation(exception, cts.Token);
        Assert.DoesNotContain(Canary, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, transaction.ApplyCalls);
    }

    [Fact]
    public async Task Apply_internal_cancellation_after_caller_cancel_keeps_caller_priority()
    {
        using var cts = new CancellationTokenSource();
        using var internalCts = new CancellationTokenSource();
        var transaction = new FakeTransaction
        {
            OnApply = () =>
            {
                cts.Cancel();
                throw new OperationCanceledException(Canary, internalCts.Token);
            }
        };
        var service = CreateService(transaction);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateAsync(Command(0, ("product.name", "B")), cts.Token).AsTask());

        AssertSafeCallerCancellation(exception, cts.Token);
        Assert.DoesNotContain(Canary, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_normal_completion_after_cancellation_keeps_caller_priority()
    {
        using var cts = new CancellationTokenSource();
        var transaction = new FakeTransaction
        {
            OnLoad = () =>
            {
                cts.Cancel();
                return FakeTransaction.EmptySnapshot(ServiceId.Parse("update-cancellation-tests"));
            }
        };
        var service = CreateService(transaction);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateAsync(Command(0, ("product.name", "B")), cts.Token).AsTask());

        AssertSafeCallerCancellation(exception, cts.Token);
        Assert.Equal(1, transaction.LoadCalls);
        Assert.Equal(0, transaction.ApplyCalls);
    }

    [Fact]
    public async Task Load_ordinary_exception_without_cancellation_stays_StorageFailed()
    {
        var transaction = new FakeTransaction
        {
            OnLoad = () => throw new InvalidOperationException(Canary)
        };
        var service = CreateService(transaction);

        var result = await service.UpdateAsync(Command(0, ("product.name", "B")));

        Assert.Equal(ServiceSettingUpdateStatus.StorageFailed, result.Status);
        Assert.Equal(0, transaction.ApplyCalls);
    }

    [Fact]
    public async Task Load_internal_cancellation_without_caller_cancellation_stays_StorageFailed()
    {
        using var internalCts = new CancellationTokenSource();
        var transaction = new FakeTransaction
        {
            OnLoad = () => throw new OperationCanceledException(internalCts.Token)
        };
        var service = CreateService(transaction);

        var result = await service.UpdateAsync(Command(0, ("product.name", "B")));

        Assert.Equal(ServiceSettingUpdateStatus.StorageFailed, result.Status);
    }

    [Fact]
    public async Task Foreign_service_id_and_version_decisions_without_cancellation_unchanged()
    {
        var service = ServiceId.Parse("update-cancellation-tests");
        var transaction = new FakeTransaction
        {
            Snapshot = new ServiceSettingStoreSnapshot(
                OtherService,
                5,
                new Dictionary<string, string>(),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                "operator-1",
                restartRequired: false)
        };
        var updateService = CreateService(transaction);

        Assert.Equal(
            ServiceSettingUpdateStatus.StorageFailed,
            (await updateService.UpdateAsync(Command(0, ("product.name", "B")))).Status);

        transaction.Snapshot = new ServiceSettingStoreSnapshot(
            service,
            5,
            new Dictionary<string, string>(),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "operator-1",
            restartRequired: false);
        Assert.Equal(
            ServiceSettingUpdateStatus.VersionConflict,
            (await updateService.UpdateAsync(Command(0, ("product.name", "B")))).Status);

        transaction.Snapshot = new ServiceSettingStoreSnapshot(
            service,
            long.MaxValue,
            new Dictionary<string, string>(),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "operator-1",
            restartRequired: false);
        Assert.Equal(
            ServiceSettingUpdateStatus.VersionExhausted,
            (await updateService.UpdateAsync(Command(long.MaxValue, ("product.name", "B")))).Status);
    }

    [Fact]
    public async Task Root_key_completion_after_cancellation_keeps_caller_priority()
    {
        using var cts = new CancellationTokenSource();
        var rootKeySource = new FakeRootKeySource
        {
            OnGetKey = () =>
            {
                cts.Cancel();
                return "root-key";
            }
        };
        var transaction = new FakeTransaction();
        var service = CreateService(transaction, rootKeySource, sensitive: true);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateAsync(Command(0, ("product.secret", "raw-value")), cts.Token).AsTask());

        AssertSafeCallerCancellation(exception, cts.Token);
        Assert.Equal(1, rootKeySource.Calls);
        Assert.Equal(0, transaction.ApplyCalls);
    }

    public static TheoryData<string> RootKeyFailures => new() { "throw", "empty" };

    [Theory]
    [MemberData(nameof(RootKeyFailures))]
    public async Task Root_key_failure_without_cancellation_stays_ProtectionFailed(string mode)
    {
        var rootKeySource = new FakeRootKeySource
        {
            OnGetKey = mode switch
            {
                "throw" => () => throw new InvalidOperationException(Canary),
                _ => () => ""
            }
        };
        var transaction = new FakeTransaction();
        var service = CreateService(transaction, rootKeySource, sensitive: true);

        var result = await service.UpdateAsync(Command(0, ("product.secret", "raw-value")));

        Assert.Equal(ServiceSettingUpdateStatus.ProtectionFailed, result.Status);
        Assert.Equal(0, transaction.ApplyCalls);
    }

    [Fact]
    public async Task Root_key_failure_after_cancellation_hides_original_details()
    {
        using var cts = new CancellationTokenSource();
        var rootKeySource = new FakeRootKeySource
        {
            OnGetKey = () =>
            {
                cts.Cancel();
                throw new InvalidOperationException(Canary);
            }
        };
        var transaction = new FakeTransaction();
        var service = CreateService(transaction, rootKeySource, sensitive: true);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateAsync(Command(0, ("product.secret", "raw-value")), cts.Token).AsTask());

        AssertSafeCallerCancellation(exception, cts.Token);
        Assert.DoesNotContain(Canary, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Composite_validator_completion_after_cancellation_keeps_caller_priority()
    {
        using var cts = new CancellationTokenSource();
        var cancellingValidator = new CancellingValidator(() => cts.Cancel());
        var transaction = new FakeTransaction();
        var service = CreateService(transaction, validator: cancellingValidator);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateAsync(Command(0, ("product.name", "B")), cts.Token).AsTask());

        AssertSafeCallerCancellation(exception, cts.Token);
        Assert.Equal(0, transaction.ApplyCalls);
    }

    [Fact]
    public async Task Invalid_candidate_without_cancellation_keeps_safe_validation_errors()
    {
        var transaction = new FakeTransaction();
        var service = CreateService(transaction);

        var result = await service.UpdateAsync(Command(0, ("product.name", "B"), ("count", "not-a-number")));

        Assert.Equal(ServiceSettingUpdateStatus.ValidationFailed, result.Status);
        Assert.Equal(1, transaction.LoadCalls);
        Assert.Equal(0, transaction.ApplyCalls);
    }

    [Fact]
    public async Task Two_independent_services_only_cancel_the_affected_one()
    {
        using var firstCts = new CancellationTokenSource();
        var first = new FakeTransaction
        {
            OnApply = () =>
            {
                firstCts.Cancel();
                return ServiceSettingUpdateResult.Applied(1);
            }
        };
        var second = new FakeTransaction();
        var firstService = CreateService(first);
        var secondService = CreateService(second);

        var firstTask = Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            firstService.UpdateAsync(Command(0, ("product.name", "B")), firstCts.Token).AsTask());
        var secondResult = await secondService.UpdateAsync(Command(0, ("product.name", "C")));

        Assert.Equal(firstCts.Token, (await firstTask).CancellationToken);
        Assert.Equal(ServiceSettingUpdateStatus.Applied, secondResult.Status);
        Assert.Equal(1, second.ApplyCalls);
    }

    private static void AssertSafeCallerCancellation(
        OperationCanceledException exception,
        CancellationToken expectedToken)
    {
        Assert.Equal(expectedToken, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Canary, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, exception.ToString(), StringComparison.Ordinal);
    }

    private static ServiceSettingUpdateService CreateService(
        IServiceSettingUpdateTransaction transaction,
        IServiceSettingRootKeySource? rootKeySource = null,
        bool sensitive = false,
        IServiceSettingCompositeValidator? validator = null) =>
        new(
            ServiceId.Parse("update-cancellation-tests"),
            Registry(sensitive, validator),
            transaction,
            rootKeySource);

    private static ServiceSettingDefinitionRegistry Registry(
        bool sensitive,
        IServiceSettingCompositeValidator? validator) =>
        new(
            [new TestDefinitions(sensitive)],
            validator is null ? null : [validator]);

    private static ServiceSettingUpdateCommand Command(long version, params (string Key, string? Value)[] changes) =>
        new(
            version,
            changes.ToDictionary(item => item.Key, item => item.Value),
            ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, "operator-1"));

    private sealed class TestDefinitions(bool sensitive) : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() =>
        [
            new("product.name", ServiceSettingValueType.String, defaultValue: "A"),
            new("count", ServiceSettingValueType.Number, defaultValue: "1"),
            new("product.secret", ServiceSettingValueType.String, isSensitive: sensitive)
        ];
    }

    private sealed class CancellingValidator(Action cancel) : IServiceSettingCompositeValidator
    {
        public IEnumerable<ServiceSettingValidationError> Validate(ServiceSettingValidationContext context)
        {
            cancel();
            return [];
        }
    }

    private sealed class FakeRootKeySource : IServiceSettingRootKeySource
    {
        public Func<string> OnGetKey { get; set; } = () => "root-key";
        public int Calls { get; private set; }

        public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(OnGetKey());
        }
    }

    private sealed class FakeTransaction : IServiceSettingUpdateTransaction
    {
        public Func<ServiceSettingStoreSnapshot> OnLoad { get; set; } =
            () => EmptySnapshot(ServiceId.Parse("update-cancellation-tests"));
        public Func<ServiceSettingUpdateResult> OnApply { get; set; } =
            () => ServiceSettingUpdateResult.Applied(1);
        public ServiceSettingStoreSnapshot Snapshot
        {
            get => OnLoad();
            set => OnLoad = () => value;
        }
        public int LoadCalls { get; private set; }
        public int ApplyCalls { get; private set; }

        public ValueTask<ServiceSettingStoreSnapshot> LoadAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken)
        {
            LoadCalls++;
            return ValueTask.FromResult(OnLoad());
        }

        public ValueTask<ServiceSettingUpdateResult> ApplyAsync(
            ServiceId serviceId,
            ServiceSettingStoreUpdate update,
            IReadOnlyList<ManagementAuditEvent> audits,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;
            return ValueTask.FromResult(OnApply());
        }

        public static ServiceSettingStoreSnapshot EmptySnapshot(ServiceId serviceId) =>
            new(serviceId, 0, new Dictionary<string, string>(), null, null, restartRequired: false);
    }
}
