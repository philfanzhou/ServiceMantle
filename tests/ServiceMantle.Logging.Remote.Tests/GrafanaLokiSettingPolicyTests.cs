using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using ServiceMantle.Configuration;
using ServiceMantle.Logging.Pipeline;
using ServiceMantle.Logging.Remote;
using Xunit;
using static ServiceMantle.Logging.Remote.GrafanaLokiSettingStatus;

namespace ServiceMantle.Logging.Remote.Tests;

public sealed class GrafanaLokiSettingPolicyTests
{
    private const string Endpoint = "https://logs.example.test/prefix";
    private const string Authorization = "Bearer secret-policy-token";
    private static readonly ServiceId Identity = ServiceId.Parse("loki-policy-test");
    private static ServiceSettingDefinitionRegistry Registry(bool strict = true) => new(
        [new GrafanaLokiSettingDefinitions()], strict ? [new GrafanaLokiSettingDefinitions()] : []);

    private static Dictionary<string, string?> Values(string? endpoint, string? authorization, bool noAuth)
    {
        var values = new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.AllowNoAuthentication] = noAuth ? "TRUE" : "false"
        };
        if (endpoint is not null) values.Add(GrafanaLokiSettingDefinitions.Endpoint, endpoint);
        if (authorization is not null) values.Add(GrafanaLokiSettingDefinitions.Authorization, authorization);
        return values;
    }

    public static IEnumerable<object?[]> PolicyCases()
    {
        foreach (var noAuth in new[] { false, true })
        {
            yield return [null, null, noAuth, Disabled, true, null];
            yield return [null, Authorization, noAuth, EndpointMissing, false, "setting.required"];
            foreach (var invalid in new[] { "relative", "https:///", "ftp://logs.example.test", "https://user:secret@logs.example.test", "https://logs.example.test?secret=query", "https://logs.example.test#secret", "http://user:secret@logs.example.test", "http://logs.example.test?secret=query", "http://logs.example.test#secret" })
                yield return [invalid, noAuth ? null : Authorization, noAuth, EndpointInvalid, false, "loki.invalid_endpoint"];
            yield return ["http://127.0.0.1:3100", noAuth ? null : Authorization, noAuth, Enabled, true, null];
            yield return ["http://logs.example.test/prefix", noAuth ? null : Authorization, noAuth, Enabled, true, null];
            yield return [Endpoint, null, noAuth, noAuth ? Enabled : AuthorizationMissing,
                noAuth, noAuth ? null : "setting.required"];
            yield return [Endpoint, Authorization, noAuth, noAuth ? AuthorizationInvalid : Enabled,
                !noAuth, noAuth ? "loki.authorization_value_invalid" : null];
            foreach (var invalid in new[] { "", " ", "bad\rvalue", "bad\nvalue", "bad\0value", new string('x', 4097) })
                yield return [Endpoint, invalid, noAuth,
                    !noAuth && string.IsNullOrWhiteSpace(invalid) ? AuthorizationMissing : AuthorizationInvalid,
                    false, "loki.authorization_value_invalid"];
            if (!noAuth)
            {
                yield return [Endpoint, "x", noAuth, Enabled, true, null];
                yield return [Endpoint, new string('x', 4096), noAuth, Enabled, true, null];
            }
        }
    }

    [Theory]
    [MemberData(nameof(PolicyCases))]
    public async Task Complete_candidates_typed_snapshots_and_registration_follow_one_policy(
        string? endpoint, string? authorization, bool noAuth,
        GrafanaLokiSettingStatus expected, bool valid, string? code)
    {
        var values = Values(endpoint, authorization, noAuth);
        var validation = Registry().Validate(values);
        Assert.Equal(valid, validation.IsValid);
        if (code is not null) Assert.Contains(validation.Errors, error => error.ErrorCode == code);
        Assert.All(validation.Errors, error => AssertSafe(error.ToString(), endpoint, authorization));
        var snapshot = await LoadAsync(values);
        var builder = Host.CreateApplicationBuilder();
        var factory = new CapturingFactory();
        builder.Services.AddSingleton<ILokiHttpMessageHandlerFactory>(factory);
        builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(_ => throw new InvalidOperationException("Resolver factory must not run"));
        var state = builder.AddServiceMantleGrafanaLokiFromSettings(snapshot, options =>
        {
            options.Enabled = true;
            options.Endpoint = new Uri("http://127.0.0.1:1234");
            options.AuthorizationHeaderResolverName = "callback-resolver";
        });
        Assert.Equal(expected, state.Status);
        Assert.Equal(expected, GrafanaLokiSettingState.Classify(endpoint, authorization, noAuth).Status);
        AssertSafe(state.ToString(), endpoint, authorization);
        var registration = Assert.Single(builder.Services, x => x.ServiceType == typeof(GrafanaLokiRegistration))
            .ImplementationInstance as GrafanaLokiRegistration;
        Assert.NotNull(registration);
        Assert.Equal(expected == Enabled, registration.Options.Enabled);
        Assert.Equal(expected == Enabled && !noAuth
            ? ServiceMantleGrafanaLokiHostApplicationBuilderExtensions.SettingDrivenAuthorizationResolverName : null,
            registration.Options.AuthorizationHeaderResolverName);
        Assert.Equal(expected == Enabled && !noAuth, builder.Services.Any(x =>
            x.ImplementationInstance is FixedRemoteLogAuthorizationResolver));
        if (expected != Enabled)
        {
            Assert.DoesNotContain(builder.Services, x => x.ServiceType == typeof(ISerilogSinkFactory));
            using var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, factory.Creations);
        }
    }

    [Fact]
    public async Task New_definitions_default_false_and_bad_persisted_types_preserve_the_current_snapshot()
    {
        var definitions = new GrafanaLokiSettingDefinitions().GetDefinitions().ToArray();
        var definition = Assert.Single(definitions, item => item.Key == GrafanaLokiSettingDefinitions.AllowNoAuthentication);
        Assert.Equal(ServiceSettingValueType.Boolean, definition.ValueType);
        Assert.Equal("false", definition.DefaultValue);
        Assert.False(definition.IsSensitive);
        Assert.True(definition.RequiresRestart);
        var invalid = Registry().Validate(new Dictionary<string, string?> { [GrafanaLokiSettingDefinitions.AllowNoAuthentication] = "not-a-boolean-secret" });
        Assert.Contains(invalid.Errors, error => error.Key == GrafanaLokiSettingDefinitions.AllowNoAuthentication && error.ErrorCode == "setting.invalid_boolean");
        Assert.All(invalid.Errors, error => Assert.DoesNotContain("not-a-boolean-secret", error.ToString()));
        var old = await LoadAsync(new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.Endpoint] = Endpoint,
            [GrafanaLokiSettingDefinitions.Authorization] = Authorization
        });
        Assert.False(old.Values[GrafanaLokiSettingDefinitions.AllowNoAuthentication].GetBoolean());
        Assert.True(old.Values[GrafanaLokiSettingDefinitions.AllowNoAuthentication].IsDefault);
        Assert.Equal(Enabled, GrafanaLokiSettingState.Classify(old).Status);
        Assert.True(GrafanaLokiSettingState.TryParseEndpoint("http://127.0.0.1", out _));
        Assert.Equal(Enabled, GrafanaLokiSettingState.Classify("http://127.0.0.1", Authorization).Status);

        var source = new MutableSource(new ServiceSettingSnapshotRead(Identity, 0, []));
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = new ServiceSettingSnapshotLoader(Identity, source, Registry(false), accessor);
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.True(accessor.TryGetCurrent(out var before));
        foreach (var type in new[] { ServiceSettingValueType.String, ServiceSettingValueType.Number })
        {
            source.Read = new(Identity, 1, [new(GrafanaLokiSettingDefinitions.AllowNoAuthentication, 1, type, "true")]);
            var result = await loader.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, x => x.ErrorCode == WellKnownServiceSettingSnapshotErrorCodes.ValueTypeMismatch);
            Assert.True(accessor.TryGetCurrent(out var after));
            Assert.Same(before, after);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Management_commit_loader_and_actual_requests_cover_both_transports_and_authentication_choices(bool http, bool noAuth)
    {
        await using var server = await LocalServer.StartAsync();
        var endpoint = http ? new Uri(server.Address, "prefix").ToString() : Endpoint;
        var store = new TransactionStore();
        var key = new CountingRootKey(noAuth);
        var service = new ServiceSettingUpdateService(Identity, Registry(), store, key);
        var oldSnapshot = await LoadStoreAsync(store, key);
        var result = await service.UpdateAsync(Command(0, Values(endpoint, noAuth ? null : Authorization, noAuth)), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Version);
        Assert.Equal(0, store.Committed.Version);
        Assert.Equal(1, store.ApplyCount);
        Assert.NotNull(store.Staged);
        Assert.True(store.Staged.RestartRequired);
        Assert.Equal(noAuth ? "true" : "false", store.Staged.Values[GrafanaLokiSettingDefinitions.AllowNoAuthentication]);
        if (!noAuth) Assert.StartsWith("sm:v1:", store.Staged.Values[GrafanaLokiSettingDefinitions.Authorization]);
        Assert.All(store.Audits, audit =>
        {
            Assert.Single(audit.Metadata);
            Assert.True(audit.Metadata.ContainsKey("key"));
            AssertSafe(audit.ToString(), endpoint, Authorization);
            AssertSafe(string.Join(" ", audit.Metadata.Values), endpoint, Authorization);
        });
        Assert.Equal(Disabled, GrafanaLokiSettingState.Classify(await LoadStoreAsync(store, key)).Status);
        store.Commit();
        var snapshot = await LoadStoreAsync(store, key);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(Disabled, GrafanaLokiSettingState.Classify(oldSnapshot).Status);
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        var factory = new CapturingFactory();
        if (!http) builder.Services.AddSingleton<ILokiHttpMessageHandlerFactory>(factory);
        if (noAuth) builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(_ =>
            throw new InvalidOperationException("No authentication must not enumerate resolver factories"));
        void Configure(GrafanaLokiOptions options)
        {
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
            options.AuthorizationHeaderResolverName = "callback-resolver";
        }
        var state = builder.AddServiceMantleGrafanaLokiFromSettings(snapshot, Configure);
        builder.AddServiceMantleGrafanaLokiFromSettings(snapshot, Configure);
        Assert.Equal(Enabled, state.Status);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ILogger<GrafanaLokiSettingPolicyTests>>()
            .LogInformation("policy-visible {Name} {Password}", "visible-policy-value", "structured-policy-secret");
        var captured = await (http ? server.Visible.Task : factory.Visible.Task)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("/prefix/loki/api/v1/push", captured.Path);
        Assert.Equal(noAuth ? "" : Authorization, captured.Authorization);
        Assert.Contains("visible-policy-value", captured.Body);
        Assert.Contains(StructuredLogSanitizer.RedactedValue, captured.Body);
        Assert.DoesNotContain("structured-policy-secret", captured.Body);
        Assert.DoesNotContain(Authorization, captured.Body);
        Assert.DoesNotContain(endpoint, captured.Body);
        // Saving a new version does not mutate this snapshot or the running sink.
        Assert.True((await service.UpdateAsync(Command(1, new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.Endpoint] = null,
            [GrafanaLokiSettingDefinitions.Authorization] = null
        }), TestContext.Current.CancellationToken)).Succeeded);
        store.Commit();
        Assert.Equal(Enabled, GrafanaLokiSettingState.Classify(snapshot).Status);
        Assert.Equal(Disabled, GrafanaLokiSettingState.Classify(await LoadStoreAsync(store, key)).Status);
        host.Services.GetRequiredService<ILogger<GrafanaLokiSettingPolicyTests>>()
            .LogInformation("policy-visible {Name}", "second-visible-policy-value");
        var afterSave = await (http ? server.SecondVisible.Task : factory.SecondVisible.Task)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(captured.Path, afterSave.Path);
        Assert.Equal(captured.Authorization, afterSave.Authorization);
        await host.StopAsync(TestContext.Current.CancellationToken);
        if (!http) Assert.Equal(1, factory.Creations);
        if (noAuth) Assert.Equal(0, key.Calls);
    }

    [Fact]
    public async Task Authentication_transitions_require_atomic_credential_removal_and_restore_and_support_old_package_rollback()
    {
        var store = new TransactionStore();
        var key = new CountingRootKey(false);
        var service = new ServiceSettingUpdateService(Identity, Registry(), store, key);
        var seed = new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.Endpoint] = Endpoint,
            [GrafanaLokiSettingDefinitions.Authorization] = Authorization
        };
        Assert.True((await service.UpdateAsync(Command(0, seed), TestContext.Current.CancellationToken)).Succeeded);
        store.Commit();
        var old = await LoadStoreAsync(store, key);
        var rejected = await service.UpdateAsync(Command(1, new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.AllowNoAuthentication] = "true"
        }), TestContext.Current.CancellationToken);
        Assert.Equal(ServiceSettingUpdateStatus.ValidationFailed, rejected.Status);
        Assert.Equal(1, store.ApplyCount);
        Assert.Null(store.Staged);
        Assert.Equal(1, store.Committed.Version);
        Assert.All(rejected.Errors, error => AssertSafe(error.ToString(), Endpoint, Authorization));
        Assert.True((await service.UpdateAsync(Command(1, new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.AllowNoAuthentication] = "true",
            [GrafanaLokiSettingDefinitions.Authorization] = null
        }), TestContext.Current.CancellationToken)).Succeeded);
        store.Commit();
        Assert.Null(GrafanaLokiSettingState.Classify(await LoadStoreAsync(store, key)).Authorization);
        Assert.Equal(Authorization, GrafanaLokiSettingState.Classify(old).Authorization);
        rejected = await service.UpdateAsync(Command(2, new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.AllowNoAuthentication] = null
        }), TestContext.Current.CancellationToken);
        Assert.Equal(ServiceSettingUpdateStatus.ValidationFailed, rejected.Status);
        Assert.Equal(2, store.ApplyCount);
        Assert.True((await service.UpdateAsync(Command(2, new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.AllowNoAuthentication] = null,
            [GrafanaLokiSettingDefinitions.Authorization] = Authorization
        }), TestContext.Current.CancellationToken)).Succeeded);
        store.Commit();
        Assert.DoesNotContain(GrafanaLokiSettingDefinitions.AllowNoAuthentication, store.Committed.Values.Keys);
        Assert.Equal(Authorization, GrafanaLokiSettingState.Classify(await LoadStoreAsync(store, key)).Authorization);
        var conflict = await service.UpdateAsync(Command(1, seed), TestContext.Current.CancellationToken);
        Assert.Equal(ServiceSettingUpdateStatus.VersionConflict, conflict.Status);
        Assert.Equal(3, store.ApplyCount);
        store.FailApply = true;
        Assert.Equal(ServiceSettingUpdateStatus.StorageFailed, (await service.UpdateAsync(Command(3, seed), TestContext.Current.CancellationToken)).Status);
        Assert.Null(store.Staged);
        Assert.Equal(3, store.Committed.Version);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("authentication")]
    [InlineData("batch")]
    public async Task Different_valid_snapshots_or_batch_configuration_conflict_at_start(string difference)
    {
        var first = await LoadAsync(Values(Endpoint, Authorization, false));
        var second = await LoadAsync(Values(difference == "endpoint" ? "https://other.example.test" : Endpoint,
            difference == "authentication" ? null : Authorization, difference == "authentication"));
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog();
        builder.Services.AddSingleton<ILokiHttpMessageHandlerFactory>(new CapturingFactory());
        builder.AddServiceMantleGrafanaLokiFromSettings(first);
        builder.AddServiceMantleGrafanaLokiFromSettings(second, options =>
        {
            if (difference == "batch") options.BatchSize = 2;
        });
        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<SerilogConfigurationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WellKnownGrafanaLokiErrorCodes.ConflictingRegistration, exception.ErrorCode);
        AssertSafe(exception.ToString(), Endpoint, Authorization);
    }

    private static void AssertSafe(string text, string? endpoint, string? authorization)
    {
        if (!string.IsNullOrEmpty(endpoint)) Assert.DoesNotContain(endpoint, text, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(authorization)) Assert.DoesNotContain(authorization, text, StringComparison.Ordinal);
        Assert.DoesNotContain("user:secret", text);
        Assert.DoesNotContain("secret=query", text);
    }

    private static ServiceSettingUpdateCommand Command(long version, IReadOnlyDictionary<string, string?> values) =>
        new(version, values, ManagementAuditOperator.System());

    private static async Task<ServiceSettingSnapshot> LoadAsync(Dictionary<string, string?> values)
    {
        var persisted = values.Select(pair => new PersistedServiceSettingValue(pair.Key, 1,
            Registry(false).TryGetDefinition(pair.Key, out var definition) ? definition!.ValueType : ServiceSettingValueType.String,
            pair.Key == GrafanaLokiSettingDefinitions.Authorization
                ? new SensitiveValueProtector(Identity, pair.Key).Protect(pair.Value!, "policy-root-key") : pair.Value!));
        var source = new MutableSource(new(Identity, 1, persisted));
        return await LoadSourceAsync(source, new CountingRootKey(false));
    }

    private static Task<ServiceSettingSnapshot> LoadStoreAsync(TransactionStore store, CountingRootKey key) =>
        LoadSourceAsync(new ServiceSettingStoreSnapshotSource(store, Registry(false)), key);

    private static async Task<ServiceSettingSnapshot> LoadSourceAsync(IServiceSettingSnapshotSource source, CountingRootKey key)
    {
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = new ServiceSettingSnapshotLoader(Identity, source, Registry(false), accessor, key);
        var loaded = await loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(loaded.Succeeded, string.Join(" ", loaded.Errors));
        Assert.True(accessor.TryGetCurrent(out var snapshot));
        return snapshot!;
    }

    private sealed class MutableSource(ServiceSettingSnapshotRead read) : IServiceSettingSnapshotSource
    {
        public ServiceSettingSnapshotRead Read { get; set; } = read;
        public ValueTask<ServiceSettingSnapshotRead> LoadAsync(ServiceId serviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read);
    }

    private sealed class CountingRootKey(bool throwOnUse) : IServiceSettingRootKeySource
    {
        public int Calls { get; private set; }
        public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (throwOnUse) throw new InvalidOperationException("No credential means no root key access");
            return ValueTask.FromResult("policy-root-key");
        }
    }

    // Deliberately separate staged transaction state from committed store reads. Only the test's
    // caller can Commit; the production update service receives no implicit commit capability.
    private sealed class TransactionStore : IServiceSettingUpdateTransaction, IServiceSettingStore
    {
        public ServiceSettingStoreSnapshot Committed { get; private set; } = new(Identity, 0, new Dictionary<string, string>(), null, null, false);
        public ServiceSettingStoreSnapshot? Staged { get; private set; }
        public IReadOnlyList<ManagementAuditEvent> Audits { get; private set; } = [];
        public int ApplyCount { get; private set; }
        public bool FailApply { get; set; }
        ValueTask<ServiceSettingStoreSnapshot> IServiceSettingUpdateTransaction.LoadAsync(ServiceId serviceId, CancellationToken cancellationToken) => ValueTask.FromResult(Staged ?? Committed);
        public ValueTask<ServiceSettingStoreSnapshot> LoadAsync(ServiceId serviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Committed);
        public ValueTask<ServiceSettingStoreUpdateResult> UpdateAsync(ServiceId serviceId, ServiceSettingStoreUpdate update, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Only the explicit transaction path is expected");
        public ValueTask<ServiceSettingUpdateResult> ApplyAsync(ServiceId serviceId, ServiceSettingStoreUpdate update, IReadOnlyList<ManagementAuditEvent> audits, CancellationToken cancellationToken)
        {
            ApplyCount++;
            if (FailApply) return ValueTask.FromResult(ServiceSettingUpdateResult.Failure(ServiceSettingUpdateStatus.StorageFailed));
            var values = new Dictionary<string, string>((Staged ?? Committed).Values);
            foreach (var (key, value) in update.Changes)
            {
                if (value is null) values.Remove(key);
                else values[key] = value;
            }
            Staged = new(Identity, update.ExpectedVersion + 1, values, DateTimeOffset.UtcNow, update.UpdatedBy, update.RestartRequired);
            Audits = audits;
            return ValueTask.FromResult(ServiceSettingUpdateResult.Applied(Staged.Version));
        }
        public void Commit()
        {
            Committed = Staged ?? throw new InvalidOperationException("No staged transaction");
            Staged = null;
        }
    }

    private sealed record Captured(string Path, string Authorization, string Body);
    private sealed class CapturingFactory : ILokiHttpMessageHandlerFactory
    {
        public int Creations { get; private set; }
        public TaskCompletionSource<Captured> Visible { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Captured> SecondVisible { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpMessageHandler Create() { Creations++; return new Handler(Visible, SecondVisible); }
        private sealed class Handler(TaskCompletionSource<Captured> visible, TaskCompletionSource<Captured> secondVisible) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (body.Contains("visible-policy-value", StringComparison.Ordinal))
                    (body.Contains("second-visible-policy-value", StringComparison.Ordinal) ? secondVisible : visible).TrySetResult(new(request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("Authorization", out var values) ? string.Join(" ", values) : "", body));
                return new(HttpStatusCode.NoContent);
            }
        }
    }

    private sealed class LocalServer(WebApplication application) : IAsyncDisposable
    {
        public TaskCompletionSource<Captured> Visible { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Captured> SecondVisible { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri Address => new(application.Urls.Single().TrimEnd('/') + "/");
        public static async Task<LocalServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var application = builder.Build();
            var server = new LocalServer(application);
            application.MapPost("/{**path}", async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                if (body.Contains("visible-policy-value", StringComparison.Ordinal))
                    (body.Contains("second-visible-policy-value", StringComparison.Ordinal) ? server.SecondVisible : server.Visible).TrySetResult(new(context.Request.Path, context.Request.Headers.Authorization.ToString(), body));
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });
            await application.StartAsync(TestContext.Current.CancellationToken);
            return server;
        }
        public async ValueTask DisposeAsync()
        {
            await application.StopAsync(CancellationToken.None);
            await application.DisposeAsync();
        }
    }
}
