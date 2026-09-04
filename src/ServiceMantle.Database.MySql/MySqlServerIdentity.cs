using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using MySqlConnector;

namespace ServiceMantle.Database.MySql;

// GET_LOCK is local to a server process, including when database metadata is hidden.
// The administrative connection owns the challenge until its existing finally closes it.
internal static class MySqlServerIdentity
{
    internal static async ValueTask<bool> VerifyAsync(
        DbConnection administrative,
        MySqlConnectionStringBuilder target,
        Func<MySqlConnectionStringBuilder, DbConnection> createConnection,
        CancellationToken cancellationToken)
    {
        await using var peer = createConnection(target);
        await peer.OpenAsync(cancellationToken).ConfigureAwait(false);
        var name = "ServiceMantle.Identity." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using var acquire = administrative.CreateCommand();
        acquire.CommandTimeout = 5;
        acquire.CommandText = "SELECT IF(@@read_only = 0, GET_LOCK(@name, 0), 0)";
        MySqlProbeConnection.AddParameter(acquire, "name", name);
        if (Convert.ToInt32(await acquire.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 1)
        {
            return false;
        }

        await using var owner = administrative.CreateCommand();
        owner.CommandTimeout = 5;
        owner.CommandText = "SELECT CONNECTION_ID()";
        var sessionId = Convert.ToInt64(await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        await using var proof = peer.CreateCommand();
        proof.CommandTimeout = 5;
        proof.CommandText = "SELECT IF(@@read_only = 0, IS_USED_LOCK(@name), NULL)";
        MySqlProbeConnection.AddParameter(proof, "name", name);
        var observed = await proof.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return observed is not null and not DBNull &&
            Convert.ToInt64(observed, CultureInfo.InvariantCulture) == sessionId;
    }
}
