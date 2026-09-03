using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace ServiceMantle.Database.SqlServer;

// Both sessions use master/public, so the challenge is independent of a missing target database.
internal static class SqlServerServerIdentity
{
    internal static async ValueTask<bool> VerifyAsync(
        SqlConnection administrative,
        SqlConnectionStringBuilder target,
        CancellationToken cancellationToken)
    {
        await using var peer = new SqlConnection(target.ConnectionString);
        await peer.OpenAsync(cancellationToken).ConfigureAwait(false);
        var name = "ServiceMantle.Identity." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        if (await TestLockAsync(peer, name, cancellationToken).ConfigureAwait(false) is not 1)
        {
            return false;
        }

        await using var acquire = administrative.CreateCommand();
        acquire.CommandTimeout = 5;
        acquire.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @name, @LockMode = 'Exclusive',
                @LockOwner = 'Session', @LockTimeout = 0, @DbPrincipal = 'public';
            SELECT @result;
            """;
        acquire.Parameters.Add("name", SqlDbType.NVarChar, 255).Value = name;
        if (await acquire.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 0)
        {
            return false;
        }

        var verified = await TestLockAsync(peer, name, cancellationToken).ConfigureAwait(false) is 0;
        cancellationToken.ThrowIfCancellationRequested();
        return verified;
    }

    private static async ValueTask<object?> TestLockAsync(
        SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT CONVERT(int, APPLOCK_TEST('public', @name, 'Exclusive', 'Session'))";
        command.Parameters.Add("name", SqlDbType.NVarChar, 255).Value = name;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
