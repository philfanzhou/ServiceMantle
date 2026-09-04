using System.Buffers.Binary;
using System.Security.Cryptography;
using Npgsql;

namespace ServiceMantle.Database.PostgreSql;

// A fresh 128-bit challenge binds the target endpoint to the very session that will create.
// Locks are kept until that unpooled administrative session closes; no identity is cached.
internal static class PostgreSqlServerIdentity
{
    internal static async ValueTask<bool> VerifyAsync(
        NpgsqlConnection administrative,
        NpgsqlConnectionStringBuilder target,
        CancellationToken cancellationToken)
    {
        await using var peer = new NpgsqlConnection(target.ConnectionString);
        await peer.OpenAsync(cancellationToken).ConfigureAwait(false);
        var bytes = RandomNumberGenerator.GetBytes(16);
        var first = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        var second = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8));
        if (first == second)
        {
            return false;
        }

        foreach (var key in new[] { first, second })
        {
            await using var command = administrative.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = "SELECT NOT pg_catalog.pg_is_in_recovery() AND pg_catalog.pg_try_advisory_lock(@key)";
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }
        }

        await using var proof = peer.CreateCommand();
        proof.CommandTimeout = 5;
        proof.CommandText = """
            SELECT NOT pg_catalog.pg_is_in_recovery() AND COUNT(*) = 2
            FROM pg_catalog.pg_locks
            WHERE locktype = 'advisory' AND granted AND mode = 'ExclusiveLock'
                AND pid = @pid AND objsubid = 1
                AND database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                AND ((classid::bigint << 32) | objid::bigint) IN (@first, @second)
            """;
        proof.Parameters.AddWithValue("pid", administrative.ProcessID);
        proof.Parameters.AddWithValue("first", first);
        proof.Parameters.AddWithValue("second", second);
        var verified = await proof.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        cancellationToken.ThrowIfCancellationRequested();
        return verified;
    }
}
