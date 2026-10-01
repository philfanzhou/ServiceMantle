namespace ServiceMantle.Migration;

/// <summary>
/// The kind of one structural difference between an <see cref="ExpectedSchema"/> and a
/// <see cref="SchemaSnapshot"/>.
/// </summary>
public enum SchemaDifferenceKind
{
    /// <summary>The table exists in the expected schema but not in the snapshot.</summary>
    MissingTable = 0,

    /// <summary>The table exists in the snapshot but not in the expected schema.</summary>
    ExtraTable = 1,

    /// <summary>The column exists in the expected schema but not in the snapshot.</summary>
    MissingColumn = 2,

    /// <summary>The column exists in the snapshot but not in the expected schema.</summary>
    ExtraColumn = 3,

    /// <summary>The column's type string differs between expected and snapshot.</summary>
    ColumnTypeMismatch = 4,

    /// <summary>The column's nullability differs between expected and snapshot.</summary>
    ColumnNullabilityMismatch = 5,

    /// <summary>The column's identity kind differs between expected and snapshot.</summary>
    ColumnIdentityMismatch = 6,

    /// <summary>
    /// The table's primary key is absent on one side or its ordered columns differ.
    /// </summary>
    PrimaryKeyMismatch = 7,

    /// <summary>
    /// A foreign key is absent on one side or its shape or delete rule differs.
    /// </summary>
    ForeignKeyMismatch = 8,

    /// <summary>
    /// A non-constraint index is absent on one side or its uniqueness differs.
    /// </summary>
    IndexMismatch = 9
}

/// <summary>
/// One immutable structured difference produced by <see cref="SchemaEvidenceComparer"/>: the
/// kind, the identifiers of the object it is about, and the expected and actual evidence
/// values. Differences carry facts only — no classification, and no accept or reject decision.
/// </summary>
/// <remarks>
/// <para>
/// The populated payload pair follows the kind:
/// <list type="bullet">
/// <item><see cref="SchemaDifferenceKind.MissingTable"/> sets
/// <see cref="ExpectedTable"/>; <see cref="SchemaDifferenceKind.ExtraTable"/> sets
/// <see cref="ActualTable"/>.</item>
/// <item><see cref="SchemaDifferenceKind.MissingColumn"/> sets <see cref="ExpectedColumn"/>;
/// <see cref="SchemaDifferenceKind.ExtraColumn"/> sets <see cref="ActualColumn"/>; the column
/// mismatches set both.</item>
/// <item><see cref="SchemaDifferenceKind.PrimaryKeyMismatch"/>,
/// <see cref="SchemaDifferenceKind.ForeignKeyMismatch"/>, and
/// <see cref="SchemaDifferenceKind.IndexMismatch"/> set the matching pair, with null on the
/// absent side.</item>
/// </list>
/// </para>
/// <para>
/// Backfill evidence: a <see cref="SchemaDifferenceKind.MissingColumn"/> difference carries the
/// full expected column, so its <see cref="SchemaColumn.IsNullable"/> and
/// <see cref="SchemaColumn.HasStoredDefault"/> values are the judgment material for safe
/// backfill decisions; the comparer itself decides nothing.
/// </para>
/// <para>
/// Every payload value is one of the neutral model objects and contains identifiers and
/// structural facts only — never SQL text, connection values, or exception messages.
/// </para>
/// </remarks>
public sealed class SchemaDifference
{
    internal SchemaDifference(
        SchemaDifferenceKind kind,
        string? tableSchema,
        string tableName,
        string? columnName,
        SchemaTable? expectedTable,
        SchemaTable? actualTable,
        SchemaColumn? expectedColumn,
        SchemaColumn? actualColumn,
        SchemaPrimaryKey? expectedPrimaryKey,
        SchemaPrimaryKey? actualPrimaryKey,
        SchemaForeignKey? expectedForeignKey,
        SchemaForeignKey? actualForeignKey,
        SchemaIndex? expectedIndex,
        SchemaIndex? actualIndex)
    {
        Kind = kind;
        TableSchema = tableSchema;
        TableName = tableName;
        ColumnName = columnName;
        ExpectedTable = expectedTable;
        ActualTable = actualTable;
        ExpectedColumn = expectedColumn;
        ActualColumn = actualColumn;
        ExpectedPrimaryKey = expectedPrimaryKey;
        ActualPrimaryKey = actualPrimaryKey;
        ExpectedForeignKey = expectedForeignKey;
        ActualForeignKey = actualForeignKey;
        ExpectedIndex = expectedIndex;
        ActualIndex = actualIndex;
    }

    /// <summary>Gets the difference kind.</summary>
    public SchemaDifferenceKind Kind { get; }

    /// <summary>Gets the schema of the table the difference is about, or null when the table
    /// carries no schema.</summary>
    public string? TableSchema { get; }

    /// <summary>Gets the name of the table the difference is about.</summary>
    public string TableName { get; }

    /// <summary>
    /// Gets the column name for column differences, or null for table, primary key, foreign
    /// key, and index differences.
    /// </summary>
    public string? ColumnName { get; }

