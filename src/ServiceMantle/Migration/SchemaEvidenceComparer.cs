namespace ServiceMantle.Migration;

/// <summary>
/// The pure-function comparer of the neutral schema evidence model: given an expected schema
/// and a snapshot, it returns every structured difference between them across the declared
/// comparison dimensions. It performs no I/O, reads no clock, keeps no state, and makes no
/// classification or accept/reject decision.
/// </summary>
/// <remarks>
/// <para>
/// Comparison dimensions: table existence (by schema-qualified name, ordinal comparison);
/// column existence (by name, ordinal) and each column's type string (exact match, no dialect
/// normalization), nullability, and identity kind; primary key existence and ordered columns;
/// foreign key existence, shape (columns, referenced schema/table/columns), and delete rule;
/// non-constraint index existence, columns, and uniqueness. Two foreign keys or indexes
/// correlate by shape, so a differing delete rule or uniqueness flag surfaces as one mismatch
/// difference carrying both sides. Every other structural change — check constraints, column
/// default values, triggers, views, permissions, stored defaults as reported by the evidence
/// flag — is outside the dimensions and produces no difference.
/// </para>
/// <para>
/// Deterministic order: differences come table by table in expected-schema order — missing
/// tables first; within a shared table, missing and mismatched columns in expected order, then
/// extra columns in snapshot order, then the primary key, then foreign keys in expected order
/// followed by unmatched snapshot foreign keys in snapshot order, then indexes the same way;
/// finally, extra tables in snapshot order.
/// </para>
/// <para>
/// Optional catalog names and index key details are compared only through the explicit-options
/// overload. That mode reserves same-name pairs before structural fallback. Unknown names do
/// not equal known names, and unmatched named objects retain their absent-side payload.
/// </para>
/// <para>
/// Non-guarantees: the list is complete only for the declared dimensions; it says nothing about
/// structure the models do not carry, and nothing about which differences a consumer may
/// safely accept or must reject.
/// </para>
/// </remarks>
public static class SchemaEvidenceComparer
{
    /// <summary>Compares the snapshot against the expected schema and returns every
    /// difference.</summary>
    /// <param name="snapshot">The actual structure as one reader observed it.</param>
    /// <param name="expected">The expected structure derived from the application's model.</param>
    /// <returns>Every structured difference across the declared dimensions; empty when the two
    /// sides agree on all of them.</returns>
    public static IReadOnlyList<SchemaDifference> Compare(
        SchemaSnapshot snapshot,
        ExpectedSchema expected) => Compare(snapshot, expected, new SchemaEvidenceComparisonOptions());

    /// <summary>Compares all original dimensions plus explicitly enabled catalog evidence.</summary>
    public static IReadOnlyList<SchemaDifference> Compare(
        SchemaSnapshot snapshot, ExpectedSchema expected, SchemaEvidenceComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(expected);

        var differences = new List<SchemaDifference>();
        var actualTablesByFullName = new Dictionary<string, SchemaTable>(StringComparer.Ordinal);
        foreach (var actualTable in snapshot.Tables)
        {
            actualTablesByFullName.Add(SchemaEvidenceModel.TableKey(actualTable), actualTable);
        }

        var expectedTableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expectedTable in expected.Tables)
        {
            var tableKey = SchemaEvidenceModel.TableKey(expectedTable);
            expectedTableNames.Add(tableKey);
            if (!actualTablesByFullName.TryGetValue(tableKey, out var actualTable))
            {
                differences.Add(SchemaDifference.MissingTable(expectedTable));
                continue;
            }

            CompareColumns(expectedTable, actualTable, differences);
            ComparePrimaryKeys(expectedTable, actualTable, differences, options);
            CompareForeignKeys(expectedTable, actualTable, differences, options);
            CompareIndexes(expectedTable, actualTable, differences, options);
        }

        foreach (var actualTable in snapshot.Tables)
        {
            if (!expectedTableNames.Contains(SchemaEvidenceModel.TableKey(actualTable)))
            {
                differences.Add(SchemaDifference.ExtraTable(actualTable));
            }
        }

