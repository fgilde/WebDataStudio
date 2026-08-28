using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Drivers.SqlServer;

namespace WebDataStudio.Server.Ddl;

public sealed class PostgreSqlDdlWriter() : DdlWriterBase(new PostgreSqlDialect())
{
    /// PostgreSQL has no full-text index type: it indexes the tsvector of the columns with GIN,
    /// which is what `to_tsvector(...) @@ to_tsquery(...)` searches against.
    public override IReadOnlyList<DdlStatement> CreateIndex(string schema, string table, IndexDefinition index)
    {
        if (!index.FullText) return base.CreateIndex(schema, table, index);

        var expression = string.Join(" || ' ' || ",
            index.Columns.Select(c => $"coalesce({Dialect.QuoteIdentifier(c)}, '')"));

        return [new DdlStatement(
            $"CREATE INDEX {Dialect.QuoteIdentifier(index.Name)} ON {Qualify(schema, table)} " +
            $"USING gin (to_tsvector('simple', {expression}));",
            false, $"create full-text index {index.Name}")];
    }


    public override string MapType(string neutralType) => neutralType.ToLowerInvariant() switch
    {
        "text" => "TEXT",
        "int" or "integer" => "INTEGER",
        "bigint" => "BIGINT",
        "smallint" => "SMALLINT",
        "bool" or "boolean" => "BOOLEAN",
        "float" or "real" => "REAL",
        "double" => "DOUBLE PRECISION",
        "date" => "DATE",
        "timestamp" => "TIMESTAMP",
        "uuid" => "UUID",
        "json" => "JSONB",
        "blob" => "BYTEA",
        // An unmapped type is passed through: the user knows their engine better than this table.
        _ => neutralType.ToUpperInvariant(),
    };

    protected override IReadOnlyList<DdlStatement> AlterColumn(TableDefinition before, ColumnDefinition column)
    {
        var table = Qualify(before.Schema, before.Name);
        var name = Dialect.QuoteIdentifier(column.Name);

        return
        [
            new DdlStatement($"ALTER TABLE {table} ALTER COLUMN {name} TYPE {MapType(column.Type)};",
                false, $"change type of {column.Name}"),
            new DdlStatement(
                $"ALTER TABLE {table} ALTER COLUMN {name} {(column.Nullable ? "DROP NOT NULL" : "SET NOT NULL")};",
                false, $"change nullability of {column.Name}"),
            new DdlStatement(
                column.Default is { Length: > 0 }
                    ? $"ALTER TABLE {table} ALTER COLUMN {name} SET DEFAULT {column.Default};"
                    : $"ALTER TABLE {table} ALTER COLUMN {name} DROP DEFAULT;",
                false, $"change default of {column.Name}"),
        ];
    }
}

public sealed class MySqlDdlWriter() : DdlWriterBase(new MySqlDialect())
{
    protected override bool SupportsColumnComments => false; // MySQL carries comments inline instead

    /// MySQL has a FULLTEXT index of its own, searched with MATCH … AGAINST.
    public override IReadOnlyList<DdlStatement> CreateIndex(string schema, string table, IndexDefinition index)
    {
        if (!index.FullText) return base.CreateIndex(schema, table, index);

        var columns = string.Join(", ", index.Columns.Select(Dialect.QuoteIdentifier));

        return [new DdlStatement(
            $"CREATE FULLTEXT INDEX {Dialect.QuoteIdentifier(index.Name)} " +
            $"ON {Qualify(schema, table)} ({columns});",
            false, $"create full-text index {index.Name}")];
    }

    public override string MapType(string neutralType) => neutralType.ToLowerInvariant() switch
    {
        "text" => "TEXT",
        "int" or "integer" => "INT",
        "bigint" => "BIGINT",
        "smallint" => "SMALLINT",
        "bool" or "boolean" => "TINYINT(1)",
        "float" or "real" => "FLOAT",
        "double" => "DOUBLE",
        "date" => "DATE",
        "timestamp" => "DATETIME",
        "uuid" => "CHAR(36)",
        "json" => "JSON",
        "blob" => "BLOB",
        _ => neutralType.ToUpperInvariant(),
    };

