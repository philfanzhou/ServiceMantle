using System.Data;
using Npgsql;
using ServiceMantle.Migration;

namespace ServiceMantle.Database.PostgreSql.Migration;

/// <summary>
/// Reads a PostgreSQL database's schema evidence on the caller's connection: the applied EF
/// migration ids and the complete <see cref="SchemaSnapshot"/> (tables with schema, columns with
/// identity kind and the stored-default evidence bit, primary keys, foreign keys with their
/// delete rules, and non-constraint indexes), returned as one
/// <see cref="SchemaEvidenceReadResult"/> that keeps the two failure facts — the target database
/// does not exist versus the read failed — separate.
/// </summary>
/// <remarks>
/// <para>
/// Read-only by contract: every query is a <c>SELECT</c> over <c>pg_catalog</c> and the EF
/// migrations history table, the reader never starts its own transaction, and it never writes.
/// It runs on the caller's connection object — opened by the reader when closed (so the
/// "database does not exist" fact, which PostgreSQL reports as SQLSTATE <c>3D000</c> at connect
/// time, is observable by the reader) and never closed by it; the caller owns disposal.
/// </para>
/// <para>
/// Deterministic: on the same connection and database the tables are ordered by schema then
/// name, columns by catalog position, key and index columns by constraint position, and foreign
/// keys by constraint creation order. The migrations history table never appears in the snapshot
/// — it is reported through the applied-migration ids — and a database without a history table
/// reads as "no migrations applied" (SQLSTATE <c>42P01</c> on that one query); an empty database
/// reads as an empty snapshot — whether either counts as "empty" is the consumer's
/// classification decision.
/// </para>
/// <para>
/// Safety: caller cancellation propagates as <see cref="OperationCanceledException"/> and is
/// never converted into a failure fact. Every other failure — authentication, network, timeout,
/// permission — is returned as the <see cref="SchemaEvidenceReadState.ReadFailed"/> fact for the
/// catalog object being read, and is never reported as "database missing": the missing-target
/// fact is decided solely by SQLSTATE <c>3D000</c> found anywhere in the exception chain.
/// <see cref="SchemaEvidenceReadResult.Message"/> is rendered by the core type from one
/// identifier; the reader passes only catalog object identifiers, so no SQL text, connection
/// value, or exception text can reach it.
/// </para>
/// <para>
/// Non-guarantees: the read is one observation, not a transactional point-in-time image —
/// concurrent DDL can interleave between the queries; the default entry excludes expression
/// indexes, while explicit extended evidence reports their key counts without expression text; identifier alignment with the expected side (for
/// example the <c>public</c> default schema versus an unconfigured EF model deriving null) is
/// the caller's responsibility per the contract in
/// <c>docs/contracts/schema-evidence-models.md</c>.
/// </para>
/// </remarks>
public sealed class PostgreSqlSchemaEvidenceReader
{
    private readonly Action<string>? completionCheckpoint;

    /// <summary>Creates a read-only evidence reader.</summary>
    public PostgreSqlSchemaEvidenceReader() { }

    internal PostgreSqlSchemaEvidenceReader(Action<string> completionCheckpoint) =>
        this.completionCheckpoint = completionCheckpoint;

    private const string HistoryTableName = "__EFMigrationsHistory";
    private const string TargetMissingSqlState = "3D000";
    private const string UndefinedTableSqlState = "42P01";

    private const string AppliedMigrationsScript =
        $"SELECT \"MigrationId\" FROM \"{HistoryTableName}\" ORDER BY \"MigrationId\"";

    private const string TablesScript = """
        SELECT n.nspname AS schema_name, c.relname AS table_name
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'r'
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname !~ '^pg_toast'
          AND n.nspname !~ '^pg_temp_'
          AND c.relname <> '__EFMigrationsHistory'
        ORDER BY n.nspname, c.relname
        """;

