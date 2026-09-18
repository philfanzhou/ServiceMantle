-- Target-identity fixture for the pinned Oracle Database Free container (#317).
--
-- The default COMMON_USER_PREFIX ('C##') is what usually makes a common user recognizable by
-- name; with a non-default or empty prefix that name proves nothing, so the target-identity
-- tests read the USER_USERS classification row instead. This script enables the empty prefix in
-- the spfile and restarts the instance so a no-prefix common user can be created at all; the
-- listener verification that follows the restart re-checks that the database is back.
--
-- Run as: docker exec -i <container> sqlplus -s / as sysdba < eng/oracle-identity-fixture.sql
ALTER SYSTEM SET COMMON_USER_PREFIX = '' SCOPE = SPFILE;
SHUTDOWN IMMEDIATE;
STARTUP;
ALTER PLUGGABLE DATABASE ALL OPEN;
EXIT;