    protected override string IdentityClause => "AUTO_INCREMENT";

    /// MySQL refuses TEXT as a key column without a prefix length. A bounded VARCHAR is what the
    /// user meant anyway, and it keeps the index definition readable.
    protected override TableDefinition Normalize(TableDefinition table)
    {
        var keys = KeyColumns(table);

        return table with
        {
            Columns = table.Columns
                .Select(c => keys.Contains(c.Name) && c.Type.Equals("text", StringComparison.OrdinalIgnoreCase)
                    ? c with { Type = "varchar(255)" }
                    : c)
                .ToList(),
        };
    }

    protected override string ColumnClause(ColumnDefinition column)
    {
        var clause = base.ColumnClause(column);
        return column.Comment is { Length: > 0 }
            ? $"{clause} COMMENT {Dialect.QuoteLiteral(column.Comment)}"
            : clause;
    }

    protected override IReadOnlyList<DdlStatement> RenameColumn(TableDefinition before, string from, string to)
    {
        var column = before.Columns.First(c => c.Name.Equals(from, StringComparison.OrdinalIgnoreCase));
        var renamed = column with { Name = to };

        // CHANGE works on every MySQL and MariaDB version; RENAME COLUMN only from 8.0 on.
        return [new DdlStatement(
            $"ALTER TABLE {Qualify(before.Schema, before.Name)} CHANGE {Dialect.QuoteIdentifier(from)} " +
            $"{ColumnClause(renamed)};",
            false, $"rename column {from} to {to}")];
    }

    protected override IReadOnlyList<DdlStatement> AlterColumn(TableDefinition before, ColumnDefinition column) =>
        [new DdlStatement(
            $"ALTER TABLE {Qualify(before.Schema, before.Name)} MODIFY {ColumnClause(column)};",
            false, $"alter column {column.Name}")];

    public override IReadOnlyList<DdlStatement> DropIndex(string schema, string table, string name) =>
        [new DdlStatement(
            $"DROP INDEX {Dialect.QuoteIdentifier(name)} ON {Qualify(schema, table)};",
            true, $"drop index {name}")];

    // --- objects other than tables ----------------------------------------------------------------

    protected override bool HasSchemas => false;

    /// MySQL has no sequences, and no schemas of its own — a schema is a database there, and the
    /// studio already has a dialog for those.
    public override IReadOnlyList<DdlStatement> CreateSequence(SequenceDefinition sequence) =>
        throw new NotSupportedException("MySQL has no sequences; an AUTO_INCREMENT column is the equivalent");

    public override IReadOnlyList<DdlStatement> AlterSequence(SequenceDefinition sequence) =>
        CreateSequence(sequence);

    public override IReadOnlyList<DdlStatement> CreateSchema(string name) =>
        throw new NotSupportedException("in MySQL a schema is a database - use New database...");

    public override IReadOnlyList<DdlStatement> DropSchema(string name, bool cascade) =>
        throw new NotSupportedException("in MySQL a schema is a database - use Drop database...");

    /// A routine cannot be replaced in place, so it is dropped and written again. Both statements
    /// are in the preview, which is the point of previewing.
    public override IReadOnlyList<DdlStatement> CreateOrReplaceRoutine(RoutineDefinition routine)
    {
        var keyword = routine.Kind.Equals("function", StringComparison.OrdinalIgnoreCase)
            ? "FUNCTION"
            : routine.Kind.Equals("trigger", StringComparison.OrdinalIgnoreCase) ? "TRIGGER" : "PROCEDURE";

        return
        [
            new DdlStatement($"DROP {keyword} IF EXISTS {Qualify(routine.Schema, routine.Name)};",
                true, $"drop {keyword.ToLowerInvariant()} {routine.Name}"),
            new DdlStatement(Body(routine.Body) + ";", false,
                $"create {keyword.ToLowerInvariant()} {routine.Name}"),
        ];
    }

