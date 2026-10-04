using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ServiceMantle.Migration;

namespace ServiceMantle.Persistence.Relational.Migration;

/// <summary>
/// Derives the provider-neutral <see cref="ExpectedSchema"/> from an EF Core relational model:
/// every table-mapped table with its columns, primary key, foreign keys, and non-constraint
/// indexes, ready for <see cref="SchemaEvidenceComparer"/> comparison against a
/// <see cref="SchemaSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic: the same <see cref="IModel"/> always derives the same schema. Tables are
/// ordered by schema then name (ordinal); columns, key columns, and index columns keep the
/// model's own order. View mappings are not derived; the migrations history table is not part of
/// the application model and therefore never appears.
/// </para>
/// <para>
/// Identifier alignment is the caller's responsibility: <see cref="SchemaTable.Schema"/> and
/// <see cref="SchemaForeignKey.ReferencedSchema"/> carry exactly what the relational model
/// reports (null when no schema is configured), and the schema-evidence contract requires the
/// expected side to match the identifiers the snapshot reader produces (for example PostgreSQL's
/// <c>public</c> default schema), so consumers must configure their model's default or per-table
/// schema accordingly.
/// </para>
/// <para>
/// Identity derivation maps the SQL-standard identity strategies only: a property configured as
/// generated always / by default as identity derives <see cref="SchemaIdentityKind.Always"/> /
/// <see cref="SchemaIdentityKind.ByDefault"/>; every other strategy (serial columns, sequences,
/// hi-lo, provider-specific identity such as SQL Server <c>IDENTITY</c>, and
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never"/>) deliberately
/// derives <see cref="SchemaIdentityKind.None"/>, because the neutral model distinguishes only
/// SQL-standard identity columns.
/// </para>
/// <para>
/// Non-guarantees: no dimension outside the model is derived; type strings are the model's store
/// types compared exactly by the comparer with no dialect normalization; and
/// <see cref="SchemaColumn.HasStoredDefault"/> is true exactly when the model configures
/// <c>HasDefaultValue</c> or <c>HasDefaultValueSql</c> — a database-side default the model does
/// not configure (for example a serial sequence default) still derives false.
/// </para>
/// </remarks>
public static class EfCoreExpectedSchemaDerivation
{
    private const string ValueGenerationAnnotationSuffix = ":ValueGenerationStrategy";
    private const string IdentityAlwaysStrategy = "IdentityAlwaysColumn";
    private const string IdentityByDefaultStrategy = "IdentityByDefaultColumn";

    /// <summary>
    /// Derives the expected schema from the relational model of <paramref name="model"/>.
    /// </summary>
    /// <param name="model">The finalized EF Core relational model to derive from.</param>
    /// <returns>The isomorphic expected schema, deterministically ordered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A mapped column has no store type; the derivation requires a model produced by a
    /// relational provider.
    /// </exception>
    public static ExpectedSchema Derive(IModel model) => Derive(model, new EfCoreExpectedSchemaDerivationOptions());

    /// <summary>Derives explicitly enabled object names and caller-resolved INCLUDE evidence.</summary>
    /// <remarks>The include resolver receives relational indexes and must return store column names.
    /// Its purity and external effects are caller-owned. Invalid output or resolver exceptions fail safely.</remarks>
    public static ExpectedSchema Derive(IModel model, EfCoreExpectedSchemaDerivationOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var relationalModel = model.GetRelationalModel();
            var tables = relationalModel.Tables
                .OrderBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Name, StringComparer.Ordinal)
                .Select(table => DeriveTable(table, options))
                .ToList();

            return new ExpectedSchema(tables);
        }
        catch (Exception) when (options.IncludeExtendedObjectEvidence)
        {
            throw new InvalidOperationException("Extended schema evidence could not be derived.");
        }
    }

    private static SchemaTable DeriveTable(ITable table, EfCoreExpectedSchemaDerivationOptions options)
    {
        var columns = table.Columns.Select(DeriveColumn).ToList();
        var primaryKey = table.PrimaryKey is null
            ? null
            : new SchemaPrimaryKey(table.PrimaryKey.Columns.Select(column => column.Name).ToList(),
                options.IncludeExtendedObjectEvidence ? table.PrimaryKey.Name : null);
        var foreignKeys = table.ForeignKeyConstraints
            .Select(constraint => new SchemaForeignKey(
                constraint.Columns.Select(column => column.Name).ToList(),
                constraint.PrincipalTable.Name,
                constraint.PrincipalColumns.Select(column => column.Name).ToList(),
                MapDeleteRule(constraint.OnDeleteAction),
                constraint.PrincipalTable.Schema,
                options.IncludeExtendedObjectEvidence ? constraint.Name : null))
            .ToList();
        var indexes = table.Indexes
            .Select(index => DeriveIndex(index, options))
            .ToList();

        return new SchemaTable(table.Name, columns, primaryKey, foreignKeys, indexes, table.Schema);
    }

    private static SchemaIndex DeriveIndex(ITableIndex index, EfCoreExpectedSchemaDerivationOptions options)
    {
        var columns = index.Columns.Select(column => column.Name).ToList();
        if (!options.IncludeExtendedObjectEvidence) return new SchemaIndex(columns, index.IsUnique);
        var included = options.IncludeColumnResolver?.Invoke(index);
        return new SchemaIndex(columns, index.IsUnique, index.Name, columns.Count, included ?? []);
    }

    private static SchemaColumn DeriveColumn(IColumn column)
    {
        if (column.StoreType is null)
        {
            throw new InvalidOperationException(
                $"The column '{column.Name}' of table '{column.Table}' has no store type; " +
                "the expected-schema derivation requires a model produced by a relational provider.");
        }

        var identityKind = SchemaIdentityKind.None;
        foreach (var mapping in column.PropertyMappings)
        {
            var propertyKind = DeriveIdentityKind(mapping.Property);
            if (propertyKind == SchemaIdentityKind.Always)
            {
                identityKind = SchemaIdentityKind.Always;
                break;
            }

            if (propertyKind == SchemaIdentityKind.ByDefault)
            {
                identityKind = SchemaIdentityKind.ByDefault;
            }
        }

        var hasStoredDefault = column.DefaultValue is not null || column.DefaultValueSql is not null;
        return new SchemaColumn(column.Name, column.StoreType, column.IsNullable, identityKind, hasStoredDefault);
    }

    private static SchemaIdentityKind DeriveIdentityKind(IProperty property)
    {
        foreach (var annotation in property.GetAnnotations())
        {
            if (!annotation.Name.EndsWith(ValueGenerationAnnotationSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            switch (annotation.Value?.ToString())
            {
                case IdentityAlwaysStrategy:
                    return SchemaIdentityKind.Always;
                case IdentityByDefaultStrategy:
                    return SchemaIdentityKind.ByDefault;
            }
        }

        return SchemaIdentityKind.None;
    }

    private static SchemaForeignKeyDeleteRule MapDeleteRule(
        Microsoft.EntityFrameworkCore.Migrations.ReferentialAction action) => action switch
    {
        Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.NoAction =>
            SchemaForeignKeyDeleteRule.NoAction,
        Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.Restrict =>
            SchemaForeignKeyDeleteRule.Restrict,
        Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.Cascade =>
            SchemaForeignKeyDeleteRule.Cascade,
        Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.SetNull =>
            SchemaForeignKeyDeleteRule.SetNull,
        Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.SetDefault =>
            SchemaForeignKeyDeleteRule.SetDefault,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