    // The table list reaches the detail queries as two parallel array parameters, paired back
    // into (schema, table) rows by the row-value IN predicate.
    private const string TableFilter = """
        (n.nspname, tbl.relname) IN (
            SELECT s, t FROM unnest(@schemaNames::text[], @tableNames::text[]) AS u(s, t))
        """;

    private const string ColumnsScript = $"""
        SELECT n.nspname AS schema_name, tbl.relname AS table_name, a.attname AS column_name,
               format_type(a.atttypid, a.atttypmod) AS data_type,
               NOT a.attnotnull AS is_nullable,
               COALESCE(a.attidentity::text, '') AS identity_kind,
               (ad.oid IS NOT NULL) AS has_stored_default
        FROM pg_catalog.pg_attribute a
        JOIN pg_catalog.pg_class tbl ON a.attrelid = tbl.oid
        JOIN pg_catalog.pg_namespace n ON tbl.relnamespace = n.oid
        LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
        WHERE a.attnum > 0 AND NOT a.attisdropped AND {TableFilter}
        ORDER BY n.nspname, tbl.relname, a.attnum
        """;

    private const string PrimaryKeysScript = $"""
        SELECT n.nspname AS schema_name, tbl.relname AS table_name, a.attname AS column_name
        FROM pg_catalog.pg_index i
        JOIN pg_catalog.pg_class tbl ON i.indrelid = tbl.oid
        JOIN pg_catalog.pg_namespace n ON tbl.relnamespace = n.oid
        JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS keys(attnum, ord) ON true
        JOIN pg_catalog.pg_attribute a ON a.attrelid = tbl.oid AND a.attnum = keys.attnum
        WHERE i.indisprimary AND {TableFilter}
        ORDER BY n.nspname, tbl.relname, keys.ord
        """;

    // Constraint and index names are read as grouping keys only; they never enter the model.
    private const string ForeignKeysScript = $"""
        SELECT n.nspname AS schema_name, tbl.relname AS table_name,
               con.conname AS constraint_name, con.confdeltype::text AS delete_rule,
               ca.attname AS column_name,
               rn.nspname AS referenced_schema, reft.relname AS referenced_table,
               fa.attname AS referenced_column
        FROM pg_catalog.pg_constraint con
        JOIN pg_catalog.pg_class tbl ON con.conrelid = tbl.oid
        JOIN pg_catalog.pg_namespace n ON tbl.relnamespace = n.oid
        JOIN pg_catalog.pg_class reft ON con.confrelid = reft.oid
        JOIN pg_catalog.pg_namespace rn ON reft.relnamespace = rn.oid
        JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS ck(attnum, ord) ON true
        JOIN LATERAL unnest(con.confkey) WITH ORDINALITY AS fk(attnum, ord) ON fk.ord = ck.ord
        JOIN pg_catalog.pg_attribute ca ON ca.attrelid = con.conrelid AND ca.attnum = ck.attnum
        JOIN pg_catalog.pg_attribute fa ON fa.attrelid = con.confrelid AND fa.attnum = fk.attnum
        WHERE con.contype = 'f' AND {TableFilter}
        ORDER BY n.nspname, tbl.relname, con.oid, ck.ord
        """;

    // Constraint-backing indexes (primary key, unique constraints) and indexes with expression
    // columns are excluded: the model carries non-constraint indexes by plain columns only.
    private const string IndexesScript = $"""
        SELECT n.nspname AS schema_name, tbl.relname AS table_name,
               idx.relname AS index_name, i.indisunique AS is_unique,
               a.attname AS column_name
        FROM pg_catalog.pg_index i
        JOIN pg_catalog.pg_class tbl ON i.indrelid = tbl.oid
        JOIN pg_catalog.pg_class idx ON i.indexrelid = idx.oid
        JOIN pg_catalog.pg_namespace n ON tbl.relnamespace = n.oid
        JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS keys(attnum, ord) ON true
        JOIN pg_catalog.pg_attribute a ON a.attrelid = tbl.oid AND a.attnum = keys.attnum
        WHERE NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_constraint con WHERE con.conindid = i.indexrelid)
          AND NOT EXISTS (SELECT 1 FROM unnest(i.indkey) AS k WHERE k = 0)
          AND {TableFilter}
        ORDER BY n.nspname, tbl.relname, idx.relname, keys.ord
        """;

