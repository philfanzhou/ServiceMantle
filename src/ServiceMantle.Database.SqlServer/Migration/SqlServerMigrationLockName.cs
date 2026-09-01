using System.Security.Cryptography;
using System.Text;

namespace ServiceMantle.Database.SqlServer.Migration;

internal static class SqlServerMigrationLockName
{
    private const string NamespacePrefix = "ServiceMantle.Migration.";

    internal static string Derive(ServiceId serviceId)
    {
        ArgumentNullException.ThrowIfNull(serviceId);

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(serviceId.Value)));
        return NamespacePrefix + digest;
    }
}
