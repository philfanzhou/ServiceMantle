# Server-database preparation identity contract

The PostgreSQL, MySQL, MariaDB, and SQL Server preparation providers verify that the
administrative session and the target endpoint reach the same server before accepting
an existing target or issuing `CREATE DATABASE`. This implements the provider-independent
requirement on `IDatabaseTargetPreparationProvider.PrepareAsync` without adding driver
references to the core package or a persistent server-identity registry.

## Evidence and session ownership

The administrative connection creates a fresh random 128-bit challenge using temporary
session locks. A separate connection using the **target credentials and endpoint** must
observe that challenge in the same server lock namespace. Neither connection-string text,
host names, ports, nor user names are used as evidence of server equality. The creation
probe continues on the **same unpooled administrative physical session**; it never verifies
one connection and then opens another to create the database. The challenge is not a
migration lock and does not serialize preparation calls.

| Provider | Shared namespace | Required evidence |
| --- | --- | --- |
| PostgreSQL | Administrative connection's maintenance database; `postgres` when unspecified | Two random 64-bit advisory locks in `pg_catalog.pg_locks`, with the administrative backend PID, exclusive granted mode, and current database OID |
| MySQL / MariaDB | Server-level connections with `Database` cleared | A random named `GET_LOCK` whose `IS_USED_LOCK` owner equals the administrative connection ID |
| SQL Server | `master`, principal `public` | A fresh `APPLOCK_TEST` is grantable before the administrator's session `sp_getapplock`, then not grantable after it |

All challenge SQL uses parameters and short command timeouts. Both connections disable
pooling and ambient transaction enlistment. SQL Server disables connection retries.
The target proof connection is disposed after verification; the administrative session
retains its challenge until the creation probe's existing `finally` closes it. On failure,
cancellation, or timeout, both sessions are cleaned up and no subsequent creation runs.
Disposal errors cannot expose an underlying exception or replace the safe outer result.

## Missing targets and restricted accounts

Verification uses a maintenance namespace, so it does not need to connect to the missing
target database. It does need to authenticate the target credentials there. A wrong target
password cannot be substituted with administrative credentials. PostgreSQL target logins
need `CONNECT` on the selected maintenance database and visibility of `pg_catalog.pg_locks`;
SQL Server target logins need access to `master` and its public application-lock functions.
MySQL and MariaDB use the public named-lock functions without requiring visibility of the
target in `INFORMATION_SCHEMA.SCHEMATA`.

A missing target and an inaccessible target are never treated as proof of server identity.
If authentication, permissions, or connectivity prevent verification, preparation fails
closed with the corresponding existing safe error code. A completed but negative or
unsupported proof returns `database_target_preparation.invalid_target`. Caller cancellation
precedes a returned rejection or timeout and is sanitized. No result or diagnostic includes
target/administrative credentials, endpoint identities, challenge values, or driver errors.

Creation does not create logins, repair grants, or make the target credentials able to use
the newly created database. Consumers still observe/connect to the target after preparation.
Existing identifier, ownership, collation, concurrency, and non-destructive creation rules
continue to apply after verification.

## Trust and routing boundary

Consumers must authenticate and trust both endpoints under their own TLS/credential policy.
The supported topology is a stable single server, accessed directly or through aliases/a
proxy whose deployment contract pins sessions to that server independently of database and
user. Different legitimate aliases can succeed. Managed offerings can be used only when they
provide those routing and maintenance-namespace guarantees and expose the proof operations.

Explicit multi-host lists, Npgsql multiplexing, SQL Server read-only application intent,
and SQL Server failover partners are rejected. PostgreSQL recovery nodes and MySQL/MariaDB
servers reporting `read_only` also reject proof. An administrative session loss does not
permit reopening a connection or replaying creation; retry requires a new preparation call
and fresh verification.

This contract does **not** discover hidden proxy policy or support database/user/statement
routing, cross-region routing, transparent session replacement, or cluster failover. Do not
invoke preparation when those routing guarantees are unknown. It does not authenticate an
untrusted server that forges protocol/lock responses, defend against a malicious server
administrator, or provide a permanent identity/fencing guarantee for later application
connections. Random challenges avoid accidental aliasing; they are not a substitute for
server authentication. A DDL acknowledgement lost after creation is not rolled back by this
verification step.

## Database references

- [PostgreSQL advisory lock representation and database scope](https://www.postgresql.org/docs/18/view-pg-locks.html)
- [MySQL named locks and connection ownership](https://dev.mysql.com/doc/refman/8.4/en/locking-functions.html)
- [MariaDB IS_USED_LOCK](https://mariadb.com/docs/server/reference/sql-functions/secondary-functions/miscellaneous-functions/is_used_lock)
- [SQL Server APPLOCK_TEST and database scope](https://learn.microsoft.com/en-us/sql/t-sql/functions/applock-test-transact-sql)
