using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Ddl;

public sealed record DdlStatement(string Sql, bool Destructive, string Description);

public sealed record RoutineDefinition(string Schema, string Name, string Kind, string Body);

public interface IDdlWriter
{
    IReadOnlyList<DdlStatement> CreateTable(TableDefinition table);
    IReadOnlyList<DdlStatement> AlterTable(TableDefinition before, TableChange change);
    IReadOnlyList<DdlStatement> DropTable(string schema, string name);
    IReadOnlyList<DdlStatement> CreateIndex(string schema, string table, IndexDefinition index);
    IReadOnlyList<DdlStatement> DropIndex(string schema, string table, string name);
    IReadOnlyList<DdlStatement> Rename(SchemaNodeRef target, string newName);
    IReadOnlyList<DdlStatement> CreateOrReplaceRoutine(RoutineDefinition routine);
}

/// The parts every engine shares. Subclasses override only what their syntax actually differs on,
/// which keeps each engine's file short enough to read in one sitting.
public abstract class DdlWriterBase(SqlDialect dialect) : IDdlWriter
{
    protected SqlDialect Dialect { get; } = dialect;

    protected virtual bool SupportsIfExists => true;
    protected virtual bool SupportsColumnComments => true;
    protected virtual bool SupportsAddConstraint => true;

    protected string Qualify(string schema, string name) =>
        schema is { Length: > 0 }
            ? $"{Dialect.QuoteIdentifier(schema)}.{Dialect.QuoteIdentifier(name)}"
            : Dialect.QuoteIdentifier(name);

    public abstract string MapType(string neutralType);

    /// Engines differ on what may be a key column. A writer that needs a bounded text type for an
    /// indexed column adjusts the definition here rather than emitting DDL the engine rejects.
    protected virtual TableDefinition Normalize(TableDefinition table) => table;

    /// Columns that any index or unique constraint uses as a key.
    protected static HashSet<string> KeyColumns(TableDefinition table) =>
        table.Indexes.SelectMany(i => i.Columns)
            .Concat(table.Constraints
                .Where(c => c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique)
                .SelectMany(c => c.Columns))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public virtual IReadOnlyList<DdlStatement> CreateTable(TableDefinition definition)
    {
        var table = Normalize(definition);
        var lines = new List<string>();

        foreach (var column in table.Columns) lines.Add("  " + ColumnClause(column));

        foreach (var constraint in table.Constraints)
            if (ConstraintClause(constraint) is { Length: > 0 } clause) lines.Add("  " + clause);

        var statements = new List<DdlStatement>
        {
            new($"CREATE TABLE {Qualify(table.Schema, table.Name)} (\n{string.Join(",\n", lines)}\n);",
                false, $"create table {table.Name}"),
        };

        foreach (var index in table.Indexes)
            statements.AddRange(CreateIndex(table.Schema, table.Name, index));

        if (SupportsColumnComments)
            statements.AddRange(CommentStatements(table));

        return statements;
    }

    public virtual IReadOnlyList<DdlStatement> AlterTable(TableDefinition before, TableChange change)
    {
        var table = Qualify(before.Schema, before.Name);
        var statements = new List<DdlStatement>();

        foreach (var constraint in change.DroppedConstraints)
            statements.Add(new DdlStatement(
                $"ALTER TABLE {table} DROP CONSTRAINT {Dialect.QuoteIdentifier(constraint.Name)};",
                true, $"drop constraint {constraint.Name}"));

        foreach (var index in change.DroppedIndexes)
            statements.AddRange(DropIndex(before.Schema, before.Name, index.Name));

        foreach (var rename in change.RenamedColumns)
            statements.AddRange(RenameColumn(before, rename.Before!.Name, rename.Column.Name));

        foreach (var column in change.AddedColumns)
            statements.Add(new DdlStatement($"ALTER TABLE {table} ADD {ColumnClause(column)};",
                false, $"add column {column.Name}"));

        foreach (var altered in change.AlteredColumns)
            statements.AddRange(AlterColumn(before, altered.Column));

        foreach (var column in change.DroppedColumns)
            statements.Add(new DdlStatement(
                $"ALTER TABLE {table} DROP COLUMN {Dialect.QuoteIdentifier(column.Name)};",
                true, $"drop column {column.Name}"));

        foreach (var index in change.AddedIndexes)
            statements.AddRange(CreateIndex(before.Schema, before.Name, index));

        foreach (var constraint in change.AddedConstraints)
            if (SupportsAddConstraint && ConstraintClause(constraint) is { Length: > 0 } clause)
                statements.Add(new DdlStatement($"ALTER TABLE {table} ADD {clause};",
                    false, $"add constraint {constraint.Name}"));

        return statements;
    }

