using System.Collections;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using ServiceMantle.AspNetCore;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ServiceMantleSensitiveHeaderTests
{
    [Fact]
    public void Capability_is_opt_in_and_does_not_change_plain_AddServiceMantle()
    {
        var services = new ServiceCollection();
        services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"));

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<ServiceMantleSensitiveHeaderRegistry>());
        Assert.Null(provider.GetService<ServiceMantleRequestHeaderDiagnosticProjector>());
        Assert.Null(provider.GetService<StructuredLogSanitizer>());
    }

    [Fact]
    public async Task Built_ins_cannot_be_removed_and_additions_merge_case_insensitively()
    {
        var configured = new List<string> { "X-Custom-Secret", "x-custom-secret" };
        using var host = Build(
            options => options.DeniedHeaderNames = configured,
            options => options.DeniedHeaderNames = ["X-Second-Secret", "X-CUSTOM-SECRET"]);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var registry = host.Services.GetRequiredService<ServiceMantleSensitiveHeaderRegistry>();

        Assert.All(
            StructuredLogSanitizerDefaults.BuiltInDeniedHeaderNames,
            name => Assert.True(registry.IsSensitive(name)));
        Assert.True(registry.IsSensitive("x-CUSTOM-secret"));
        Assert.True(registry.IsSensitive("x-second-secret"));
        Assert.Equal(
            1,
            registry.DeniedHeaderNames.Count(name =>
                string.Equals(name, "X-Custom-Secret", StringComparison.OrdinalIgnoreCase)));
        var setInterface = Assert.IsAssignableFrom<ISet<string>>(registry.DeniedHeaderNames);
        Assert.Throws<NotSupportedException>(() => setInterface.Add("X-Later-Secret"));
        Assert.Equal(
            ["DeniedHeaderNames"],
            typeof(ServiceMantleSensitiveHeadersOptions).GetProperties().Select(property => property.Name));
        Assert.Single(host.Services.GetServices<ServiceMantleSensitiveHeaderRegistry>());
        Assert.Single(host.Services.GetServices<ServiceMantleRequestHeaderDiagnosticProjector>());
        Assert.Single(host.Services.GetServices<StructuredLogSanitizer>());
        Assert.Single(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "ServiceMantleSensitiveHeaderStartupValidator");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Snapshot_is_materialized_after_build_once_and_ignores_later_mutation()
    {
        var names = new CountingEnumerable(["X-Initial-Secret"]);
        var builder = Host.CreateApplicationBuilder();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
            .AddSensitiveHeaders(options => options.DeniedHeaderNames = names);
        using var host = builder.Build();
        names.Values.Add("X-Before-Start-Secret");

        await host.StartAsync(TestContext.Current.CancellationToken);
        var registry = host.Services.GetRequiredService<ServiceMantleSensitiveHeaderRegistry>();
        names.Values.Add("X-After-Start-Secret");

        Assert.True(registry.IsSensitive("X-Initial-Secret"));
        Assert.True(registry.IsSensitive("X-Before-Start-Secret"));
        Assert.False(registry.IsSensitive("X-After-Start-Secret"));
        _ = registry.DeniedHeaderNames.Count;
        Assert.Equal(1, names.EnumerationCount);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Registry_drives_both_the_public_sanitizer_and_request_diagnostic_projection()
    {
        const string authorization = "Bearer built-in-secret";
        const string first = "custom-secret-one";
        const string second = "custom-secret-two";
        using var host = Build(options => options.DeniedHeaderNames = ["X-Custom-Secret"]);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var headers = new HeaderDictionary
        {
            ["Authorization"] = authorization,
            ["x-CUSTOM-secret"] = new StringValues([first, second]),
            ["X-Safe-Multi"] = new StringValues(["one", "two"])
        };

        var projector = host.Services.GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>();
        var projection = projector.Project(headers);
        var sanitizer = host.Services.GetRequiredService<StructuredLogSanitizer>();
        var direct = sanitizer.SanitizeHeaders(headers.Select(header =>
            new KeyValuePair<string, object?>(header.Key, header.Value.ToArray())));

        Assert.Equal(StructuredLogSanitizer.RedactedValue, projection["Authorization"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, projection["X-Custom-Secret"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, direct["Authorization"]);
        Assert.Equal(StructuredLogSanitizer.RedactedValue, direct["X-Custom-Secret"]);
        Assert.Equal(
            ["one", "two"],
            Assert.IsAssignableFrom<IReadOnlyList<object?>>(projection["X-Safe-Multi"]));
        var serialized = System.Text.Json.JsonSerializer.Serialize(projection);
        Assert.DoesNotContain(authorization, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(first, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(second, serialized, StringComparison.Ordinal);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task All_HTTP_token_characters_are_supported_by_the_registry_and_sanitizer()
    {
        const string tokenName = "X_!#$%&'*+-.^`|~";
        using var host = Build(options => options.DeniedHeaderNames = [tokenName]);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var output = host.Services.GetRequiredService<StructuredLogSanitizer>().SanitizeHeaders(
            [new(tokenName, "token-character-secret")]);

        Assert.True(host.Services.GetRequiredService<ServiceMantleSensitiveHeaderRegistry>()
            .IsSensitive(tokenName));
        Assert.Equal(StructuredLogSanitizer.RedactedValue, output[tokenName]);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Bad Header")]
    [InlineData("Bad:Header")]
    [InlineData("Bad\tHeader")]
    [InlineData("Bad\r\nHeader")]
    [InlineData("Bad(Header)")]
    [InlineData("Bad/Header")]
    [InlineData("Ünicode")]
    public async Task Invalid_names_fail_at_startup_without_echoing_the_name(string invalidName)
    {
        using var host = Build(options => options.DeniedHeaderNames = [invalidName]);

        var exception = await Assert.ThrowsAsync<ServiceMantleSensitiveHeaderConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WellKnownSensitiveHeaderConfigurationErrorCodes.InvalidName, exception.ErrorCode);
        Assert.Equal("DeniedHeaderNames", exception.FieldName);
        if (!string.IsNullOrWhiteSpace(invalidName))
        {
            Assert.DoesNotContain(invalidName, exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Enumeration_and_configuration_callback_failures_are_safe_startup_errors()
    {
        const string secret = "configuration-enumeration-secret";
        using var enumerationHost = Build(options =>
            options.DeniedHeaderNames = new ThrowingEnumerable(secret));
        using var callbackHost = Build(_ => throw new InvalidOperationException(secret));

        var enumerationFailure = await Assert.ThrowsAsync<
            ServiceMantleSensitiveHeaderConfigurationException>(() =>
            enumerationHost.StartAsync(TestContext.Current.CancellationToken));
        var callbackFailure = await Assert.ThrowsAsync<
            ServiceMantleSensitiveHeaderConfigurationException>(() =>
            callbackHost.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            WellKnownSensitiveHeaderConfigurationErrorCodes.EnumerationFailed,
            enumerationFailure.ErrorCode);
        Assert.Equal(
            WellKnownSensitiveHeaderConfigurationErrorCodes.ConfigureFailed,
            callbackFailure.ErrorCode);
        Assert.DoesNotContain(secret, enumerationFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, callbackFailure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_separately_registered_sanitizer_is_a_safe_startup_conflict(bool before)
    {
        var builder = Host.CreateApplicationBuilder();
        if (before)
        {
            builder.Services.AddSingleton(new StructuredLogSanitizer());
        }

        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"));
        serviceMantle.AddSensitiveHeaders();
        if (!before)
        {
            builder.Services.AddSingleton(new StructuredLogSanitizer());
        }

        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<ServiceMantleSensitiveHeaderConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            WellKnownSensitiveHeaderConfigurationErrorCodes.SanitizerConflict,
            exception.ErrorCode);
        Assert.Equal(nameof(StructuredLogSanitizer), exception.FieldName);
    }

    [Fact]
    public async Task Projection_enumeration_failure_returns_only_the_stable_marker()
    {
        const string secret = "request-header-enumeration-secret";
        using var host = Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        var output = host.Services
            .GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>()
            .Project(new ThrowingHeaderDictionary(secret));

        var failure = Assert.Single(output);
        Assert.Equal("SanitizationFailure", failure.Key);
        Assert.Equal(StructuredLogSanitizer.SanitizationFailed, failure.Value);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(output), StringComparison.Ordinal);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Snapshot_and_projection_support_concurrent_reads()
    {
        using var host = Build(options => options.DeniedHeaderNames = ["X-Concurrent-Secret"]);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var registry = host.Services.GetRequiredService<ServiceMantleSensitiveHeaderRegistry>();
        var projector = host.Services.GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>();
        var headers = new HeaderDictionary { ["x-concurrent-secret"] = "concurrent-secret-value" };

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            Assert.True(registry.IsSensitive("X-CONCURRENT-SECRET"));
            Assert.Equal(
                StructuredLogSanitizer.RedactedValue,
                projector.Project(headers)["X-Concurrent-Secret"]);
        })));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Product_headers_and_third_party_tracing_remain_explicitly_outside_the_defaults()
    {
        const string productHeader = "X-Admin-AppSecret";
        const string secret = "product-specific-secret";
        using var defaultHost = Build();
        await defaultHost.StartAsync(TestContext.Current.CancellationToken);
        var headers = new HeaderDictionary { [productHeader] = secret };
        using var activity = new Activity("third-party").Start();
        activity?.SetTag(productHeader, secret);

        var projection = defaultHost.Services
            .GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>()
            .Project(headers);

        Assert.False(defaultHost.Services.GetRequiredService<ServiceMantleSensitiveHeaderRegistry>()
            .IsSensitive(productHeader));
        Assert.Equal(secret, projection[productHeader]);
        Assert.Equal(secret, headers[productHeader].ToString());
        Assert.Equal(secret, activity?.GetTagItem(productHeader));
        await defaultHost.StopAsync(TestContext.Current.CancellationToken);

        using var configuredHost = Build(options => options.DeniedHeaderNames = [productHeader]);
        await configuredHost.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            StructuredLogSanitizer.RedactedValue,
            configuredHost.Services.GetRequiredService<ServiceMantleRequestHeaderDiagnosticProjector>()
                .Project(headers)[productHeader]);
        await configuredHost.StopAsync(TestContext.Current.CancellationToken);
    }

    private static IHost Build(params Action<ServiceMantleSensitiveHeadersOptions>[] registrations)
    {
        var builder = Host.CreateApplicationBuilder();
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"));
        if (registrations.Length == 0)
        {
            serviceMantle.AddSensitiveHeaders();
        }
        else
        {
            foreach (var registration in registrations)
            {
                serviceMantle.AddSensitiveHeaders(registration);
            }
        }

        return builder.Build();
    }

    private sealed class CountingEnumerable(IEnumerable<string> values) : IEnumerable<string>
    {
        internal List<string> Values { get; } = values.ToList();
        internal int EnumerationCount { get; private set; }

        public IEnumerator<string> GetEnumerator()
        {
            EnumerationCount++;
            return Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingEnumerable(string secret) : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException(secret);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingHeaderDictionary(string secret) : IHeaderDictionary
    {
        public StringValues this[string key]
        {
            get => StringValues.Empty;
            set => throw new NotSupportedException();
        }

        public long? ContentLength { get; set; }
        public ICollection<string> Keys => [];
        public ICollection<StringValues> Values => [];
        public int Count => 1;
        public bool IsReadOnly => true;
        public void Add(string key, StringValues value) => throw new NotSupportedException();
        public void Add(KeyValuePair<string, StringValues> item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(KeyValuePair<string, StringValues> item) => false;
        public bool ContainsKey(string key) => false;
        public void CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex) =>
            throw new NotSupportedException();
        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() =>
            throw new InvalidOperationException(secret);
        public bool Remove(string key) => throw new NotSupportedException();
        public bool Remove(KeyValuePair<string, StringValues> item) => throw new NotSupportedException();
        public bool TryGetValue(string key, out StringValues value)
        {
            value = StringValues.Empty;
            return false;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
