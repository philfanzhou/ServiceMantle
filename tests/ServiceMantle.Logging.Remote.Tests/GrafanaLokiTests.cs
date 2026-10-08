using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using global::Serilog.Debugging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Logging;
using ServiceMantle.Logging.Pipeline;
using ServiceMantle.Logging.Remote;
using Xunit;

namespace ServiceMantle.Logging.Remote.Tests;

public sealed class GrafanaLokiTests
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
        builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(resolver);
        builder.Services.Replace(ServiceDescriptor.Singleton<ILokiHttpMessageHandlerFactory>(
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
        var options = new GrafanaLokiOptions
        {
            Endpoint = new Uri("https://logs.example.test/prefix"),
            AuthorizationHeaderResolverName = ResolverName,
            Labels = new Dictionary<string, string> { ["service"] = "Ruoyu.Admin" },
        };
        var properties = typeof(GrafanaLokiOptions).GetProperties();

        Assert.DoesNotContain(properties, property =>
            property.Name is "Token" or "AuthorizationHeader" or "Password" or "Secret");
        Assert.DoesNotContain("logs.example.test", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ResolverName, options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Ruoyu.Admin", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_registration_requires_the_base_ServiceMantle_Serilog_pipeline()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(
            new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(Enable);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<SerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownGrafanaLokiErrorCodes.SerilogPipelineMissing, exception.ErrorCode);
    }

    public static TheoryData<Action<GrafanaLokiOptions>, string> InvalidSettings => new()
    {
        { options => options.Endpoint = null, WellKnownGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("relative", UriKind.Relative), WellKnownGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("ftp://logs.example.test"), WellKnownGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("https://user:pass@logs.example.test"), WellKnownGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("https://logs.example.test?token=value"), WellKnownGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("https://logs.example.test#secret"), WellKnownGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("http://user:pass@ruoyu-loki:3100"), WellKnownGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("http://ruoyu-loki:3100?token=value"), WellKnownGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.Endpoint = new Uri("http://ruoyu-loki:3100#secret"), WellKnownGrafanaLokiErrorCodes.InvalidEndpoint },
        { options => options.AuthorizationHeaderResolverName = " ", WellKnownGrafanaLokiErrorCodes.InvalidAuthorizationResolverName },
        { options => options.AuthorizationHeaderResolverName = "invalid/name", WellKnownGrafanaLokiErrorCodes.InvalidAuthorizationResolverName },
        { options => options.Labels = new Dictionary<string, string> { ["level"] = "fixed-label-secret" }, WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.Labels = new Dictionary<string, string> { ["service-name"] = "fixed-label-secret" }, WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.Labels = new Dictionary<string, string> { [""] = "fixed-label-secret" }, WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.Labels = new Dictionary<string, string> { ["1service"] = "fixed-label-secret" }, WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.Labels = new Dictionary<string, string> { [new string('k', GrafanaLokiDefaults.MaxLabelKeyLength + 1)] = "fixed-label-secret" }, WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.Labels = new Dictionary<string, string> { ["service"] = "" }, WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.Labels = new Dictionary<string, string> { ["service"] = new string('v', GrafanaLokiDefaults.MaxLabelValueLength + 1) }, WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.Labels = new Dictionary<string, string> { ["service"] = "fixed\tlabel-secret" }, WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.Labels = new Dictionary<string, string>(), WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.Labels = Enumerable.Range(0, GrafanaLokiDefaults.MaxLabelCount + 1)
            .ToDictionary(index => $"label{index}", index => "fixed-label-secret"), WellKnownGrafanaLokiErrorCodes.InvalidLabels },
        { options => options.BatchSize = 0, WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.BatchSize = 1_001, WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.QueueLimit = 99, WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.QueueLimit = 50_001, WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.FlushPeriod = TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1), WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.FlushPeriod = TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1), WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.ShutdownDrainTimeout = TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1), WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting },
        { options => options.ShutdownDrainTimeout = TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1), WellKnownGrafanaLokiErrorCodes.InvalidBoundedSetting },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task Invalid_configuration_fails_safely_when_host_starts(
        Action<GrafanaLokiOptions> mutate,
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

        var exception = await Assert.ThrowsAsync<SerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token=value", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixed-label-secret", exception.ToString(), StringComparison.Ordinal);
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
    public async Task Http_endpoints_start_without_any_transport_switch()
    {
        foreach (var endpoint in new[] { "http://127.0.0.1:3100", "http://localhost:3100/prefix", "http://ruoyu-loki:3100" })
        {
            var builder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
            builder.AddServiceMantleGrafanaLoki(options =>
            {
                Enable(options);
                options.Endpoint = new Uri(endpoint);
            });
            using var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Http_delivery_to_a_non_loopback_host_needs_no_switch()
    {
        var handler = new RecordingHandler();
        var builder = CreateBuilder(handler, new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri("http://ruoyu-loki:3100");
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
        });
        using var accepted = builder.Build();
        await accepted.StartAsync(TestContext.Current.CancellationToken);
        accepted.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("container network event");

        await WaitUntilAsync(() => handler.RequestCount > 0, TestContext.Current.CancellationToken);
        var requestUri = handler.RequestUris.First();
        Assert.Equal("http", requestUri.Scheme);
        Assert.Equal("ruoyu-loki", requestUri.Host);
        Assert.Equal(3100, requestUri.Port);
        Assert.Equal("/loki/api/v1/push", requestUri.AbsolutePath);
        Assert.All(handler.RequestUris, uri =>
            Assert.Equal(new Uri("http://ruoyu-loki:3100/loki/api/v1/push"), uri));

        await accepted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Http_delivery_delivers_authorized_events_to_a_local_http_server()
    {
        await using var server = await LocalLokiServer.StartAsync(TestContext.Current.CancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        var resolver = new RecordingResolver(AuthorizationHeader);
        builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(resolver);
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri(server.BaseAddress, "gateway");
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("insecure delivery event");

        await WaitUntilAsync(
            () => server.Requests.Any(request =>
                request.Body.Contains("insecure delivery event", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken);

        Assert.All(server.Requests, request =>
        {
            Assert.Equal("/gateway/loki/api/v1/push", request.Path);
            Assert.Equal(AuthorizationHeader, request.Authorization);
            Assert.InRange(CountLokiValues(request.Body), 1, 1);
        });
        Assert.Equal(1, resolver.InvocationCount);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Configured_fixed_labels_reach_the_stream_labels_alongside_the_sink_owned_level_label()
    {
        await using var server = await LocalLokiServer.StartAsync(TestContext.Current.CancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(
            new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri(server.BaseAddress, "gateway");
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
            options.Labels = new Dictionary<string, string> { ["service"] = "Ruoyu.Admin" };
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("labeled delivery event");

        await WaitUntilAsync(
            () => server.Requests.Any(request =>
                request.Body.Contains("labeled delivery event", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken);
        var bodies = server.Requests
            .Where(request => request.Body.Contains("labeled delivery event", StringComparison.Ordinal))
            .Select(request => request.Body)
            .ToArray();

        Assert.NotEmpty(bodies);
        Assert.All(bodies, body =>
        {
            var streamLabels = ParseStreamLabels(body);
            Assert.NotEmpty(streamLabels);
            Assert.All(streamLabels, labels =>
            {
                Assert.Equal("Ruoyu.Admin", labels["service"]);
                Assert.True(labels.ContainsKey("level"));
            });
        });

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unconfigured_labels_keep_the_stream_labels_at_the_sink_owned_level_label()
    {
        await using var server = await LocalLokiServer.StartAsync(TestContext.Current.CancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(
            new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri(server.BaseAddress, "gateway");
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("unlabeled delivery event");

        await WaitUntilAsync(
            () => server.Requests.Any(request =>
                request.Body.Contains("unlabeled delivery event", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken);
        var bodies = server.Requests
            .Where(request => request.Body.Contains("unlabeled delivery event", StringComparison.Ordinal))
            .Select(request => request.Body)
            .ToArray();

        Assert.NotEmpty(bodies);
        Assert.All(bodies, body =>
        {
            var streamLabels = ParseStreamLabels(body);
            Assert.NotEmpty(streamLabels);
            Assert.All(streamLabels, labels =>
                Assert.Equal(new[] { "level" }, labels.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray()));
        });

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Valid_label_boundaries_start_successfully()
    {
        var builder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Labels = Enumerable.Range(0, GrafanaLokiDefaults.MaxLabelCount)
                .ToDictionary(
                    index => index == 0
                        ? new string('k', GrafanaLokiDefaults.MaxLabelKeyLength)
                        : $"label{index}",
                    index => new string('v', GrafanaLokiDefaults.MaxLabelValueLength));
        });
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Equivalent_label_sets_are_idempotent_across_order_and_different_labels_conflict()
    {
        var duplicateBuilder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
        duplicateBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Labels = new Dictionary<string, string>
            {
                ["service"] = "Ruoyu.Admin",
                ["environment"] = "production",
            };
        });
        duplicateBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Labels = new Dictionary<string, string>
            {
                ["environment"] = "production",
                ["service"] = "Ruoyu.Admin",
            };
        });
        using (var duplicate = duplicateBuilder.Build())
        {
            await duplicate.StartAsync(TestContext.Current.CancellationToken);
            Assert.Single(duplicate.Services.GetServices<RemoteLogDeliveryDiagnostics>());
            await duplicate.StopAsync(TestContext.Current.CancellationToken);
        }

        var conflictBuilder = CreateBuilder(new RecordingHandler(), new RecordingResolver(AuthorizationHeader));
        conflictBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Labels = new Dictionary<string, string> { ["service"] = "Ruoyu.Admin" };
        });
        conflictBuilder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Labels = new Dictionary<string, string> { ["service"] = "Ruoyu.Other" };
        });
        using var conflict = conflictBuilder.Build();
        var conflictException = await Assert.ThrowsAsync<SerilogConfigurationException>(() =>
            conflict.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WellKnownGrafanaLokiErrorCodes.ConflictingRegistration, conflictException.ErrorCode);
    }

    [Fact]
    public void Reserved_label_keys_pin_exactly_the_labels_owned_by_the_sink_wiring()
    {
        // GrafanaLokiSinkFactory wires handleLogLevelAsLabel: true (the "level" label) and keeps
        // propertiesAsLabels, traceIdMode, and spanIdMode off. Widening this set requires new
        // sink-owned labels; shrinking it would let configuration collide with the level label.
        Assert.Equal(
            new[] { "level" },
            GrafanaLokiDefaults.ReservedLabelKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Category_level_overrides_filter_events_before_the_loki_pipeline()
    {
        await using var server = await LocalLokiServer.StartAsync(TestContext.Current.CancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options =>
        {
            options.FlushTimeout = TimeSpan.FromSeconds(5);
            options.MinimumLevelOverrides = new Dictionary<string, LogLevel>
            {
                ["Microsoft.AspNetCore"] = LogLevel.Warning,
            };
        });
        builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(
            new RecordingResolver(AuthorizationHeader));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri(server.BaseAddress, "gateway");
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var categoryLogger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.AspNetCore.Hosting");
        categoryLogger.LogInformation("overridden category information event");
        categoryLogger.LogWarning("overridden category warning event");

        await WaitUntilAsync(
            () => server.Requests.Any(request =>
                request.Body.Contains("overridden category warning event", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken);
        var bodies = server.Requests.Select(request => request.Body).ToArray();

        Assert.DoesNotContain(bodies, body =>
            body.Contains("overridden category information event", StringComparison.Ordinal));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unset_resolver_name_disables_authorization_without_a_registered_resolver()
    {
        var handler = new RecordingHandler();
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        builder.Services.Replace(ServiceDescriptor.Singleton<ILokiHttpMessageHandlerFactory>(
            new StaticHandlerFactory(handler)));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            options.Enabled = true;
            options.Endpoint = new Uri("https://logs.example.test");
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("unauthenticated https event");

        await WaitUntilAsync(() => handler.RequestCount > 0, TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unset_resolver_name_sends_requests_without_an_authorization_header()
    {
        await using var server = await LocalLokiServer.StartAsync(TestContext.Current.CancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            options.Enabled = true;
            options.Endpoint = new Uri(server.BaseAddress, "gateway");
            options.BatchSize = 1;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("unauthenticated http event");

        await WaitUntilAsync(
            () => server.Requests.Any(request =>
                request.Body.Contains("unauthenticated http event", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken);

        Assert.All(server.Requests, request =>
        {
            Assert.Equal("/gateway/loki/api/v1/push", request.Path);
            Assert.Equal(string.Empty, request.Authorization);
        });
        Assert.Contains(server.Requests, request =>
            request.Body.Contains("unauthenticated http event", StringComparison.Ordinal));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    public static TheoryData<IRemoteLogAuthorizationResolver?, string> InvalidResolvers => new()
    {
        { null, WellKnownGrafanaLokiErrorCodes.AuthorizationResolverMissing },
        { new RecordingResolver(null), WellKnownGrafanaLokiErrorCodes.AuthorizationValueInvalid },
        { new RecordingResolver("bad\r\nheader"), WellKnownGrafanaLokiErrorCodes.AuthorizationValueInvalid },
        { new ThrowingResolver("resolver-token-secret"), WellKnownGrafanaLokiErrorCodes.AuthorizationResolutionFailed },
    };

    [Theory]
    [MemberData(nameof(InvalidResolvers))]
    public async Task Missing_or_invalid_authorization_fails_safely_at_startup(
        IRemoteLogAuthorizationResolver? resolver,
        string expectedErrorCode)
    {
        var builder = CreateBuilder(new RecordingHandler(), resolver);
        builder.AddServiceMantleGrafanaLoki(Enable);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<SerilogConfigurationException>(() =>
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
            Assert.Single(duplicate.Services.GetServices<RemoteLogDeliveryDiagnostics>());
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
        var conflictException = await Assert.ThrowsAsync<SerilogConfigurationException>(() =>
            conflict.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WellKnownGrafanaLokiErrorCodes.ConflictingRegistration, conflictException.ErrorCode);
    }

    [Fact]
    public async Task Local_http_server_receives_authorized_batched_sanitized_events_at_fixed_push_path()
    {
        await using var server = await LocalLokiServer.StartAsync(TestContext.Current.CancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        var resolver = new RecordingResolver(AuthorizationHeader);
        builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(resolver);
        builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.Endpoint = new Uri(server.BaseAddress, "gateway");
            options.BatchSize = 2;
            options.FlushPeriod = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>();

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
            host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
                .LogError("transport failure {Password}", eventSecret);
            var diagnostics = host.Services.GetRequiredService<RemoteLogDeliveryDiagnostics>();
            await WaitUntilAsync(() => diagnostics.FailedBatchCount > 0, TestContext.Current.CancellationToken);

            Assert.Equal(
                WellKnownGrafanaLokiErrorCodes.TransportFailed,
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
        var logger = host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>();

        logger.LogInformation("first queued event");
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        for (var index = 1; index < emitted; index++)
        {
            logger.LogInformation("queued event {Index}", index);
        }

        handler.Release.TrySetResult();
        await host.StopAsync(TestContext.Current.CancellationToken);
        var diagnostics = host.Services.GetRequiredService<RemoteLogDeliveryDiagnostics>();

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
            host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
                .LogError("failed event {Password}", eventSecret);
            var diagnostics = host.Services.GetRequiredService<RemoteLogDeliveryDiagnostics>();
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
                    WellKnownGrafanaLokiErrorCodes.RemoteResponseFailed,
                    WellKnownGrafanaLokiErrorCodes.ShutdownDrainTimedOut,
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
        host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("drain event");
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var stopTask = host.StopAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        handler.Release.TrySetResult();
        await stopTask;

        var diagnostics = host.Services.GetRequiredService<RemoteLogDeliveryDiagnostics>();
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
            timeoutHost.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
                .LogInformation("timeout event");
            await timeoutHandler.Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            var stopwatch = Stopwatch.StartNew();

            await timeoutHost.StopAsync(TestContext.Current.CancellationToken);

            stopwatch.Stop();
            var diagnostics = timeoutHost.Services.GetRequiredService<RemoteLogDeliveryDiagnostics>();
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
        cancelHost.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("cancel event");
        await cancelHandler.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var lifecycle = Assert.IsType<GrafanaLokiLifecycle>(Assert.Single(
            cancelHost.Services.GetServices<IHostedService>(),
            service => service is GrafanaLokiLifecycle));

        await lifecycle.StopAsync(cancellation.Token);

        var cancelDiagnostics = cancelHost.Services.GetRequiredService<RemoteLogDeliveryDiagnostics>();
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
        host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("ignored cancellation event");
        await handler.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        var lifecycle = Assert.IsType<GrafanaLokiLifecycle>(Assert.Single(
            host.Services.GetServices<IHostedService>(),
            service => service is GrafanaLokiLifecycle));
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

        var diagnostics = host.Services.GetRequiredService<RemoteLogDeliveryDiagnostics>();
        Assert.Equal(cancelFirstAttempt ? 1 : 0, diagnostics.DrainCancellationCount);
        Assert.Equal(cancelFirstAttempt ? 0 : 1, diagnostics.DrainTimeoutCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(750),
            $"Base pipeline disposal repeated the shutdown window and took {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_shutdown_cancellation_preempts_base_flush_for_both_registration_orders(
        bool lokiRegisteredFirst)
    {
        var handler = new IgnoringCancellationHandler(HttpStatusCode.NoContent);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IRemoteLogAuthorizationResolver>(
            new RecordingResolver(AuthorizationHeader));
        builder.Services.Replace(ServiceDescriptor.Singleton<ILokiHttpMessageHandlerFactory>(
            new StaticHandlerFactory(handler)));

        void RegisterLoki() => builder.AddServiceMantleGrafanaLoki(options =>
        {
            Enable(options);
            options.BatchSize = 1;
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(30);
        });
        void RegisterSerilog() => builder.AddServiceMantleSerilog(options =>
            options.FlushTimeout = TimeSpan.FromSeconds(5));

        if (lokiRegisteredFirst)
        {
            RegisterLoki();
            RegisterSerilog();
        }
        else
        {
            RegisterSerilog();
            RegisterLoki();
        }

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ILogger<GrafanaLokiTests>>()
            .LogInformation("registration-order cancellation event");
        await handler.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        var stopwatch = Stopwatch.StartNew();

        var stopTask = host.StopAsync(cancellation.Token);
        var completed = await Task.WhenAny(
            stopTask,
            Task.Delay(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken));
        handler.Release.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await handler.Completed.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        stopwatch.Stop();

        var diagnostics = host.Services.GetRequiredService<RemoteLogDeliveryDiagnostics>();
        Assert.Same(stopTask, completed);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(750),
            $"Host shutdown took {stopwatch.Elapsed} before cancellation reached the Loki drain.");
        Assert.Equal(1, diagnostics.DrainCancellationCount);
        Assert.Equal(0, diagnostics.DrainTimeoutCount);
    }

    private static HostApplicationBuilder CreateBuilder(
        HttpMessageHandler handler,
        IRemoteLogAuthorizationResolver? resolver)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        if (resolver is not null)
        {
            builder.Services.AddSingleton(resolver);
        }

        builder.Services.Replace(ServiceDescriptor.Singleton<ILokiHttpMessageHandlerFactory>(
            new StaticHandlerFactory(handler)));
        return builder;
    }

    private static void Enable(GrafanaLokiOptions options)
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

    private static List<Dictionary<string, string?>> ParseStreamLabels(string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.GetProperty("streams")
            .EnumerateArray()
            .Select(stream => stream.GetProperty("stream").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()))
            .ToList();
    }

    private sealed class RecordingResolver(string? value) : IRemoteLogAuthorizationResolver
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

    private sealed class ThrowingResolver(string secret) : IRemoteLogAuthorizationResolver
    {
        public string? ResolveAuthorizationHeader(string name) => throw new InvalidOperationException(secret);
    }

    private sealed class StaticHandlerFactory(HttpMessageHandler handler)
        : ILokiHttpMessageHandlerFactory
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

        internal ConcurrentQueue<Uri> RequestUris { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            RequestUris.Enqueue(request.RequestUri!);
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