    /// <summary>Gets the expected table, for table differences; otherwise null.</summary>
    public SchemaTable? ExpectedTable { get; }

    /// <summary>Gets the actual table, for table differences; otherwise null.</summary>
    public SchemaTable? ActualTable { get; }

    /// <summary>Gets the expected column, for column differences; otherwise null.</summary>
    public SchemaColumn? ExpectedColumn { get; }

    /// <summary>Gets the actual column, for column differences; otherwise null.</summary>
    public SchemaColumn? ActualColumn { get; }

    /// <summary>Gets the expected primary key, for primary key differences; otherwise null.</summary>
    public SchemaPrimaryKey? ExpectedPrimaryKey { get; }

    /// <summary>Gets the actual primary key, for primary key differences; otherwise null.</summary>
    public SchemaPrimaryKey? ActualPrimaryKey { get; }

    /// <summary>Gets the expected foreign key, for foreign key differences; otherwise null.</summary>
    public SchemaForeignKey? ExpectedForeignKey { get; }

    /// <summary>Gets the actual foreign key, for foreign key differences; otherwise null.</summary>
    public SchemaForeignKey? ActualForeignKey { get; }

    /// <summary>Gets the expected index, for index differences; otherwise null.</summary>
    public SchemaIndex? ExpectedIndex { get; }

    /// <summary>Gets the actual index, for index differences; otherwise null.</summary>
    public SchemaIndex? ActualIndex { get; }

    /// <summary>Returns the identifier-only difference summary.</summary>
    public override string ToString() =>
        ColumnName is null
            ? $"SchemaDifference(Kind={Kind}, Table={TableName})"
            : $"SchemaDifference(Kind={Kind}, Table={TableName}, Column={ColumnName})";

    internal static SchemaDifference MissingTable(SchemaTable expectedTable) =>
        new(SchemaDifferenceKind.MissingTable, expectedTable.Schema, expectedTable.Name, null,
            expectedTable, null, null, null, null, null, null, null, null, null);

    internal static SchemaDifference ExtraTable(SchemaTable actualTable) =>
        new(SchemaDifferenceKind.ExtraTable, actualTable.Schema, actualTable.Name, null,
            null, actualTable, null, null, null, null, null, null, null, null);

    internal static SchemaDifference MissingColumn(
        SchemaTable table,
        SchemaColumn expectedColumn) =>
        new(SchemaDifferenceKind.MissingColumn, table.Schema, table.Name, expectedColumn.Name,
            null, null, expectedColumn, null, null, null, null, null, null, null);

    internal static SchemaDifference ExtraColumn(SchemaTable table, SchemaColumn actualColumn) =>
        new(SchemaDifferenceKind.ExtraColumn, table.Schema, table.Name, actualColumn.Name,
            null, null, null, actualColumn, null, null, null, null, null, null);

    internal static SchemaDifference ColumnTypeMismatch(
        SchemaTable table,
        SchemaColumn expectedColumn,
        SchemaColumn actualColumn) =>
        new(SchemaDifferenceKind.ColumnTypeMismatch, table.Schema, table.Name, expectedColumn.Name,
            null, null, expectedColumn, actualColumn, null, null, null, null, null, null);

    internal static SchemaDifference ColumnNullabilityMismatch(
        SchemaTable table,
        SchemaColumn expectedColumn,
        SchemaColumn actualColumn) =>
        new(SchemaDifferenceKind.ColumnNullabilityMismatch, table.Schema, table.Name,
            expectedColumn.Name, null, null, expectedColumn, actualColumn, null, null,
            null, null, null, null);

    internal static SchemaDifference ColumnIdentityMismatch(
        SchemaTable table,
        SchemaColumn expectedColumn,
        SchemaColumn actualColumn) =>
        new(SchemaDifferenceKind.ColumnIdentityMismatch, table.Schema, table.Name,
            expectedColumn.Name, null, null, expectedColumn, actualColumn, null, null,
            null, null, null, null);

    internal static SchemaDifference PrimaryKeyMismatch(
        SchemaTable table,
        SchemaPrimaryKey? expectedPrimaryKey,
        SchemaPrimaryKey? actualPrimaryKey) =>
        new(SchemaDifferenceKind.PrimaryKeyMismatch, table.Schema, table.Name, null,
            null, null, null, null, expectedPrimaryKey, actualPrimaryKey, null, null,
            null, null);

    internal static SchemaDifference ForeignKeyMismatch(
        SchemaTable table,
        SchemaForeignKey? expectedForeignKey,
        SchemaForeignKey? actualForeignKey) =>
        new(SchemaDifferenceKind.ForeignKeyMismatch, table.Schema, table.Name, null,
            null, null, null, null, null, null, expectedForeignKey, actualForeignKey,
            null, null);

    internal static SchemaDifference IndexMismatch(
        SchemaTable table,
        SchemaIndex? expectedIndex,
        SchemaIndex? actualIndex) =>
        new(SchemaDifferenceKind.IndexMismatch, table.Schema, table.Name, null,
            null, null, null, null, null, null, null, null, expectedIndex, actualIndex);
}
