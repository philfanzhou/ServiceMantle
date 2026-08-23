using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ServiceMantle.Database.PostgreSql.Migration;

/// <summary>
/// Derives a stable, deterministic PostgreSQL advisory lock key from a ServiceId.
/// The key is stable across processes, machines, and restarts, using explicit big-endian byte order.
/// </summary>
internal static class ServiceIdToLockKeyDeriver
{
    private const string LockKeyNamespacePrefix = "ServiceMantle.Migration.";

    /// <summary>
    /// Derives a 64-bit advisory lock key from a service identifier.
    /// Uses SHA-256 with explicit big-endian byte order to ensure stability across platforms.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <returns>A 64-bit lock key suitable for pg_advisory_lock.</returns>
    public static long DeriveAdvisoryLockKey(ServiceId serviceId)
    {
        ArgumentNullException.ThrowIfNull(serviceId);

        var input = LockKeyNamespacePrefix + serviceId.Value;
        var inputBytes = Encoding.UTF8.GetBytes(input);

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(inputBytes);

        // Read the first 8 bytes as a signed 64-bit integer in big-endian byte order.
        // Big-endian is used to ensure deterministic results across all platforms.
        // This is deterministic and stable across all invocations and architectures.
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(hashBytes);
        return lockKey;
    }
}