    public virtual IReadOnlyList<DdlStatement> DropTable(string schema, string name) =>
        [new DdlStatement($"DROP TABLE {(SupportsIfExists ? "IF EXISTS " : "")}{Qualify(schema, name)};",
            true, $"drop table {name}")];

    public virtual IReadOnlyList<DdlStatement> CreateIndex(string schema, string table, IndexDefinition index)
    {
        var unique = index.Unique ? "UNIQUE " : "";
        var columns = string.Join(", ", index.Columns.Select(Dialect.QuoteIdentifier));
        var filter = index.Filter is { Length: > 0 } ? $" WHERE {index.Filter}" : "";
        var include = index.IncludeColumns is { Count: > 0 }
            ? $" INCLUDE ({string.Join(", ", index.IncludeColumns.Select(Dialect.QuoteIdentifier))})"
            : "";

        return [new DdlStatement(
            $"CREATE {unique}INDEX {Dialect.QuoteIdentifier(index.Name)} ON {Qualify(schema, table)} ({columns}){include}{filter};",
            false, $"create index {index.Name}")];
    }

    public virtual IReadOnlyList<DdlStatement> DropIndex(string schema, string table, string name) =>
        [new DdlStatement($"DROP INDEX {(SupportsIfExists ? "IF EXISTS " : "")}{Qualify(schema, name)};",
            true, $"drop index {name}")];

    public virtual IReadOnlyList<DdlStatement> Rename(SchemaNodeRef target, string newName)
    {
        var schema = target.Path.Count > 1 ? target.Path[0] : "";
        return [new DdlStatement(
            $"ALTER TABLE {Qualify(schema, target.Name)} RENAME TO {Dialect.QuoteIdentifier(newName)};",
            false, $"rename {target.Name} to {newName}")];
    }

    public virtual IReadOnlyList<DdlStatement> CreateOrReplaceRoutine(RoutineDefinition routine) =>
        [new DdlStatement(routine.Body.TrimEnd().TrimEnd(';') + ";", false,
            $"create or replace {routine.Kind} {routine.Name}")];

    protected virtual IReadOnlyList<DdlStatement> RenameColumn(TableDefinition before, string from, string to) =>
        [new DdlStatement(
            $"ALTER TABLE {Qualify(before.Schema, before.Name)} RENAME COLUMN " +
            $"{Dialect.QuoteIdentifier(from)} TO {Dialect.QuoteIdentifier(to)};",
            false, $"rename column {from} to {to}")];

    protected abstract IReadOnlyList<DdlStatement> AlterColumn(TableDefinition before, ColumnDefinition column);

    protected virtual string ColumnClause(ColumnDefinition column)
    {
        var parts = new List<string> { Dialect.QuoteIdentifier(column.Name), MapType(column.Type) };
        if (column.Identity) parts.Add(IdentityClause);
        if (!column.Nullable) parts.Add("NOT NULL");
        if (column.Default is { Length: > 0 }) parts.Add($"DEFAULT {column.Default}");
        return string.Join(" ", parts);
    }

    protected virtual string IdentityClause => "GENERATED BY DEFAULT AS IDENTITY";

    protected virtual string ConstraintClause(ConstraintDefinition constraint)
    {
        var name = $"CONSTRAINT {Dialect.QuoteIdentifier(constraint.Name)} ";
        var columns = string.Join(", ", constraint.Columns.Select(Dialect.QuoteIdentifier));

        return constraint.Kind switch
        {
            ConstraintKind.PrimaryKey => $"{name}PRIMARY KEY ({columns})",
            ConstraintKind.Unique => $"{name}UNIQUE ({columns})",
            ConstraintKind.Check => $"{name}CHECK ({constraint.Expression})",
            ConstraintKind.ForeignKey =>
                $"{name}FOREIGN KEY ({columns}) REFERENCES {Dialect.QuoteIdentifier(constraint.ReferencedTable!)} " +
                $"({string.Join(", ", (constraint.ReferencedColumns ?? []).Select(Dialect.QuoteIdentifier))}) " +
                $"ON DELETE {constraint.OnDelete} ON UPDATE {constraint.OnUpdate}",
            _ => "",
        };
    }

    protected virtual IEnumerable<DdlStatement> CommentStatements(TableDefinition table)
    {
        foreach (var column in table.Columns.Where(c => c.Comment is { Length: > 0 }))
            yield return new DdlStatement(
                $"COMMENT ON COLUMN {Qualify(table.Schema, table.Name)}.{Dialect.QuoteIdentifier(column.Name)} " +
                $"IS {Dialect.QuoteLiteral(column.Comment!)};",
                false, $"comment on {column.Name}");

        if (table.Comment is { Length: > 0 })
            yield return new DdlStatement(
                $"COMMENT ON TABLE {Qualify(table.Schema, table.Name)} IS {Dialect.QuoteLiteral(table.Comment)};",
                false, "comment on table");
    }
}