    private const string ExtendedPrimaryKeysScript = $"""
        SELECT n.nspname, tbl.relname, a.attname, con.conname
        FROM pg_catalog.pg_index i
        JOIN pg_catalog.pg_class tbl ON i.indrelid = tbl.oid
        JOIN pg_catalog.pg_namespace n ON tbl.relnamespace = n.oid
        JOIN pg_catalog.pg_constraint con ON con.conindid = i.indexrelid AND con.contype = 'p'
        JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS keys(attnum, ord) ON true
        JOIN pg_catalog.pg_attribute a ON a.attrelid = tbl.oid AND a.attnum = keys.attnum
        WHERE i.indisprimary AND keys.ord <= i.indnkeyatts AND {TableFilter}
        ORDER BY n.nspname, tbl.relname, keys.ord
        """;

    private const string ExtendedIndexesScript = $"""
        SELECT n.nspname, tbl.relname, idx.relname, i.indisunique,
               a.attname, i.indnkeyatts, keys.ord
        FROM pg_catalog.pg_index i
        JOIN pg_catalog.pg_class tbl ON i.indrelid = tbl.oid
        JOIN pg_catalog.pg_class idx ON i.indexrelid = idx.oid
        JOIN pg_catalog.pg_namespace n ON tbl.relnamespace = n.oid
        JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS keys(attnum, ord) ON true
        LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid = tbl.oid AND a.attnum = keys.attnum
        WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint con WHERE con.conindid = i.indexrelid)
          AND {TableFilter}
        ORDER BY n.nspname, tbl.relname, idx.relname, keys.ord
        """;

