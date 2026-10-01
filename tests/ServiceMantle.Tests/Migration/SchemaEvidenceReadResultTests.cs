using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Tests.Migration;

/// <summary>
/// Unit tests for <see cref="SchemaEvidenceReadResult"/>: the two failure facts stay distinct,
/// messages are rendered from one identifier plus a fixed template, and identifier validation
/// holds.
/// </summary>
public sealed class SchemaEvidenceReadResultTests
{
    // Ambient hostile material that must never reach a message: the factories accept exactly
    // one validated identifier, so none of this can enter through any path.
    private const string HostileSql =
        "SELECT * FROM pg_catalog.pg_class; Host=primary.db.internal; Password=top-secret-value;";

    [Fact]
    public void Success_carries_the_complete_evidence()
    {
        var snapshot = new SchemaSnapshot(
        [
            new SchemaTable(
                "orders",
                [new SchemaColumn("id", "integer", isNullable: false)]),
        ]);

        var result = SchemaEvidenceReadResult.Success(["20260101000000_InitialCreate"], snapshot);

        Assert.Equal(SchemaEvidenceReadState.Succeeded, result.State);
        Assert.Equal(["20260101000000_InitialCreate"], result.AppliedMigrationIds);
        Assert.Same(snapshot, result.Snapshot);
        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public void Target_missing_and_read_failed_are_two_distinct_facts()
    {
        var missing = SchemaEvidenceReadResult.TargetDatabaseMissing("catalog");
        var failed = SchemaEvidenceReadResult.ReadFailed("catalog.public.orders");

        Assert.Equal(SchemaEvidenceReadState.TargetDatabaseMissing, missing.State);
        Assert.NotEqual(failed.State, missing.State);
        Assert.Equal(SchemaEvidenceReadState.ReadFailed, failed.State);
        Assert.Null(missing.Snapshot);
        Assert.Null(failed.Snapshot);
        Assert.Empty(missing.AppliedMigrationIds);
        Assert.Empty(failed.AppliedMigrationIds);
    }

    [Fact]
    public void Messages_contain_identifiers_only_no_sql_and_no_connection_values()
    {
        var missing = SchemaEvidenceReadResult.TargetDatabaseMissing("catalog");
        var failed = SchemaEvidenceReadResult.ReadFailed("catalog.public.orders");

        Assert.Equal("The target database 'catalog' does not exist.", missing.Message);
        Assert.Equal("The schema evidence of 'catalog.public.orders' could not be read.", failed.Message);

        foreach (var message in new[] { missing.Message, failed.Message, missing.ToString(), failed.ToString() })
        {
            Assert.DoesNotContain("SELECT", message, StringComparison.Ordinal);
            Assert.DoesNotContain("pg_catalog", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Host=", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Password", message, StringComparison.Ordinal);
            Assert.DoesNotContain("top-secret-value", message, StringComparison.Ordinal);
            Assert.DoesNotContain(HostileSql, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Identifier_and_argument_validation_is_enforced()
    {
        var snapshot = new SchemaSnapshot(
        [
            new SchemaTable("orders", [new SchemaColumn("id", "integer", isNullable: false)]),
        ]);

        Assert.Throws<ArgumentNullException>(
            () => SchemaEvidenceReadResult.Success(null!, snapshot));
        Assert.Throws<ArgumentNullException>(
            () => SchemaEvidenceReadResult.Success(["20260101000000_InitialCreate"], null!));
        Assert.Throws<ArgumentException>(
            () => SchemaEvidenceReadResult.Success(["   "], snapshot));
        Assert.Throws<ArgumentException>(
            () => SchemaEvidenceReadResult.Success(["id\u0000with-control"], snapshot));
        Assert.Throws<ArgumentException>(
            () => SchemaEvidenceReadResult.TargetDatabaseMissing(string.Empty));
        Assert.Throws<ArgumentException>(
            () => SchemaEvidenceReadResult.ReadFailed(new string('x', 129)));
    }
}

/// <summary>
/// Unit tests for the neutral schema evidence model's immutability and validation rules.
/// </summary>
public sealed class SchemaEvidenceModelTests
{
    [Fact]
    public void Model_lists_are_copied_and_cannot_be_mutated_through_the_source()
    {
        var columns = new List<SchemaColumn> { new("id", "integer", isNullable: false) };
        var table = new SchemaTable("orders", columns);

        columns.Add(new SchemaColumn("extra", "text", isNullable: true));

        Assert.Single(table.Columns);
        Assert.Throws<NotSupportedException>(() => ((ICollection<SchemaColumn>)table.Columns).Add(
            new SchemaColumn("more", "text", isNullable: true)));
    }

    [Fact]
    public void Duplicate_tables_columns_keys_and_indexes_are_rejected()
    {
        var column = new SchemaColumn("id", "integer", isNullable: false);
        var table = new SchemaTable("orders", [column]);

        Assert.Throws<ArgumentException>(() => new SchemaSnapshot([table, table]));
        Assert.Throws<ArgumentException>(() => new ExpectedSchema([table, table]));
        Assert.Throws<ArgumentException>(() => new SchemaTable(
            "orders",
            [column, new SchemaColumn("id", "bigint", isNullable: false)]));
        Assert.Throws<ArgumentException>(() => new SchemaTable(
            "orders",
            [column],
            indexes:
            [
                new SchemaIndex(["id"], isUnique: false),
                new SchemaIndex(["id"], isUnique: false),
            ]));
        Assert.Throws<ArgumentException>(() => new SchemaTable(
            "orders",
            [column],
            foreignKeys:
            [
                new SchemaForeignKey(["id"], "tenants", ["id"], SchemaForeignKeyDeleteRule.Cascade),
                new SchemaForeignKey(["id"], "tenants", ["id"], SchemaForeignKeyDeleteRule.Cascade),
            ]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name\u0007with-control")]
    public void Identifiers_are_validated(string invalidIdentifier)
    {
        Assert.Throws<ArgumentException>(
            () => new SchemaColumn(invalidIdentifier, "integer", isNullable: false));
        Assert.Throws<ArgumentException>(
            () => new SchemaTable(invalidIdentifier, [new SchemaColumn("id", "integer", false)]));
        Assert.Throws<ArgumentException>(
            () => new SchemaTable("orders", [new SchemaColumn("id", invalidIdentifier, false)]));
        Assert.Throws<ArgumentException>(
            () => new SchemaTable(
                "orders",
                [new SchemaColumn("id", "integer", false)],
                schema: invalidIdentifier));
    }

    [Fact]
    public void Empty_column_lists_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SchemaPrimaryKey([]));
        Assert.Throws<ArgumentException>(() => new SchemaIndex([], isUnique: false));
        Assert.Throws<ArgumentException>(
            () => new SchemaForeignKey([], "tenants", ["id"], SchemaForeignKeyDeleteRule.Cascade));
        Assert.Throws<ArgumentException>(
            () => new SchemaTable("orders", Array.Empty<SchemaColumn>()));
    }

    [Fact]
    public void Undefined_enum_values_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SchemaColumn("id", "integer", false, (SchemaIdentityKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SchemaForeignKey(
                ["id"], "tenants", ["id"], (SchemaForeignKeyDeleteRule)99));
    }
}
