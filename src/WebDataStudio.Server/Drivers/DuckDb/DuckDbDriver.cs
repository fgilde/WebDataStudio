using DuckDB.NET.Data;
// DuckDB.NET also exports a ColumnInfo; alias ours so the file reads unambiguously.
using ColumnInfo = WebDataStudio.Server.Drivers.Abstractions.ColumnInfo;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.DuckDb;

public sealed class DuckDbDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    public override string ParameterPrefix => "$";
    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";
}

public sealed class DuckDbDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } = new("duckdb", "DuckDB", 0, "Data Source=/path/to.duckdb");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, MultiSchema = true, Transactions = true, Ddl = true,
        EstimatedPlan = true, ActualPlan = true, Views = true, Sequences = true,
        ForeignKeys = true, SystemCommands = true,
        // No stored procedures, no triggers, no user management, no session list: DuckDB is an
        // in-process analytical engine and has none of them.
    };

    public override SqlDialect Dialect { get; } = new DuckDbDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new DuckDBConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct, bool systemObjects = false)
    {
        if (parent is null)
            return await QueryAsync(session, ct,
                $"""
                SELECT schema_name FROM information_schema.schemata
                 WHERE {(systemObjects
                     ? "true"
                     : "schema_name NOT IN ('information_schema', 'pg_catalog', 'temp', 'system')")}
                 ORDER BY schema_name
                """,
                name => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [name]), name, true));

        if (parent.Kind == SchemaNodeKind.Schema)
        {
            var s = parent.Name;
            return
            [
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TableFolder, [s, "tables"]), "Tables", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ViewFolder, [s, "views"]), "Views", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.SequenceFolder, [s, "sequences"]), "Sequences", true),
            ];
        }

        var schema = parent.Path[0].Replace("'", "''");
        var (sql, kind) = parent.Kind switch
        {
            SchemaNodeKind.TableFolder => (
                $"""
                SELECT table_name FROM information_schema.tables
                 WHERE table_schema = '{schema}' AND table_type = 'BASE TABLE'
                 ORDER BY table_name
                """,
                SchemaNodeKind.Table),
            SchemaNodeKind.ViewFolder => (
                $"""
                SELECT table_name FROM information_schema.tables
                 WHERE table_schema = '{schema}' AND table_type = 'VIEW'
                 ORDER BY table_name
                """,
                SchemaNodeKind.View),
            SchemaNodeKind.SequenceFolder => (
                $"SELECT sequence_name FROM duckdb_sequences() WHERE schema_name = '{schema}' ORDER BY sequence_name",
                SchemaNodeKind.Sequence),
            _ => (null, SchemaNodeKind.Table),
        };
        if (sql is null) return [];

        return await QueryAsync(session, ct, sql,
            name => new SchemaNode(new SchemaNodeRef(kind, [parent.Path[0], name]), name,
                HasChildren: kind is SchemaNodeKind.Table or SchemaNodeKind.View));
    }

    public override async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var schema = target.Path[0].Replace("'", "''");
        var name = target.Name.Replace("'", "''");

        var columns = new List<ColumnInfo>();
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT c.column_name, c.data_type, c.is_nullable, c.column_default, c.ordinal_position,
                       CASE WHEN k.column_name IS NULL THEN false ELSE true END
                  FROM information_schema.columns c
                  LEFT JOIN (
                       SELECT kcu.column_name
                         FROM information_schema.table_constraints tc
                         JOIN information_schema.key_column_usage kcu
                           ON kcu.constraint_name = tc.constraint_name
                        WHERE tc.constraint_type = 'PRIMARY KEY'
                          AND tc.table_schema = '{schema}' AND tc.table_name = '{name}'
                  ) k ON k.column_name = c.column_name
                 WHERE c.table_schema = '{schema}' AND c.table_name = '{name}'
                 ORDER BY c.ordinal_position
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new ColumnInfo(
                    reader.GetString(0), reader.GetString(1),
                    reader.GetValue(2).ToString() is "YES" or "True" or "true",
                    reader.IsDBNull(3) ? null : reader.GetValue(3).ToString(),
                    Convert.ToBoolean(reader.GetValue(5)), false, null,
                    Convert.ToInt32(reader.GetValue(4))));
        }

        var indexes = new List<IndexInfo>();
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT index_name, is_unique, sql
                  FROM duckdb_indexes()
                 WHERE schema_name = '{schema}' AND table_name = '{name}'
                 ORDER BY index_name
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                // duckdb_indexes() carries the DDL, not a column list; the expression is the
                // honest answer rather than a guess parsed out of it.
                indexes.Add(new IndexInfo(reader.GetString(0), [], reader.GetBoolean(1), false,
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        var foreignKeys = new List<ForeignKeyInfo>();
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT constraint_text
                  FROM duckdb_constraints()
                 WHERE schema_name = '{schema}' AND table_name = '{name}' AND constraint_type = 'FOREIGN KEY'
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // duckdb_constraints() gives the constraint text, not structured columns; both the
                // local and the referenced side are read out of it.
                var text = reader.GetString(0);
                foreignKeys.Add(new ForeignKeyInfo($"fk_{target.Name}_{foreignKeys.Count}",
                    LocalColumns(text), target.Path[0], ReferencedTable(text), [],
                    "NO ACTION", "NO ACTION"));
            }
        }

        long? rows = null;
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT count(*) FROM {Dialect.QuoteIdentifier(target.Path[0])}.{Dialect.QuoteIdentifier(target.Name)}";
            rows = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        return new ObjectDetail(target, columns, indexes, foreignKeys, [], rows, null, null, null);
    }

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = (mode == PlanMode.Actual ? "EXPLAIN ANALYZE " : "EXPLAIN ") + sql;

        var text = new System.Text.StringBuilder();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                for (var i = 0; i < reader.FieldCount; i++)
                    if (!reader.IsDBNull(i)) text.AppendLine(reader.GetValue(i).ToString());

        // DuckDB draws its plan as ASCII art; the tree is reconstructed from the operator names,
        // which is enough for the heat map and the rules without pretending to parse the box art.
        var children = text.ToString()
            .Split('\n')
            .Select(l => l.Trim().Trim('│', '┌', '┐', '└', '┘', '├', '─', '┤', ' '))
            .Where(l => l.Length > 2 && l.All(c => char.IsLetter(c) || c is '_' or ' '))
            .Distinct()
            .Select(l => new PlanNode(l, null, null, null, null, null, [],
                l.Contains("SEQ_SCAN", StringComparison.OrdinalIgnoreCase) ? ["sequential scan"] : []))
            .ToList();

        return new PlanNode("EXPLAIN", null, null, null, null, null, children, []);
    }

    /// "FOREIGN KEY (person_id) REFERENCES people(id)" — the columns before REFERENCES.
    private static List<string> LocalColumns(string constraintText)
    {
        var open = constraintText.IndexOf('(');
        var close = constraintText.IndexOf(')');
        if (open < 0 || close <= open) return [];

        return constraintText[(open + 1)..close]
            .Split(',')
            .Select(c => c.Trim().Trim('"'))
            .Where(c => c.Length > 0)
            .ToList();
    }

    private static string ReferencedTable(string constraintText)
    {
        // "FOREIGN KEY (person_id) REFERENCES people(id)"
        const string marker = "REFERENCES ";
        var index = constraintText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "";

        var rest = constraintText[(index + marker.Length)..].TrimStart();
        var end = rest.IndexOfAny(['(', ' ']);
        return end < 0 ? rest : rest[..end];
    }

    private static async Task<IReadOnlyList<SchemaNode>> QueryAsync(
        IDbSession session, CancellationToken ct, string sql, Func<string, SchemaNode> map)
    {
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) nodes.Add(map(reader.GetString(0)));
        return nodes;
    }
}