    /// <summary>
    /// Reads the complete schema evidence on the caller's connection.
    /// </summary>
    /// <param name="connection">
    /// The caller's connection; opened by the reader when closed, never closed by it.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the read at the entry checkpoint and any in-flight query.
    /// </param>
    /// <returns>
    /// The successful evidence, or one of the two failure facts; never a classification.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    /// <exception cref="OperationCanceledException">
    /// The <paramref name="cancellationToken"/> was observed cancelled; no failure fact is
    /// produced.
    /// </exception>
    public Task<SchemaEvidenceReadResult> ReadAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default) =>
        ReadAsync(connection, new PostgreSqlSchemaEvidenceReadOptions(), cancellationToken);

    /// <summary>Reads explicitly selected tables and optional extended object evidence.</summary>
    /// <remarks>Null scope selects all candidate tables; an empty scope selects none. The caller owns
    /// the connection and any transaction. Concurrent DDL can interleave; expressions are not interpreted.</remarks>
    public async Task<SchemaEvidenceReadResult> ReadAsync(
        NpgsqlConnection connection,
        PostgreSqlSchemaEvidenceReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var databaseName = IsValidIdentifier(connection.Database) ? connection.Database : "database";
        var step = new StepTracker(databaseName);
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            var appliedMigrationIds = await ReadAppliedMigrationIdsAsync(
                connection, step, cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadSnapshotAsync(connection, options, step, cancellationToken)
                .ConfigureAwait(false);
            return SchemaEvidenceReadResult.Success(appliedMigrationIds, snapshot);
        }
        catch (Exception exception)
        {
            return ClassifyFailure(exception, databaseName, step.Identifier);
        }
        finally
        {
            completionCheckpoint?.Invoke("final");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool IsValidIdentifier(string? value) => value is { Length: >= 1 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private async Task<IReadOnlyList<string>> ReadAppliedMigrationIdsAsync(
        NpgsqlConnection connection,
        StepTracker step,
        CancellationToken cancellationToken)
    {
        step.Identifier = HistoryTableName;
        var appliedMigrationIds = new List<string>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = AppliedMigrationsScript;
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                appliedMigrationIds.Add(reader.GetString(0));
            }
        }
        catch (PostgresException exception) when (exception.SqlState == UndefinedTableSqlState)
        {
            // A database without the history table reports no applied migrations; whether that
            // means "empty" is the consumer's decision.
            appliedMigrationIds.Clear();
        }

        completionCheckpoint?.Invoke("history");
        cancellationToken.ThrowIfCancellationRequested();
        return appliedMigrationIds;
    }

    private async Task<SchemaSnapshot> ReadSnapshotAsync(
        NpgsqlConnection connection,
        PostgreSqlSchemaEvidenceReadOptions options,
        StepTracker step,
        CancellationToken cancellationToken)
    {
        step.Identifier = "pg_class";
        var tables = new List<(string Schema, string Name)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = TablesScript;
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var pair = (reader.GetString(0), reader.GetString(1));
                if (options.Tables is null || options.Tables.Contains(pair))
                    tables.Add(pair);
            }
        }

        completionCheckpoint?.Invoke("tables");
        cancellationToken.ThrowIfCancellationRequested();
        if (tables.Count == 0) return new SchemaSnapshot([]);
        var schemas = tables.Select(table => table.Schema).ToArray();
        var names = tables.Select(table => table.Name).ToArray();

        step.Identifier = "pg_attribute";
        var columns = await ReadColumnsAsync(connection, schemas, names, cancellationToken)
            .ConfigureAwait(false);
        step.Identifier = "pg_index";
        var primaryKeys = await ReadPrimaryKeysAsync(connection, schemas, names, options.IncludeExtendedObjectEvidence, cancellationToken)
            .ConfigureAwait(false);
        step.Identifier = "pg_constraint";
        var foreignKeys = await ReadForeignKeysAsync(connection, schemas, names, options.IncludeExtendedObjectEvidence, cancellationToken)
            .ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(connection, schemas, names, options.IncludeExtendedObjectEvidence, cancellationToken)
            .ConfigureAwait(false);

        var schemaTables = tables
            .Select(table => new SchemaTable(
                table.Name,
                columns[(table.Schema, table.Name)],
                primaryKeys.GetValueOrDefault((table.Schema, table.Name)),
                foreignKeys.TryGetValue((table.Schema, table.Name), out var tableForeignKeys)
                    ? tableForeignKeys
                    : [],
                indexes.TryGetValue((table.Schema, table.Name), out var tableIndexes)
                    ? tableIndexes
                    : [],
                table.Schema))
            .ToList();

        return new SchemaSnapshot(schemaTables);
    }

    private async Task<Dictionary<(string Schema, string Table), List<SchemaColumn>>> ReadColumnsAsync(
        NpgsqlConnection connection,
        string[] schemas,
        string[] names,
        CancellationToken cancellationToken)
    {
        try
        {
            var columns = new Dictionary<(string, string), List<SchemaColumn>>();
            await using var command = connection.CreateCommand();
            command.CommandText = ColumnsScript;
            command.Parameters.AddWithValue("schemaNames", schemas);
            command.Parameters.AddWithValue("tableNames", names);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!columns.TryGetValue(key, out var tableColumns))
                {
                    columns[key] = tableColumns = [];
                }

                tableColumns.Add(new SchemaColumn(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    MapIdentityKind(reader.GetString(5)),
                    reader.GetBoolean(6)));
            }

            return columns;
        }
        finally
        {
            completionCheckpoint?.Invoke("ReadColumnsAsync");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<Dictionary<(string Schema, string Table), SchemaPrimaryKey>> ReadPrimaryKeysAsync(
        NpgsqlConnection connection,
        string[] schemas,
        string[] names,
        bool extended,
        CancellationToken cancellationToken)
    {
        try
        {
            var primaryKeys = new Dictionary<(string, string), List<string>>();
            var constraintNames = new Dictionary<(string, string), string>();
            await using var command = connection.CreateCommand();
            command.CommandText = extended ? ExtendedPrimaryKeysScript : PrimaryKeysScript;
            command.Parameters.AddWithValue("schemaNames", schemas);
            command.Parameters.AddWithValue("tableNames", names);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!primaryKeys.TryGetValue(key, out var columns))
                {
                    primaryKeys[key] = columns = [];
                }

                columns.Add(reader.GetString(2));
                if (extended) constraintNames[key] = reader.GetString(3);
            }

            return primaryKeys.ToDictionary(
                pair => pair.Key,
                pair => new SchemaPrimaryKey(pair.Value, extended ? constraintNames[pair.Key] : null));
        }
        finally
        {
            completionCheckpoint?.Invoke("ReadPrimaryKeysAsync");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<Dictionary<(string Schema, string Table), List<SchemaForeignKey>>> ReadForeignKeysAsync(
        NpgsqlConnection connection,
        string[] schemas,
        string[] names,
        bool extended,
        CancellationToken cancellationToken)
    {
        try
        {
            // Foreign keys arrive as one row per (constraint, position); the per-table list keeps the
            // constraint creation order of the query, and the constraint name is only the grouping
            // key; extended evidence also carries the catalog name.
            var foreignKeys = new Dictionary<(string, string), List<SchemaForeignKey>>();
            var constraints = new Dictionary<
                (string Schema, string Table, string Constraint),
                ConstraintRows>();
            var orderedKeys = new List<(string Schema, string Table, string Constraint)>();
            await using var command = connection.CreateCommand();
            command.CommandText = ForeignKeysScript;
            command.Parameters.AddWithValue("schemaNames", schemas);
            command.Parameters.AddWithValue("tableNames", names);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var tableKey = (reader.GetString(0), reader.GetString(1));
                var constraintKey = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (!constraints.TryGetValue(constraintKey, out var rows))
                {
                    constraints[constraintKey] = rows = new ConstraintRows(
                        reader.GetString(5), reader.GetString(6), MapDeleteRule(reader.GetString(3)[0]));
                    orderedKeys.Add(constraintKey);
                    if (!foreignKeys.TryGetValue(tableKey, out var tableForeignKeys))
                    {
                        foreignKeys[tableKey] = tableForeignKeys = [];
                    }

                    tableForeignKeys.Add(null!);
                    rows.TableIndex = tableForeignKeys.Count - 1;
                }

                rows.Columns.Add(reader.GetString(4));
                rows.ReferencedColumns.Add(reader.GetString(7));
            }

            foreach (var (schema, table, constraintName) in orderedKeys)
            {
                var rows = constraints[(schema, table, constraintName)];
                foreignKeys[(schema, table)][rows.TableIndex] = new SchemaForeignKey(
                    rows.Columns,
                    rows.ReferencedTable,
                    rows.ReferencedColumns,
                    rows.Rule,
                    rows.ReferencedSchema,
                    extended ? constraintName : null);
            }

            return foreignKeys;
        }
        finally
        {
            completionCheckpoint?.Invoke("ReadForeignKeysAsync");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<Dictionary<(string Schema, string Table), List<SchemaIndex>>> ReadIndexesAsync(
        NpgsqlConnection connection,
        string[] schemas,
        string[] names,
        bool extended,
        CancellationToken cancellationToken)
    {
        try
        {
            // Index rows retain ordinal key/include evidence in the explicit mode.
            var indexes = new Dictionary<(string, string), List<SchemaIndex>>();
            var orderedIndexKeys = new List<(string Schema, string Table, string Index)>();
            var indexColumns = new Dictionary<(string, string, string), List<string>>();
            var indexUniqueness = new Dictionary<(string, string, string), bool>();
            var includedColumns = new Dictionary<(string, string, string), List<string>>();
            var keyCounts = new Dictionary<(string, string, string), int>();
            await using var command = connection.CreateCommand();
            command.CommandText = extended ? ExtendedIndexesScript : IndexesScript;
            command.Parameters.AddWithValue("schemaNames", schemas);
            command.Parameters.AddWithValue("tableNames", names);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var tableKey = (reader.GetString(0), reader.GetString(1));
                var indexKey = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (!indexColumns.TryGetValue(indexKey, out var columns))
                {
                    indexColumns[indexKey] = columns = [];
                    indexUniqueness[indexKey] = reader.GetBoolean(3);
                    includedColumns[indexKey] = [];
                    if (extended) keyCounts[indexKey] = reader.GetInt16(5);
                    orderedIndexKeys.Add(indexKey);
                    indexes.TryAdd(tableKey, []);
                }

                if (!extended) columns.Add(reader.GetString(4));
                else if (!reader.IsDBNull(4))
                {
                    if (reader.GetInt64(6) <= keyCounts[indexKey]) columns.Add(reader.GetString(4));
                    else includedColumns[indexKey].Add(reader.GetString(4));
                }
            }

            foreach (var (schema, table, index) in orderedIndexKeys)
            {
                var key = (schema, table, index);
                indexes[(schema, table)].Add(extended
                    ? new SchemaIndex(indexColumns[key], indexUniqueness[key], index,
                        keyCounts[key], includedColumns[key])
                    : new SchemaIndex(indexColumns[key], indexUniqueness[key]));
            }

            return indexes;
        }
        finally
        {
            completionCheckpoint?.Invoke("ReadIndexesAsync");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static SchemaIdentityKind MapIdentityKind(string attidentity) => attidentity switch
    {
        "a" => SchemaIdentityKind.Always,
        "d" => SchemaIdentityKind.ByDefault,
        _ => SchemaIdentityKind.None
    };

    private static SchemaForeignKeyDeleteRule MapDeleteRule(char confdeltype) => confdeltype switch
    {
        'r' => SchemaForeignKeyDeleteRule.Restrict,
        'c' => SchemaForeignKeyDeleteRule.Cascade,
        'n' => SchemaForeignKeyDeleteRule.SetNull,
        'd' => SchemaForeignKeyDeleteRule.SetDefault,
        _ => SchemaForeignKeyDeleteRule.NoAction
    };

    private static SchemaEvidenceReadResult ClassifyFailure(
        Exception exception,
        string databaseName,
        string stepIdentifier)
    {
        if (FindsSqlState(exception, TargetMissingSqlState))
        {
            return SchemaEvidenceReadResult.TargetDatabaseMissing(databaseName);
        }

        return SchemaEvidenceReadResult.ReadFailed(stepIdentifier);
    }

    private static bool FindsSqlState(Exception exception, string sqlState)
    {
        var current = exception;
        while (current is not null)
        {
            if (current is PostgresException postgresException &&
                postgresException.SqlState == sqlState)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    /// <summary>Tracks which catalog object the read is currently on, for failure facts.</summary>
    private sealed class StepTracker(string identifier)
    {
        public string Identifier { get; set; } = identifier;
    }

    /// <summary>The accumulated rows of one foreign-key constraint.</summary>
    private sealed class ConstraintRows(
        string referencedSchema,
        string referencedTable,
        SchemaForeignKeyDeleteRule rule)
    {
        public List<string> Columns { get; } = [];

        public List<string> ReferencedColumns { get; } = [];

        public string ReferencedSchema { get; } = referencedSchema;

        public string ReferencedTable { get; } = referencedTable;

        public SchemaForeignKeyDeleteRule Rule { get; } = rule;

        public int TableIndex { get; set; }
    }
}
