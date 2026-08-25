using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceLogContextTests
{
    [Fact]
    public void AddServiceMantle_RegistersStableIdentityContext()
    {
        var services = new ServiceCollection();
        services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.2.3+build.4");

        using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<ServiceLogContext>();

        Assert.Equal("catalog", context.ServiceName);
        Assert.Equal("1.2.3+build.4", context.ServiceVersion);
        Assert.Equal("catalog-01", context.InstanceId);
        Assert.Same(context, provider.GetRequiredService<ServiceLogContext>());
    }

    [Fact]
    public void AddServiceMantle_ResolvesMissingVersionAndRejectsInvalidOrConflictingVersion()
    {
        var services = new ServiceCollection();
        var serviceId = ServiceId.Parse("catalog");
        var instanceId = InstanceId.Parse("catalog-01");

        services.AddServiceMantle(serviceId, instanceId);
        using var provider = services.BuildServiceProvider();

        Assert.False(string.IsNullOrWhiteSpace(
            provider.GetRequiredService<ServiceLogContext>().ServiceVersion));
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddServiceMantle(
                serviceId,
                instanceId,
                serviceVersion: " \n "));
        Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceMantle(serviceId, instanceId, serviceVersion: "different"));
    }

    [Fact]
    public void BeginScope_AddsIdentityAndControlledExtensionFieldsToEveryEvent()
    {
        using var fixture = new LoggingFixture();
        var context = CreateContext();
        var logger = fixture.Factory.CreateLogger("test");

        using (context.BeginScope(
            logger,
            [new("Operation", "bootstrap"), new("Attempt", 2)]))
        {
            logger.LogInformation("first");
            logger.LogWarning("second");
        }

        var records = fixture.Provider.Records.ToArray();
        Assert.Equal(2, records.Length);
        Assert.All(records, record =>
        {
            Assert.Equal("catalog", record.Fields[ServiceLogFieldNames.ServiceName]);
            Assert.Equal("2.0.0", record.Fields[ServiceLogFieldNames.ServiceVersion]);
            Assert.Equal("catalog-01", record.Fields[ServiceLogFieldNames.InstanceId]);
            Assert.Equal("bootstrap", record.Fields["Operation"]);
            Assert.Equal(2, record.Fields["Attempt"]);
        });
    }

    [Fact]
    public void BeginScope_RejectsProtectedDuplicateMissingAndInvalidExtensionFields()
    {
        using var fixture = new LoggingFixture();
        var context = CreateContext();
        var logger = fixture.Factory.CreateLogger("test");

        Assert.Throws<ArgumentException>(() => context.BeginScope(
            logger,
            [new("servicename", "override")]));
        Assert.Throws<ArgumentException>(() => context.BeginScope(
            logger,
            [new("Field", 1), new("field", 2)]));
        Assert.Throws<ArgumentException>(() => context.BeginScope(
            logger,
            [new("Missing", null)]));
        Assert.Throws<ArgumentException>(() => context.BeginScope(
            logger,
            [new("invalid field", 1)]));
        Assert.Throws<ArgumentException>(() => context.BeginScope(
            logger,
            Enumerable.Range(0, 33).Select(index =>
                new KeyValuePair<string, object?>($"Field{index}", index))));
    }

    [Fact]
    public void DisposedScope_DoesNotEnrichLaterEvents()
    {
        using var fixture = new LoggingFixture();
        var context = CreateContext();
        var logger = fixture.Factory.CreateLogger("test");

        using (context.BeginScope(logger, [new("Operation", "inside")]))
        {
            logger.LogInformation("inside");
        }

        logger.LogInformation("outside");

        var records = fixture.Provider.Records.ToArray();
        Assert.True(records[0].Fields.ContainsKey(ServiceLogFieldNames.ServiceName));
        Assert.False(records[1].Fields.ContainsKey(ServiceLogFieldNames.ServiceName));
        Assert.False(records[1].Fields.ContainsKey("Operation"));
    }

    [Fact]
    public async Task ConcurrentScopes_DoNotLeakExtensionFieldsBetweenRequests()
    {
        using var fixture = new LoggingFixture();
        var context = CreateContext();
        var logger = fixture.Factory.CreateLogger("test");
        using var start = new ManualResetEventSlim(false);

        var operations = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            using (context.BeginScope(logger, [new("RequestSlot", index)]))
            {
                start.Wait(TestContext.Current.CancellationToken);
                logger.LogInformation("request {RequestSlot}", index);
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        start.Set();
        await Task.WhenAll(operations);

        var records = fixture.Provider.Records.ToArray();
        Assert.Equal(64, records.Length);
        Assert.Equal(
            Enumerable.Range(0, 64),
            records.Select(record => Assert.IsType<int>(record.Fields["RequestSlot"])).Order());
        Assert.All(records, record =>
        {
            Assert.Equal("catalog", record.Fields[ServiceLogFieldNames.ServiceName]);
            Assert.Equal(4, record.Fields.Count);
        });
    }

    private static ServiceLogContext CreateContext() =>
        new(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"), "2.0.0");

    private sealed class LoggingFixture : IDisposable
    {
        internal LoggingFixture()
        {
            Provider = new CapturingLoggerProvider();
            Factory = LoggerFactory.Create(builder => builder.AddProvider(Provider));
        }

        internal CapturingLoggerProvider Provider { get; }

        internal ILoggerFactory Factory { get; }

        public void Dispose() => Factory.Dispose();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopeProvider = new LoggerExternalScopeProvider();

        internal ConcurrentQueue<LogRecord> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            this.scopeProvider = scopeProvider;
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                provider.scopeProvider.ForEachScope((scope, target) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> structuredScope)
                    {
                        foreach (var field in structuredScope)
                        {
                            target.Add(field.Key, field.Value);
                        }
                    }
                }, fields);

                provider.Records.Enqueue(new LogRecord(fields));
            }
        }
    }

    private sealed record LogRecord(IReadOnlyDictionary<string, object?> Fields);
}