    /// Only a table carries a description here, and it is part of ALTER TABLE rather than a
    /// statement of its own.
    public override IReadOnlyList<DdlStatement> Comment(SchemaNodeRef target, string? text)
    {
        if (target.Kind != SchemaNodeKind.Table)
            throw new NotSupportedException(
                "MySQL keeps a description on tables only - the studio's own notes cover the rest");

        var schema = target.Path.Count > 1 ? target.Path[0] : "";

        return [new DdlStatement(
            $"ALTER TABLE {Qualify(schema, target.Name)} COMMENT = {Dialect.QuoteLiteral(text ?? "")};",
            false, $"comment on {target.Name}")];
    }

    public override IReadOnlyList<DdlStatement> SetTriggerEnabled(SchemaNodeRef trigger, bool enabled) =>
        throw new NotSupportedException("MySQL cannot switch a trigger off; drop it or guard its body");

    /// `DROP TRIGGER x` - the table it hangs on is not part of it.
    public override IReadOnlyList<DdlStatement> DropObject(SchemaNodeRef target) =>
        target.Kind == SchemaNodeKind.Trigger
            ? [new DdlStatement(
                $"DROP TRIGGER IF EXISTS {Qualify(target.Path.Count > 2 ? target.Path[0] : "", target.Name)};",
                true, $"drop trigger {target.Name}")]
            : base.DropObject(target);

    /// A view is renamed with RENAME TABLE, like everything else in that namespace.
    public override IReadOnlyList<DdlStatement> Rename(SchemaNodeRef target, string newName)
    {
        if (target.Kind != SchemaNodeKind.View) return base.Rename(target, newName);

        var schema = target.Path.Count > 1 ? target.Path[0] : "";
        return [new DdlStatement(
            $"RENAME TABLE {Qualify(schema, target.Name)} TO {Qualify(schema, newName)};",
            false, $"rename {target.Name} to {newName}")];
    }

}

public sealed class SqlServerDdlWriter() : DdlWriterBase(new SqlServerDialect())
{
    protected override bool SupportsIfExists => true;
    protected override bool SupportsColumnComments => false; // extended properties, not COMMENT ON

    public override string MapType(string neutralType) => neutralType.ToLowerInvariant() switch
    {
        "text" => "NVARCHAR(MAX)",
        "int" or "integer" => "INT",
        "bigint" => "BIGINT",
        "smallint" => "SMALLINT",
        "bool" or "boolean" => "BIT",
        "float" or "real" => "REAL",
        "double" => "FLOAT",
        "date" => "DATE",
        "timestamp" => "DATETIME2",
        "uuid" => "UNIQUEIDENTIFIER",
        "json" => "NVARCHAR(MAX)",
        "blob" => "VARBINARY(MAX)",
        _ => neutralType.ToUpperInvariant(),
    };

    protected override string IdentityClause => "IDENTITY(1,1)";

    /// NVARCHAR(MAX) cannot be a key column; 450 characters is the largest that still fits the
    /// 1700-byte index key limit.
    protected override TableDefinition Normalize(TableDefinition table)
    {
        var keys = KeyColumns(table);

        return table with
        {
            Columns = table.Columns
                .Select(c => keys.Contains(c.Name) && c.Type.Equals("text", StringComparison.OrdinalIgnoreCase)
                    ? c with { Type = "nvarchar(450)" }
                    : c)
                .ToList(),
        };
    }

    protected override IReadOnlyList<DdlStatement> RenameColumn(TableDefinition before, string from, string to) =>
        [new DdlStatement(
            $"EXEC sp_rename '{before.Schema}.{before.Name}.{from}', '{to}', 'COLUMN';",
            false, $"rename column {from} to {to}")];

