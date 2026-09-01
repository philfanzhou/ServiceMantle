using System.Security.Cryptography;
using System.Text;

namespace ServiceMantle.Database.MariaDb.Migration;

internal static class MariaDbMigrationLockName
{
    private const string Prefix = "sm:migration:";
    private const int DigestCharacterCount = 51;

    internal static string Derive(ServiceId serviceId)
    {
        ArgumentNullException.ThrowIfNull(serviceId);

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(serviceId.Value)));
        return Prefix + digest[..DigestCharacterCount];
    }
}
