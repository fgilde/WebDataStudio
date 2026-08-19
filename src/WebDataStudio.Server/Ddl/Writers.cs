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
}