    protected override IReadOnlyList<DdlStatement> AlterColumn(TableDefinition before, ColumnDefinition column) =>
        [new DdlStatement(
            $"ALTER TABLE {Qualify(before.Schema, before.Name)} ALTER COLUMN " +
            $"{Dialect.QuoteIdentifier(column.Name)} {MapType(column.Type)} " +
            $"{(column.Nullable ? "NULL" : "NOT NULL")};",
            false, $"alter column {column.Name}")];

    public override IReadOnlyList<DdlStatement> Rename(SchemaNodeRef target, string newName)
    {
        var schema = target.Path.Count > 1 ? target.Path[0] : "dbo";
        return [new DdlStatement($"EXEC sp_rename '{schema}.{target.Name}', '{newName}';",
            false, $"rename {target.Name} to {newName}")];
    }

    public override IReadOnlyList<DdlStatement> DropIndex(string schema, string table, string name) =>
        [new DdlStatement($"DROP INDEX {Dialect.QuoteIdentifier(name)} ON {Qualify(schema, table)};",
            true, $"drop index {name}")];

    // --- objects other than tables ----------------------------------------------------------------

    /// SQL Server replaces a definition with CREATE OR ALTER rather than CREATE OR REPLACE.
    public override IReadOnlyList<DdlStatement> CreateOrReplaceView(string schema, string name, string select) =>
    [
        new DdlStatement($"CREATE OR ALTER VIEW {Qualify(schema, name)} AS\n{Body(select)};",
            false, $"create or alter view {name}"),
    ];

    /// The same for a routine: whatever the source in the editor says, it is sent as CREATE OR
    /// ALTER, so saving an existing procedure does not fail with "there is already an object named".
    public override IReadOnlyList<DdlStatement> CreateOrReplaceRoutine(RoutineDefinition routine) =>
    [
        new DdlStatement(CreateOrAlter(Body(routine.Body)) + ";", false,
            $"create or alter {routine.Kind} {routine.Name}"),
    ];

    private static string CreateOrAlter(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body,
            CreateHead, "CREATE OR ALTER $1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private const string CreateHead = @"^\s*CREATE\s+(?:OR\s+ALTER\s+)?(PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER)\b";

    /// A sequence needs a type; BIGINT is the one that does not run out.
    public override IReadOnlyList<DdlStatement> CreateSequence(SequenceDefinition sequence) =>
    [
        new DdlStatement(
            $"CREATE SEQUENCE {Qualify(sequence.Schema, sequence.Name)} AS BIGINT{Clauses(sequence, restart: false)};",
            false, $"create sequence {sequence.Name}"),
    ];

    /// There is no CASCADE here: a schema is dropped once it is empty, and saying so beats a
    /// statement the server rejects.
    public override IReadOnlyList<DdlStatement> DropSchema(string name, bool cascade) =>
        cascade
            ? throw new NotSupportedException(
                "SQL Server drops a schema only once it is empty; move or drop what is in it first")
            : base.DropSchema(name, cascade);

    /// A description lives in an extended property here, which is a procedure call rather than DDL
    /// and needs to know whether one is already there.
    public override IReadOnlyList<DdlStatement> Comment(SchemaNodeRef target, string? text) =>
        throw new NotSupportedException(
            "SQL Server keeps descriptions as extended properties; the studio's own notes are the "
            + "simpler place for one");

    public override IReadOnlyList<DdlStatement> SetTriggerEnabled(SchemaNodeRef trigger, bool enabled)
    {
        var (schema, table) = TableOf(trigger);

        return [new DdlStatement(
            $"{(enabled ? "ENABLE" : "DISABLE")} TRIGGER {Dialect.QuoteIdentifier(trigger.Name)} " +
            $"ON {Qualify(schema, table)};",
            !enabled, $"{(enabled ? "enable" : "disable")} trigger {trigger.Name}")];
    }

    public override IReadOnlyList<DdlStatement> DropObject(SchemaNodeRef target) =>
        target.Kind == SchemaNodeKind.Trigger
            ? [new DdlStatement(
                $"DROP TRIGGER IF EXISTS {Qualify(target.Path.Count > 2 ? target.Path[0] : "", target.Name)};",
                true, $"drop trigger {target.Name}")]
            : base.DropObject(target);

}

public sealed class SqliteDdlWriter() : DdlWriterBase(new SqliteDialect())
{
    /// SQLite puts the schema on the index, not on the table: `CREATE INDEX main.ix ON t (...)`.
    /// Qualifying the table instead is a syntax error.
    public override IReadOnlyList<DdlStatement> CreateIndex(string schema, string table, IndexDefinition index)
    {
        if (index.FullText) return base.CreateIndex(schema, table, index);

        var unique = index.Unique ? "UNIQUE " : "";
        var columns = string.Join(", ", index.Columns.Select(Dialect.QuoteIdentifier));
        var filter = index.Filter is { Length: > 0 } ? $" WHERE {index.Filter}" : "";

        return [new DdlStatement(
            $"CREATE {unique}INDEX {Qualify(schema, index.Name)} " +
            $"ON {Dialect.QuoteIdentifier(table)} ({columns}){filter};",
            false, $"create index {index.Name}")];
    }

