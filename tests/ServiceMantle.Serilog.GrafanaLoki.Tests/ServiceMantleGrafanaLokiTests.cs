using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using global::Serilog.Debugging;
using ServiceMantle.Logging;
using ServiceMantle.Serilog;
using ServiceMantle.Serilog.GrafanaLoki;
using Xunit;

namespace ServiceMantle.Serilog.GrafanaLoki.Tests;

public sealed class ServiceMantleGrafanaLokiTests
{
    private const string ResolverName = "loki-primary";
    private const string AuthorizationHeader = "Bearer test-runtime-token";

    [Fact]
    public async Task Disabled_registration_does_not_require_base_pipeline_resolve_auth_or_create_transport()
    {
        var resolver = new RecordingResolver(AuthorizationHeader);
        var transport = new RecordingHandler();
        var handlerFactory = new StaticHandlerFactory(transport);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IServiceMantleLokiAuthorizationHeaderResolver>(resolver);
        builder.Services.Replace(ServiceDescriptor.Singleton<IServiceMantleLokiHttpMessageHandlerFactory>(
            handlerFactory));
        builder.AddServiceMantleGrafanaLoki();
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, resolver.InvocationCount);
        Assert.Equal(0, handlerFactory.InvocationCount);
        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public void Public_options_do_not_accept_or_render_authorization_values()
    {
        var options = new ServiceMantleGrafanaLokiOptions
        {
            Endpoint = new Uri("https://logs.example.test/prefix"),
            AuthorizationHeaderResolverName = ResolverName,
        };
        var properties = typeof(ServiceMantleGrafanaLokiOptions).GetProperties();

        Assert.DoesNotContain(properties, property =>
            property.Name is "Token" or "AuthorizationHeader" or "Password" or "Secret");
        Assert.DoesNotContain("logs.example.test", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ResolverName, options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_registration_requires_the_base_ServiceMantle_Serilog_pipeline()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IServiceMantleLokiAuthorizationHeaderResolver>(
            new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(Enable);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleSerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownServiceMantleGrafanaLokiErrorCodes.SerilogPipelineMissing, exception.ErrorCode);
    }

    public static TheoryData<Action<ServiceMantleGrafanaLokiOptions>, string> InvalidSettings => new()
    {
        { options => options.Endpoint = null, WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("relative", UriKind.Relative), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("http://logs.example.test"), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("https://user:pass@logs.example.test"), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("https://logs.example.test?token=value"), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("https://logs.example.test#secret"), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.AuthorizationHeaderResolverName = " ", WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidAuthorizationResolverName },
        { options => options.AuthorizationHeaderResolverName = "invalid/name", WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidAuthorizationResolverName },
        { options => options.BatchSize = 0, WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.BatchSize = 1_001, WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.QueueLimit = 99, WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.QueueLimit = 50_001, WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.FlushPeriod = TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.FlushPeriod = TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.ShutdownDrainTimeout = TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.ShutdownDrainTimeout = TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task Invalid_configuration_fails_safely_when_host_starts(
        Action<ServiceMantleGrafanaLokiOptions> mutate,
        string expectedErrorCode)
    {
        const string secret = "configuration-secret";
        var builder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            mutate(options);
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleSerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token=value", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 100, 1, 1)]
    [InlineData(1_000, 50_000, 30, 30)]
    public async Task Inclusive_numeric_boundaries_start_successfully(
        int batchSize,
        int queueLimit,
        int flushSeconds,
        int drainSeconds)
    {
        var builder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.BatchSize = batchSize;
            options.QueueLimit = queueLimit;
            options.FlushPeriod = TimeSpan.FromSeconds(flushSeconds);
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(drainSeconds);
        });
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Insecure_http_requires_explicit_loopback_test_option()
    {
        var rejectedBuilder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
        rejectedBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri("http://127.0.0.1:3100");
        });
        using (var rejected = rejectedBuilder.Build())
        {
            var exception = await Assert.ThrowsAsync<ServiceMantleSerilogConfigurationException>(() =>
                rejected.StartAsync(TestContext.Current.CancellationToken));
            Assert.Equal(WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidEndpoint, exception.ErrorCode);
        }

        var acceptedBuilder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
        acceptedBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri("http://localhost:3100/prefix");
            options.AllowInsecureLoopbackForTesting = true;
        });
        using var accepted = acceptedBuilder.Build();
        await accepted.StartAsync(TestContext.Current.CancellationToken);
        await accepted.StopAsync(TestContext.Current.CancellationToken);
    }

    public static TheoryData<IServiceMantleLokiAuthorizationHeaderResolver?, string> InvalidResolvers => new()
    {
        { null, WellKnownServiceMantleGrafanaLokiErrorCodes.AuthorizationResolverMissing },
        { new RecordingResolver(null), WellKnownServiceMantleGrafanaLokiErrorCodes.AuthorizationValueInvalid },
        { new RecordingResolver("bad\r\nheader"), WellKnownServiceMantleGrafanaLokiErrorCodes.AuthorizationValueInvalid },
        { new ThrowingResolver("resolver-token-secret"), WellKnownServiceMantleGrafanaLokiErrorCodes.AuthorizationResolutionFailed },
    };

    [Theory]
    [MemberData(nameof(InvalidResolvers))]
    public async Task Missing_or_invalid_authorization_fails_safely_at_startup(
        IServiceMantleLokiAuthorizationHeaderResolver? resolver,
        string expectedErrorCode)
    {
        var builder = CreateBuilder(new RecordingHandler(), resolver);
        builder.AddServiceMantleGrafanaLoki(Enable);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleSerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        Assert.DoesNotContain("resolver-token-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AuthorizationHeader, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Equivalent_duplicates_are_idempotent_and_different_settings_conflict()
    {
        var duplicateBuilder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
        duplicateBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.AuthorizationHeaderResolverName = $" {ResolverName} ";
        });
        duplicateBuilder.AddServiceMantleGrafanaLoki(Enable);
        using (var duplicate = duplicateBuilder.Build())
        {
            await duplicate.StartAsync(TestContext.Current.CancellationToken);
            Assert.Single(duplicate.Services.GetServices<ServiceMantleGrafanaLokiDiagnostics>());
            await duplicate.StopAsync(TestContext.Current.CancellationToken);
        }

        var conflictBuilder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
        conflictBuilder.AddServiceMantleGrafanaLoki(Enable);
        conflictBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.BatchSize = 5;
        });
        using var conflict = conflictBuilder.Build();
        var conflictException = await Assert.ThrowsAsync<ServiceMantleSerilogConfigurationException>(() =>
            conflict.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WellKnownServiceMantleGrafanaLokiErrorCodes.ConflictingRegistration, conflictException.ErrorCode);
    }

    [Fact]
    public async Task Local_http_server_receives_authorized_batched_sanitized_events_at_fixed_push_path()
    {
        await using var server = await LocalLokiServer.StartAsync(TestContext.Current.CancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        var resolver = new RecordingResolver(AuthorizationHeader);
        builder.Services.AddSingleton<IServiceMantleLokiAuthorizationHeaderResolver>(resolver);
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri(server.BaseAddress, "gateway");
            options.AllowInsecureLoopbackForTesting = true;
            options.BatchSize = 2;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<ServiceMantleGrafanaLokiTests>>();

        logger.LogInformation("first {Name} {Password}", "visible-one", "structured-secret-one");
        logger.LogInformation("second {Name} {Password}", "visible-two", "structured-secret-two");
        await server.ReceivedApplicationEvents.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var requests = server.Requests.ToArray();
        var bodies = string.Join(string.Empty, requests.Select(request => request.Body));

        Assert.All(requests, request =>
        {
            Assert.Equal("/gateway/loki/api/v1/push", request.Path);
            Assert.Equal(AuthorizationHeader, request.Authorization);
            Assert.InRange(CountLokiValues(request.Body), 1, 2);
        });
        Assert.Contains("visible-one", bodies, StringComparison.Ordinal);
        Assert.Contains("visible-two", bodies, StringComparison.Ordinal);
        Assert.Contains(StructuredLogSanitizer.RedactedValue, bodies, StringComparison.Ordinal);
        Assert.DoesNotContain("structured-secret-one", bodies, StringComparison.Ordinal);
        Assert.DoesNotContain("structured-secret-two", bodies, StringComparison.Ordinal);
        Assert.Equal(1, resolver.InvocationCount);
        Assert.Equal(ResolverName, resolver.LastName);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Transport_failure_is_safely_classified_without_exception_or_event_content()
    {
        const string transportSecret = "transport-exception-secret";
        const string eventSecret = "transport-event-secret";
        var builder = CreateBuilder(
            new ThrowingHandler(transportSecret),
            new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(1);
        });
        using var selfLog = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        SelfLog.Enable(selfLog);
        try
        {
            using var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            host.Services.GetRequiredService<ILogger<ServiceMantleGrafanaLokiTests>>()
                .LogError("transport failure {Password}", eventSecret);
            var diagnostics = host.Services.GetRequiredService<ServiceMantleGrafanaLokiDiagnostics>();
            await WaitUntilAsync(() => diagnostics.FailedBatchCount > 0, TestContext.Current.CancellationToken);

            Assert.Equal(
                WellKnownServiceMantleGrafanaLokiErrorCodes.TransportFailed,
                diagnostics.LastErrorCode);
            var diagnosticText = diagnostics + selfLog.ToString();
            Assert.DoesNotContain(transportSecret, diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain(eventSecret, diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthorizationHeader, diagnosticText, StringComparison.Ordinal);

            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            SelfLog.Disable();
        }
    }

    [Fact]
    public async Task Full_queue_uses_upstream_bounded_drop_semantics()
    {
        const int emitted = 250;
        var handler = new BlockingHandler(HttpStatusCode.NoContent);
        var builder = CreateBuilder(handler, new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.BatchSize = 1;
            options.QueueLimit = 100;
            options.FlushPeriod = TimeSpan.FromSeconds(30);
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<ServiceMantleGrafanaLokiTests>>();

        logger.LogInformation("first queued event");
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        for (var index = 1; index < emitted; index++)
        {
            logger.LogInformation("queued event {Index}", index);
        }

        handler.Release.TrySetResult();
        await host.StopAsync(TestContext.Current.CancellationToken);
        var diagnostics = host.Services.GetRequiredService<ServiceMantleGrafanaLokiDiagnostics>();

        Assert.InRange(handler.RequestCount, 1, 102);
        Assert.True(handler.RequestCount < emitted);
        Assert.True(diagnostics.DroppedEventCount > 0);
        Assert.DoesNotContain("queued event", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_response_and_diagnostics_exclude_remote_auth_and_event_secrets()
    {
        const string responseSecret = "remote-response-secret";
        const string eventSecret = "event-property-secret";
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, responseSecret);
        var builder = CreateBuilder(handler, new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri("https://logs.example.test/sensitive-path");
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(1);
        });
        using var selfLog = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        SelfLog.Enable(selfLog);
        try
        {
            using var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            host.Services.GetRequiredService<ILogger<ServiceMantleGrafanaLokiTests>>()
                .LogError("failed event {Password}", eventSecret);
            var diagnostics = host.Services.GetRequiredService<ServiceMantleGrafanaLokiDiagnostics>();
            await WaitUntilAsync(() => diagnostics.FailedBatchCount > 0, TestContext.Current.CancellationToken);

            await host.StopAsync(TestContext.Current.CancellationToken);
            var diagnosticText = diagnostics + selfLog.ToString();
            Assert.DoesNotContain(responseSecret, diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain(eventSecret, diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthorizationHeader, diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive-path", diagnosticText, StringComparison.Ordinal);
            Assert.Contains(
                diagnostics.LastErrorCode!,
                new[]
                {
                    WellKnownServiceMantleGrafanaLokiErrorCodes.RemoteResponseFailed,
                    WellKnownServiceMantleGrafanaLokiErrorCodes.ShutdownDrainTimedOut,
                });
        }
        finally
        {
            SelfLog.Disable();
        }
    }

    [Fact]
    public async Task Shutdown_drain_completes_when_transport_releases_before_timeout()
    {
        var handler = new BlockingHandler(HttpStatusCode.NoContent);
        var builder = CreateBuilder(handler, new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.BatchSize = 1;
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(2);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ILogger<ServiceMantleGrafanaLokiTests>>()
            .LogInformation("drain event");
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var stopTask = host.StopAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        handler.Release.TrySetResult();
        await stopTask;

        var diagnostics = host.Services.GetRequiredService<ServiceMantleGrafanaLokiDiagnostics>();
        Assert.Equal(0, diagnostics.DrainTimeoutCount);
        Assert.True(handler.Completed);
    }

    [Fact]
    public async Task Shutdown_timeout_and_caller_cancellation_abort_transport_without_leaking_values()
    {
        var timeoutHandler = new BlockingHandler(HttpStatusCode.NoContent);
        var timeoutBuilder = CreateBuilder(timeoutHandler, new RecordingResolver(AuthorizationHeader));
        timeoutBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.BatchSize = 1;
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(1);
        });
        using (var timeoutHost = timeoutBuilder.Build())
        {
            await timeoutHost.StartAsync(TestContext.Current.CancellationToken);
            timeoutHost.Services.GetRequiredService<ILogger<ServiceMantleGrafanaLokiTests>>()
                .LogInformation("timeout event");
            await timeoutHandler.Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            var stopwatch = Stopwatch.StartNew();

            await timeoutHost.StopAsync(TestContext.Current.CancellationToken);

            stopwatch.Stop();
            var diagnostics = timeoutHost.Services.GetRequiredService<ServiceMantleGrafanaLokiDiagnostics>();
            Assert.Equal(1, diagnostics.DrainTimeoutCount);
            Assert.True(timeoutHandler.Cancelled);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        }

        var cancelHandler = new BlockingHandler(HttpStatusCode.NoContent);
        var cancelBuilder = CreateBuilder(cancelHandler, new RecordingResolver(AuthorizationHeader));
        cancelBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.BatchSize = 1;
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(30);
        });
        using var cancelHost = cancelBuilder.Build();
        await cancelHost.StartAsync(TestContext.Current.CancellationToken);
        cancelHost.Services.GetRequiredService<ILogger<ServiceMantleGrafanaLokiTests>>()
            .LogInformation("cancel event");
        await cancelHandler.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var lifecycle = cancelHost.Services.GetServices<IHostedService>()
            .OfType<ServiceMantleGrafanaLokiLifecycle>()
            .Single();

        await lifecycle.StopAsync(cancellation.Token);

        var cancelDiagnostics = cancelHost.Services.GetRequiredService<ServiceMantleGrafanaLokiDiagnostics>();
        Assert.Equal(1, cancelDiagnostics.DrainCancellationCount);
        Assert.True(cancelHandler.Cancelled);
        await cancelHost.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_shutdown_attempt_is_not_repeated_when_the_base_pipeline_is_disposed(
        bool cancelFirstAttempt)
    {
        var handler = new IgnoringCancellationHandler(HttpStatusCode.NoContent);
        var builder = CreateBuilder(handler, new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.BatchSize = 1;
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ILogger<ServiceMantleGrafanaLokiTests>>()
            .LogInformation("ignored cancellation event");
        await handler.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        var lifecycle = host.Services.GetServices<IHostedService>()
            .OfType<ServiceMantleGrafanaLokiLifecycle>()
            .Single();
        using var cancellation = new CancellationTokenSource();
        if (cancelFirstAttempt)
        {
            cancellation.Cancel();
        }

        await lifecycle.StopAsync(cancelFirstAttempt ? cancellation.Token : CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();
        handler.Release.TrySetResult();
        await handler.Completed.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        var diagnostics = host.Services.GetRequiredService<ServiceMantleGrafanaLokiDiagnostics>();
        Assert.Equal(cancelFirstAttempt ? 1 : 0, diagnostics.DrainCancellationCount);
        Assert.Equal(cancelFirstAttempt ? 0 : 1, diagnostics.DrainTimeoutCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(750),
            $"Base pipeline disposal repeated the shutdown window and took {stopwatch.Elapsed}.");
    }

    private static HostApplicationBuilder CreateBuilder(
        HttpMessageHandler handler,
        IServiceMantleLokiAuthorizationHeaderResolver? resolver)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        if (resolver is not null)
        {
            builder.Services.AddSingleton(resolver);
        }

        builder.Services.Replace(ServiceDescriptor.Singleton<IServiceMantleLokiHttpMessageHandlerFactory>(
            new StaticHandlerFactory(handler)));
        return builder;
    }

    private static void Enable(ServiceMantleGrafanaLokiOptions options)
    {
        options.Enabled = true;
        options.Endpoint = new Uri("https://logs.example.test");
        options.AuthorizationHeaderResolverName = ResolverName;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(predicate());
    }

    private static int CountLokiValues(string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.GetProperty("streams")
            .EnumerateArray()
            .Sum(stream => stream.GetProperty("values").GetArrayLength());
    }

    private sealed class RecordingResolver(string? value) : IServiceMantleLokiAuthorizationHeaderResolver
    {
        private int invocationCount;
        private string? lastName;

        internal int InvocationCount => Volatile.Read(ref invocationCount);

        internal string? LastName => Volatile.Read(ref lastName);

        public string? ResolveAuthorizationHeader(string name)
        {
            Interlocked.Increment(ref invocationCount);
            Volatile.Write(ref lastName, name);
            return value;
        }
    }

    private sealed class ThrowingResolver(string secret) : IServiceMantleLokiAuthorizationHeaderResolver
    {
        public string? ResolveAuthorizationHeader(string name) => throw new InvalidOperationException(secret);
    }

    private sealed class StaticHandlerFactory(HttpMessageHandler handler)
        : IServiceMantleLokiHttpMessageHandlerFactory
    {
        private int invocationCount;

        internal int InvocationCount => Volatile.Read(ref invocationCount);

        public HttpMessageHandler Create()
        {
            Interlocked.Increment(ref invocationCount);
            return handler;
        }
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode = HttpStatusCode.NoContent,
        string responseBody = "") : HttpMessageHandler
    {
        private int requestCount;

        internal int RequestCount => Volatile.Read(ref requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            _ = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            };
        }
    }

    private sealed class ThrowingHandler(string secret) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(secret);
    }

    private sealed class BlockingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        private int requestCount;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int RequestCount => Volatile.Read(ref requestCount);

        internal bool Cancelled { get; private set; }

        internal bool Completed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                Completed = true;
                return new HttpResponseMessage(statusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled = true;
                throw;
            }
        }
    }

    private sealed class IgnoringCancellationHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            Completed.TrySetResult();
            return new HttpResponseMessage(statusCode);
        }
    }

    private sealed class LocalLokiServer(WebApplication application) : IAsyncDisposable
    {
        internal ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        internal TaskCompletionSource ReceivedApplicationEvents { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Uri BaseAddress => new(application.Urls.Single().TrimEnd('/') + "/");

        internal static async Task<LocalLokiServer> StartAsync(CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var application = builder.Build();
            var server = new LocalLokiServer(application);
            application.MapPost("/{**path}", async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                server.Requests.Enqueue(new(
                    context.Request.Path,
                    context.Request.Headers.Authorization.ToString(),
                    body));
                if (body.Contains("visible-two", StringComparison.Ordinal))
                {
                    server.ReceivedApplicationEvents.TrySetResult();
                }
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });
            await application.StartAsync(cancellationToken);
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync(CancellationToken.None);
            await application.DisposeAsync();
        }
    }

    private sealed record CapturedRequest(string Path, string Authorization, string Body);
}
