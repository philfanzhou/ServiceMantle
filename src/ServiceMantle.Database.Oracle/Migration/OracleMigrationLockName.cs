using System.Security.Cryptography;
using System.Text;

namespace ServiceMantle.Database.Oracle.Migration;

internal static class OracleMigrationLockName
{
    private const string NamespacePrefix = "ServiceMantle.Migration.";

    internal static string Derive(ServiceId serviceId)
    {
        ArgumentNullException.ThrowIfNull(serviceId);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(serviceId.Value));
        return NamespacePrefix + Convert.ToHexStringLower(digest);
    }
}