    protected override bool SupportsColumnComments => false; // SQLite has no comment syntax at all
    protected override bool SupportsAddConstraint => false;  // nor ALTER TABLE ADD CONSTRAINT

    public override string MapType(string neutralType) => neutralType.ToLowerInvariant() switch
    {
        "text" or "uuid" or "json" => "TEXT",
        "int" or "integer" or "bigint" or "smallint" or "bool" or "boolean" => "INTEGER",
        "float" or "real" or "double" => "REAL",
        "date" or "timestamp" => "TEXT",
        "blob" => "BLOB",
        _ => neutralType.ToUpperInvariant(),
    };

    protected override string IdentityClause => "AUTOINCREMENT";

    /// SQLite cannot change a column in place. The honest answer is the rebuild sequence it would
    /// take by hand — shown in full in the preview, not hidden behind a statement that would fail.
    protected override IReadOnlyList<DdlStatement> AlterColumn(TableDefinition before, ColumnDefinition column)
    {
        var updated = before with
        {
            Columns = before.Columns
                .Select(c => c.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase) ? column : c)
                .ToList(),
        };

        return Rebuild(before, updated, $"change column {column.Name}");
    }

    public override IReadOnlyList<DdlStatement> AlterTable(TableDefinition before, TableChange change)
    {
        // Adds, renames and drops are supported directly by modern SQLite; anything that changes a
        // column's shape needs the rebuild.
        if (change.AlteredColumns.Count == 0 && change.AddedConstraints.Count == 0
            && change.DroppedConstraints.Count == 0)
            return base.AlterTable(before, change);

        var after = Apply(before, change);
        return Rebuild(before, after, "apply schema changes");
    }

