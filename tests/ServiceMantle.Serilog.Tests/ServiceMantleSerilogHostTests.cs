using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using global::Serilog;
using Serilog.Core;
using Serilog.Events;
using ServiceMantle;
using ServiceMantle.Logging;
using ServiceMantle.Serilog;
using Xunit;

namespace ServiceMantle.Serilog.Tests;

[CollectionDefinition("ServiceMantle Serilog Console", DisableParallelization = true)]
public sealed class ServiceMantleSerilogConsoleCollection;

[Collection("ServiceMantle Serilog Console")]
public sealed class ServiceMantleSerilogHostTests
{
    [Fact]
    public async Task Default_host_starts_and_console_receives_only_sanitized_structured_properties()
    {
        const string secret = "console-structured-secret";
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.AddServiceMantleSerilog();
            using var host = builder.Build();

            await host.StartAsync(TestContext.Current.CancellationToken);
            host.Services.GetRequiredService<ILogger<ServiceMantleSerilogHostTests>>()
                .LogInformation("Handled login for {UserName} with {Password}", "alice", secret);
            await host.StopAsync(TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("alice", text, StringComparison.Ordinal);
            Assert.Contains(StructuredLogSanitizer.RedactedValue, text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void Public_options_have_no_sanitizer_disable_or_bypass_switch()
    {
        var properties = typeof(ServiceMantleSerilogOptions).GetProperties();

        Assert.Equal(
            ["EnricherNames", "FlushTimeout", "MinimumLevel", "OutputTemplate"],
            properties.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(bool));
        Assert.DoesNotContain(
            properties,
            property => property.Name.Contains("Sanit", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Bypass", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Disable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Normalized_equivalent_duplicate_registration_is_idempotent()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options =>
        {
            options.MinimumLevel = " information ";
            options.EnricherNames = ["fromlogcontext", "FromLogContext"];
        });
        builder.AddServiceMantleSerilog(options =>
        {
            options.MinimumLevel = "Information";
            options.EnricherNames = ["FromLogContext"];
        });
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Single(host.Services.GetServices<ServiceMantleSerilogRuntime>());
        Assert.Single(host.Services.GetServices<ILoggerProvider>());
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Different_duplicate_configuration_fails_at_startup_without_values()
    {
        const string untrusted = "untrusted-level-value";
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.MinimumLevel = "Information");
        builder.AddServiceMantleSerilog(options => options.MinimumLevel = "Warning");
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleSerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("serilog.registration_conflict", exception.ErrorCode);
        Assert.DoesNotContain("Information", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Warning", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(untrusted, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflicting_console_sink_template_fails_at_startup_without_template_text()
    {
        const string templateSecret = "sink-template-secret";
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog();
        builder.AddServiceMantleSerilog(options =>
            options.OutputTemplate = "[{Level}] " + templateSecret + " {Message}{NewLine}");
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleSerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("serilog.console_sink_conflict", exception.ErrorCode);
        Assert.Equal("ConsoleSink", exception.FieldName);
        Assert.DoesNotContain(templateSecret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("level")]
    [InlineData("template")]
    public async Task Invalid_level_and_output_template_fail_safely_at_startup(string field)
    {
        const string invalidValue = "invalid-config-secret";
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options =>
        {
            if (field == "level")
            {
                options.MinimumLevel = invalidValue;
            }
            else
            {
                options.OutputTemplate = "{" + invalidValue;
            }
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleSerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            field == "level" ? "serilog.minimum_level_invalid" : "serilog.output_template_invalid",
            exception.ErrorCode);
        Assert.DoesNotContain(invalidValue, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preexisting_Serilog_configuration_is_a_safe_console_sink_conflict()
    {
        const string secret = "existing-sink-secret";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSerilog(
            new LoggerConfiguration().WriteTo.Console(outputTemplate: secret).CreateLogger(),
            dispose: true);
        builder.AddServiceMantleSerilog();
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<ServiceMantleSerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("serilog.console_sink_conflict", exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Normal_shutdown_flushes_exactly_once()
    {
        var sink = new TrackingSink();
        using var host = BuildHost(sink);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var runtime = host.Services.GetRequiredService<ServiceMantleSerilogRuntime>();

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.Equal(1, sink.DisposeCount);
        Assert.Equal(1, runtime.FlushInvocationCount);
    }

    [Fact]
    public async Task Observable_unhandled_exception_path_flushes_exactly_once()
    {
        var sink = new TrackingSink();
        using var host = BuildHost(sink);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var lifecycle = Assert.IsType<ServiceMantleSerilogLifecycle>(Assert.Single(
            host.Services.GetServices<IHostedService>(),
            service => service is ServiceMantleSerilogLifecycle));
        var runtime = host.Services.GetRequiredService<ServiceMantleSerilogRuntime>();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(lifecycle.HandleUnhandledExceptionForTests)));
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, sink.DisposeCount);
        Assert.Equal(1, runtime.FlushInvocationCount);
    }

    [Fact]
    public async Task Flush_timeout_does_not_hang_host_shutdown()
    {
        using var release = new ManualResetEventSlim();
        var sink = new TrackingSink(release);
        using var host = BuildHost(sink, TimeSpan.FromMilliseconds(25));
        await host.StartAsync(TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();

        await host.StopAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();
        release.Set();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(1, sink.DisposeCount);
    }

    [Fact]
    public async Task Throwing_sanitizer_emits_only_the_stable_marker_and_keeps_logging_alive()
    {
        const string secret = "sanitizer-failure-secret";
        var events = new CollectingSink();
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServiceMantleStructuredLogSanitizer>(
            new ThrowingSanitizer(secret)));
        builder.Services.Replace(ServiceDescriptor.Singleton<IServiceMantleSerilogSinkFactory>(
            new SanitizingCollectingSinkFactory(events)));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        events.Events.Clear();

        var logger = host.Services.GetRequiredService<ILogger<ServiceMantleSerilogHostTests>>();
        logger.LogInformation("first {Payload}", secret);
        logger.LogInformation("second {Payload}", secret);

        Assert.Equal(2, events.Events.Count);
        Assert.All(events.Events, logEvent =>
        {
            var property = Assert.Single(logEvent.Properties);
            Assert.Equal("SanitizationFailure", property.Key);
            Assert.Contains(StructuredLogSanitizer.SanitizationFailed, property.Value.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, property.Value.ToString(), StringComparison.Ordinal);
        });
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Service_context_and_correlation_scopes_keep_each_field_once()
    {
        var events = new CollectingSink();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "2.0.0");
        builder.AddServiceMantleSerilog();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServiceMantleSerilogSinkFactory>(
            new SanitizingCollectingSinkFactory(events)));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        events.Events.Clear();
        var logger = host.Services.GetRequiredService<ILogger<ServiceMantleSerilogHostTests>>();
        var serviceContext = host.Services.GetRequiredService<ServiceLogContext>();

        using (serviceContext.BeginScope(logger))
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            [ServiceLogFieldNames.CorrelationId] = "caller-42"
        }))
        {
            logger.LogInformation("handled {Operation}", "read");
        }

        var serialized = string.Join(
            ";",
            Assert.Single(events.Events).Properties.Select(property =>
                $"{property.Key}={property.Value}"));
        Assert.Equal(1, Count(serialized, ServiceLogFieldNames.ServiceName));
        Assert.Equal(1, Count(serialized, ServiceLogFieldNames.ServiceVersion));
        Assert.Equal(1, Count(serialized, ServiceLogFieldNames.InstanceId));
        Assert.Equal(1, Count(serialized, ServiceLogFieldNames.CorrelationId));
        Assert.Contains("catalog", serialized, StringComparison.Ordinal);
        Assert.Contains("catalog-01", serialized, StringComparison.Ordinal);
        Assert.Contains("caller-42", serialized, StringComparison.Ordinal);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static IHost BuildHost(TrackingSink sink, TimeSpan? timeout = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options =>
            options.FlushTimeout = timeout ?? ServiceMantleSerilogDefaults.FlushTimeout);
        builder.Services.Replace(ServiceDescriptor.Singleton<IServiceMantleSerilogSinkFactory>(
            new TrackingSinkFactory(sink)));
        return builder.Build();
    }

    private static int Count(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private sealed class TrackingSinkFactory(TrackingSink sink) : IServiceMantleSerilogSinkFactory
    {
        public ILogEventSink Create(
            ServiceMantleSerilogConfiguration configuration,
            IServiceMantleStructuredLogSanitizer sanitizer) => sink;
    }

    private sealed class TrackingSink(ManualResetEventSlim? release = null) : ILogEventSink, IDisposable
    {
        private int disposeCount;

        internal int DisposeCount => Volatile.Read(ref disposeCount);

        public void Emit(LogEvent logEvent)
        {
        }

        public void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
            release?.Wait();
        }
    }

    private sealed class ThrowingSanitizer(string secret) : IServiceMantleStructuredLogSanitizer
    {
        public IReadOnlyDictionary<string, object?> SanitizeFields(
            IEnumerable<KeyValuePair<string, object?>> fields) =>
            throw new InvalidOperationException(secret);
    }

    private sealed class SanitizingCollectingSinkFactory(CollectingSink collectingSink)
        : IServiceMantleSerilogSinkFactory
    {
        public ILogEventSink Create(
            ServiceMantleSerilogConfiguration configuration,
            IServiceMantleStructuredLogSanitizer sanitizer)
        {
            var inner = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(collectingSink)
                .CreateLogger();
            return new ServiceMantleSanitizingSink(sanitizer, inner);
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        internal List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