        return differences;
    }

    private static void CompareColumns(
        SchemaTable expectedTable,
        SchemaTable actualTable,
        List<SchemaDifference> differences)
    {
        var actualColumnsByName = new Dictionary<string, SchemaColumn>(StringComparer.Ordinal);
        foreach (var actualColumn in actualTable.Columns)
        {
            actualColumnsByName.Add(actualColumn.Name, actualColumn);
        }

        var expectedColumnNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expectedColumn in expectedTable.Columns)
        {
            expectedColumnNames.Add(expectedColumn.Name);
            if (!actualColumnsByName.TryGetValue(expectedColumn.Name, out var actualColumn))
            {
                differences.Add(SchemaDifference.MissingColumn(expectedTable, expectedColumn));
                continue;
            }

            if (!string.Equals(expectedColumn.DataType, actualColumn.DataType, StringComparison.Ordinal))
            {
                differences.Add(SchemaDifference.ColumnTypeMismatch(expectedTable, expectedColumn, actualColumn));
            }

            if (expectedColumn.IsNullable != actualColumn.IsNullable)
            {
                differences.Add(SchemaDifference.ColumnNullabilityMismatch(
                    expectedTable, expectedColumn, actualColumn));
            }

            if (expectedColumn.IdentityKind != actualColumn.IdentityKind)
            {
                differences.Add(SchemaDifference.ColumnIdentityMismatch(
                    expectedTable, expectedColumn, actualColumn));
            }
        }

        foreach (var actualColumn in actualTable.Columns)
        {
            if (!expectedColumnNames.Contains(actualColumn.Name))
            {
                differences.Add(SchemaDifference.ExtraColumn(expectedTable, actualColumn));
            }
        }
    }

    private static void ComparePrimaryKeys(SchemaTable expectedTable, SchemaTable actualTable,
        List<SchemaDifference> differences, SchemaEvidenceComparisonOptions options)
    {
        var expected = expectedTable.PrimaryKey;
        var actual = actualTable.PrimaryKey;
        if (expected is null && actual is null) return;
        if (expected is null || actual is null ||
            !expected.Columns.SequenceEqual(actual.Columns, StringComparer.Ordinal))
            differences.Add(SchemaDifference.PrimaryKeyMismatch(expectedTable, expected, actual));
        if (options.CompareObjectNames && !string.Equals(expected?.Name, actual?.Name, StringComparison.Ordinal))
            differences.Add(SchemaDifference.ObjectMismatch(SchemaDifferenceKind.NameMismatch,
                expectedTable, expectedPrimaryKey: expected, actualPrimaryKey: actual));
    }

    private static void CompareForeignKeys(SchemaTable expectedTable, SchemaTable actualTable,
        List<SchemaDifference> differences, SchemaEvidenceComparisonOptions options)
    {
        foreach (var (expected, actual) in MatchObjects(expectedTable.ForeignKeys, actualTable.ForeignKeys,
                     value => value.Name, KeysEqual, ShapesEqual, options.CompareObjectNames))
        {
            if (expected is null || actual is null || !KeysEqual(expected, actual))
                differences.Add(SchemaDifference.ForeignKeyMismatch(expectedTable, expected, actual));
            if (options.CompareObjectNames && !string.Equals(expected?.Name, actual?.Name, StringComparison.Ordinal))
                differences.Add(SchemaDifference.ObjectMismatch(SchemaDifferenceKind.NameMismatch,
                    expectedTable, expectedForeignKey: expected, actualForeignKey: actual));
        }
    }

    private static void CompareIndexes(SchemaTable expectedTable, SchemaTable actualTable,
        List<SchemaDifference> differences, SchemaEvidenceComparisonOptions options)
    {
        foreach (var (expected, actual) in MatchObjects(expectedTable.Indexes, actualTable.Indexes,
                     value => value.Name, KeysEqual, ShapesEqual, options.CompareObjectNames))
        {
            if (expected is null || actual is null || !KeysEqual(expected, actual))
                differences.Add(SchemaDifference.IndexMismatch(expectedTable, expected, actual));
            if (options.CompareObjectNames && !string.Equals(expected?.Name, actual?.Name, StringComparison.Ordinal))
                differences.Add(SchemaDifference.ObjectMismatch(SchemaDifferenceKind.NameMismatch,
                    expectedTable, expectedIndex: expected, actualIndex: actual));
            if (!options.CompareIndexKeyDetails || expected is null || actual is null) continue;
            if (expected.KeyColumnCount != actual.KeyColumnCount)
                differences.Add(SchemaDifference.ObjectMismatch(SchemaDifferenceKind.IndexKeyColumnCountMismatch,
                    expectedTable, expectedIndex: expected, actualIndex: actual));
            if (!expected.IncludedColumns.SequenceEqual(actual.IncludedColumns, StringComparer.Ordinal))
                differences.Add(SchemaDifference.ObjectMismatch(SchemaDifferenceKind.IndexIncludedColumnsMismatch,
                    expectedTable, expectedIndex: expected, actualIndex: actual));
        }
    }

    private static IEnumerable<(T? Expected, T? Actual)> MatchObjects<T>(
        IReadOnlyList<T> expected, IReadOnlyList<T> actual, Func<T, string?> name,
        Func<T, T, bool> exact, Func<T, T, bool> shape, bool compareNames) where T : class
    {
        var matches = new T?[expected.Count];
        var remaining = new List<T>(actual);
        // Reserve every same-name pair before structural fallback can consume one of them.
        if (compareNames)
            for (var i = 0; i < expected.Count; i++)
                if (name(expected[i]) is string objectName)
                {
                    var match = FindFirstIndex(remaining, value => string.Equals(name(value), objectName, StringComparison.Ordinal));
                    if (match is int index) { matches[i] = remaining[index]; remaining.RemoveAt(index); }
                }
        for (var i = 0; i < expected.Count; i++)
            if (matches[i] is null)
            {
                var match = FindFirstIndex(remaining, value => exact(expected[i], value)) ??
                    FindFirstIndex(remaining, value => shape(expected[i], value));
                if (match is int index) { matches[i] = remaining[index]; remaining.RemoveAt(index); }
            }
        for (var i = 0; i < expected.Count; i++) yield return (expected[i], matches[i]);
        foreach (var value in remaining) yield return (null, value);
    }

    private static bool KeysEqual(SchemaForeignKey expected, SchemaForeignKey actual) =>
        ShapesEqual(expected, actual) && expected.DeleteRule == actual.DeleteRule;

    private static bool ShapesEqual(SchemaForeignKey expected, SchemaForeignKey actual) =>
        string.Equals(
            SchemaTable.ForeignKeyShape(expected),
            SchemaTable.ForeignKeyShape(actual),
            StringComparison.Ordinal);

    private static bool KeysEqual(SchemaIndex expected, SchemaIndex actual) =>
        ShapesEqual(expected, actual) && expected.IsUnique == actual.IsUnique;

    private static bool ShapesEqual(SchemaIndex expected, SchemaIndex actual) =>
        string.Equals(
            SchemaTable.IndexShape(expected),
            SchemaTable.IndexShape(actual),
            StringComparison.Ordinal);

    private static int? FindFirstIndex<T>(List<T> values, Func<T, bool> predicate)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (predicate(values[index]))
            {
                return index;
            }
        }

        return null;
    }
}