    private IReadOnlyList<DdlStatement> Rebuild(TableDefinition before, TableDefinition after, string reason)
    {
        var temporary = after with { Name = $"{after.Name}__new" };
        var shared = after.Columns
            .Where(c => before.Columns.Any(b =>
                b.Name.Equals(c.RenamedFrom ?? c.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var targetColumns = string.Join(", ", shared.Select(c => Dialect.QuoteIdentifier(c.Name)));
        var sourceColumns = string.Join(", ", shared.Select(c => Dialect.QuoteIdentifier(c.RenamedFrom ?? c.Name)));

        var statements = new List<DdlStatement>();
        statements.AddRange(CreateTable(temporary));
        statements.Add(new DdlStatement(
            $"INSERT INTO {Qualify(after.Schema, temporary.Name)} ({targetColumns}) " +
            $"SELECT {sourceColumns} FROM {Qualify(before.Schema, before.Name)};",
            false, "copy the rows"));
        statements.Add(new DdlStatement(
            $"DROP TABLE {Qualify(before.Schema, before.Name)};", true, "drop the old table"));
        statements.Add(new DdlStatement(
            $"ALTER TABLE {Qualify(after.Schema, temporary.Name)} RENAME TO {Dialect.QuoteIdentifier(after.Name)};",
            false, $"rename the rebuilt table ({reason})"));

        return statements;
    }

    private static TableDefinition Apply(TableDefinition before, TableChange change)
    {
        var columns = before.Columns.ToList();

        foreach (var rename in change.RenamedColumns)
        {
            var index = columns.FindIndex(c => c.Name.Equals(rename.Before!.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) columns[index] = rename.Column;
        }

        foreach (var altered in change.AlteredColumns)
        {
            var index = columns.FindIndex(c => c.Name.Equals(
                altered.Column.RenamedFrom ?? altered.Column.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) columns[index] = altered.Column;
        }

        columns.RemoveAll(c => change.DroppedColumns.Any(d =>
            d.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)));
        columns.AddRange(change.AddedColumns);

        var constraints = before.Constraints
            .Where(c => !change.DroppedConstraints.Contains(c))
            .Concat(change.AddedConstraints)
            .ToList();

        var indexes = before.Indexes
            .Where(i => !change.DroppedIndexes.Contains(i))
            .Concat(change.AddedIndexes)
            .ToList();

        return before with { Columns = columns, Constraints = constraints, Indexes = indexes };
    }

    // --- objects other than tables ----------------------------------------------------------------

    protected override bool HasSchemas => false;

    /// SQLite cannot replace a view, so it is dropped and written again - both in the preview.
    public override IReadOnlyList<DdlStatement> CreateOrReplaceView(string schema, string name, string select) =>
    [
        new DdlStatement($"DROP VIEW IF EXISTS {Qualify(schema, name)};", true, $"drop view {name}"),
        new DdlStatement($"CREATE VIEW {Qualify(schema, name)} AS\n{Body(select)};", false,
            $"create view {name}"),
    ];

    public override IReadOnlyList<DdlStatement> CreateSequence(SequenceDefinition sequence) =>
        throw new NotSupportedException(
            "SQLite has no sequences; an INTEGER PRIMARY KEY counts up by itself");

    public override IReadOnlyList<DdlStatement> AlterSequence(SequenceDefinition sequence) =>
        CreateSequence(sequence);

    public override IReadOnlyList<DdlStatement> CreateSchema(string name) =>
        throw new NotSupportedException("a SQLite file has no schemas");

    public override IReadOnlyList<DdlStatement> DropSchema(string name, bool cascade) =>
        throw new NotSupportedException("a SQLite file has no schemas");

    public override IReadOnlyList<DdlStatement> Comment(SchemaNodeRef target, string? text) =>
        throw new NotSupportedException(
            "SQLite keeps no descriptions of its own; the studio's own notes cover it");

    public override IReadOnlyList<DdlStatement> SetTriggerEnabled(SchemaNodeRef trigger, bool enabled) =>
        throw new NotSupportedException("SQLite cannot switch a trigger off; drop it instead");

    public override IReadOnlyList<DdlStatement> DropObject(SchemaNodeRef target) =>
        target.Kind == SchemaNodeKind.Trigger
            ? [new DdlStatement($"DROP TRIGGER IF EXISTS {Dialect.QuoteIdentifier(target.Name)};",
                true, $"drop trigger {target.Name}")]
            : base.DropObject(target);

    /// Only a table or an index can be renamed; the rest is created under the new name.
    public override IReadOnlyList<DdlStatement> Rename(SchemaNodeRef target, string newName) =>
        target.Kind is SchemaNodeKind.Table or SchemaNodeKind.Index
            ? base.Rename(target, newName)
            : throw new NotSupportedException(
                $"SQLite cannot rename a {target.Kind.ToString().ToLowerInvariant()}; create it under "
                + "the new name and drop the old one");

}
