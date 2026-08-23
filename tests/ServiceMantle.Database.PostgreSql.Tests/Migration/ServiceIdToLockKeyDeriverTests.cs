using ServiceMantle.Database.PostgreSql.Migration;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests.Migration;

public class ServiceIdToLockKeyDeriverTests
{
    [Fact]
    public void DeriveAdvisoryLockKey_WithValidServiceId_ReturnsSameLongKey()
    {
        var serviceId = ServiceId.Parse("test-service");

        var key1 = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId);
        var key2 = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DeriveAdvisoryLockKey_WithDifferentServiceIds_ReturnsDifferentKeys()
    {
        var serviceId1 = ServiceId.Parse("service-1");
        var serviceId2 = ServiceId.Parse("service-2");

        var key1 = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId1);
        var key2 = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId2);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DeriveAdvisoryLockKey_FixedVector_Signacore()
    {
        // Fixed vector test: hardcoded expected value for determinism verification
        // This proves the algorithm is stable across platforms and doesn't depend on
        // endianness-dependent operations like BitConverter.
        var serviceId = ServiceId.Parse("signacore");
        var key = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId);

        // SHA256("ServiceMantle.Migration.signacore").BigEndian[0:8] as int64
        const long expectedKey = -8197774346362508027;
        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public void DeriveAdvisoryLockKey_FixedVector_TestService()
    {
        // Fixed vector test: hardcoded expected value for "test-service"
        var serviceId = ServiceId.Parse("test-service");
        var key = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId);

        // SHA256("ServiceMantle.Migration.test-service").BigEndian[0:8] as int64
        const long expectedKey = -6409392792308155105;
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("signacore")]
    [InlineData("test-service")]
    [InlineData("my-service-123")]
    [InlineData("a")]
    public void DeriveAdvisoryLockKey_WithVariousServiceIds_IsDeterministic(string serviceIdValue)
    {
        var serviceId = ServiceId.Parse(serviceIdValue);
        var key = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId);

        // Same input should return the same key
        var key2 = ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(serviceId);
        Assert.Equal(key, key2);
    }

    [Fact]
    public void DeriveAdvisoryLockKey_WithNullServiceId_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => ServiceIdToLockKeyDeriver.DeriveAdvisoryLockKey(null!));

        Assert.NotNull(ex);
    }
}
