using System.Data.Common;
using System.Text.Json;
using Npgsql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.PostgreSql;

public sealed class PostgreSqlDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } =
        new("postgresql", "PostgreSQL", 5432, "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, MultiSchema = true, MultiDatabase = true, Transactions = true, Ddl = true,
        EstimatedPlan = true, ActualPlan = true, StoredProcedures = true, Triggers = true,
        Views = true, MaterializedViews = true, Sequences = true, ForeignKeys = true,
        PartialIndexes = true, IncludeColumns = true, Backup = true, Restore = true,
        UserManagement = true, SessionList = true, KillSession = true, ServerStats = true,
        SlowQueryLog = true, SystemCommands = true,
    };

    public override SqlDialect Dialect { get; } = new PostgreSqlDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct)
    {
        if (parent is null)
            return await QueryNodesAsync(session, ct,
                """
                SELECT nspname FROM pg_namespace
                 WHERE nspname NOT IN ('pg_catalog','information_schema')
                   AND nspname NOT LIKE 'pg_toast%' AND nspname NOT LIKE 'pg_temp%'
                 ORDER BY nspname
                """,
                name => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [name]), name, true));

        if (parent.Kind == SchemaNodeKind.Schema)
        {
            var s = parent.Name;
            return
            [
                Folder(SchemaNodeKind.TableFolder, s, "tables", "Tables"),
                Folder(SchemaNodeKind.ViewFolder, s, "views", "Views"),
                Folder(SchemaNodeKind.ProcedureFolder, s, "procedures", "Procedures"),
                Folder(SchemaNodeKind.FunctionFolder, s, "functions", "Functions"),
                Folder(SchemaNodeKind.SequenceFolder, s, "sequences", "Sequences"),
            ];
        }

        var schema = parent.Path[0];
        return parent.Kind switch
        {
            SchemaNodeKind.TableFolder => await ListAsync(session, ct, schema, SchemaNodeKind.Table,
                "SELECT tablename FROM pg_tables WHERE schemaname = @s ORDER BY tablename"),
            SchemaNodeKind.ViewFolder => await ListAsync(session, ct, schema, SchemaNodeKind.View,
                "SELECT viewname FROM pg_views WHERE schemaname = @s ORDER BY viewname"),
            SchemaNodeKind.ProcedureFolder => await ListAsync(session, ct, schema, SchemaNodeKind.Procedure,
                """
                SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname = @s AND p.prokind = 'p' ORDER BY p.proname
                """),
            SchemaNodeKind.FunctionFolder => await ListAsync(session, ct, schema, SchemaNodeKind.Function,
                """
                SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname = @s AND p.prokind = 'f' ORDER BY p.proname
                """),
            SchemaNodeKind.SequenceFolder => await ListAsync(session, ct, schema, SchemaNodeKind.Sequence,
                "SELECT sequencename FROM pg_sequences WHERE schemaname = @s ORDER BY sequencename"),
            _ => [],
        };
    }

    public override async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var schema = target.Path[0];
        var name = target.Name;

        var columns = new List<ColumnInfo>();
        await using (var cmd = Command(session,
            """
            SELECT c.column_name, c.data_type, c.is_nullable = 'YES', c.column_default,
                   COALESCE(pk.is_pk, false), c.is_identity = 'YES',
                   col_description(format('%I.%I', c.table_schema, c.table_name)::regclass, c.ordinal_position),
                   c.ordinal_position
              FROM information_schema.columns c
              LEFT JOIN (
                   SELECT kcu.column_name, true AS is_pk
                     FROM information_schema.table_constraints tc
                     JOIN information_schema.key_column_usage kcu
                       ON kcu.constraint_name = tc.constraint_name
                      AND kcu.table_schema = tc.table_schema
                    WHERE tc.constraint_type = 'PRIMARY KEY'
                      AND tc.table_schema = @s AND tc.table_name = @t
              ) pk ON pk.column_name = c.column_name
             WHERE c.table_schema = @s AND c.table_name = @t
             ORDER BY c.ordinal_position
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new ColumnInfo(
                    reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7)));
        }

        var indexes = new List<IndexInfo>();
        await using (var cmd = Command(session,
            """
            SELECT i.relname,
                   ARRAY(SELECT pg_get_indexdef(ix.indexrelid, k + 1, true)
                           FROM generate_subscripts(ix.indkey, 1) AS k ORDER BY k),
                   ix.indisunique, ix.indisprimary,
                   pg_get_expr(ix.indpred, ix.indrelid)
              FROM pg_index ix
              JOIN pg_class i ON i.oid = ix.indexrelid
              JOIN pg_class t ON t.oid = ix.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
             WHERE n.nspname = @s AND t.relname = @t
             ORDER BY i.relname
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                indexes.Add(new IndexInfo(
                    reader.GetString(0), reader.GetFieldValue<string[]>(1),
                    reader.GetBoolean(2), reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        var foreignKeys = new List<ForeignKeyInfo>();
        await using (var cmd = Command(session,
            """
            SELECT con.conname,
                   ARRAY(SELECT att.attname FROM unnest(con.conkey) AS k
                           JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = k),
                   fn.nspname, ft.relname,
                   ARRAY(SELECT att.attname FROM unnest(con.confkey) AS k
                           JOIN pg_attribute att ON att.attrelid = con.confrelid AND att.attnum = k),
                   con.confdeltype, con.confupdtype
              FROM pg_constraint con
              JOIN pg_class t ON t.oid = con.conrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
              JOIN pg_class ft ON ft.oid = con.confrelid
              JOIN pg_namespace fn ON fn.oid = ft.relnamespace
             WHERE con.contype = 'f' AND n.nspname = @s AND t.relname = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                foreignKeys.Add(new ForeignKeyInfo(
                    reader.GetString(0), reader.GetFieldValue<string[]>(1),
                    reader.GetString(2), reader.GetString(3), reader.GetFieldValue<string[]>(4),
                    ReferentialAction(reader.GetChar(5)), ReferentialAction(reader.GetChar(6))));
        }

        var triggers = new List<TriggerInfo>();
        await using (var cmd = Command(session,
            """
            SELECT trigger_name, action_timing, event_manipulation
              FROM information_schema.triggers
             WHERE event_object_schema = @s AND event_object_table = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                triggers.Add(new TriggerInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        long? rows = null;
        long? size = null;
        string? comment = null;
        await using (var cmd = Command(session,
            """
            SELECT c.reltuples::bigint, pg_total_relation_size(c.oid), obj_description(c.oid)
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = @s AND c.relname = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                rows = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                size = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                comment = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        return new ObjectDetail(target, columns, indexes, foreignKeys, triggers, rows, size, comment, null);
    }

    public override Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope,
        SchemaNodeRef? target, CancellationToken ct) =>
        Analysis.PostgreSqlAnalyzer.RunAsync(session, target?.Path.FirstOrDefault(), ct);

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        var prefix = mode == PlanMode.Actual
            ? "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            : "EXPLAIN (FORMAT JSON) ";

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = prefix + sql;
        var json = (string)(await cmd.ExecuteScalarAsync(ct))!;

        using var document = JsonDocument.Parse(json);
        return Convert(document.RootElement[0].GetProperty("Plan"));

        static PlanNode Convert(JsonElement element)
        {
            var children = element.TryGetProperty("Plans", out var plans)
                ? plans.EnumerateArray().Select(Convert).ToList()
                : [];

            var operation = element.GetProperty("Node Type").GetString()!;
            var estimatedRows = Number(element, "Plan Rows");
            var actualRows = Number(element, "Actual Rows");

            var warnings = new List<string>();
            if (operation == "Seq Scan" && estimatedRows > 1000) warnings.Add("sequential scan over many rows");
            if (actualRows is not null && estimatedRows is > 0 && actualRows > estimatedRows * 10)
                warnings.Add("row estimate is off by more than 10x; statistics may be stale");
            if (element.TryGetProperty("Sort Space Type", out var space) && space.GetString() == "Disk")
                warnings.Add("sort spilled to disk");

            return new PlanNode(
                operation,
                element.TryGetProperty("Relation Name", out var rel) ? rel.GetString() : null,
                Number(element, "Total Cost"), estimatedRows, actualRows,
                Number(element, "Actual Total Time"), children, warnings);
        }

        static double? Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetDouble(out var d) ? d : null;
    }

    protected override (int? Line, int? Column) LocateError(DbException exception, string sql)
    {
        // Npgsql reports a 1-based character position in the statement; turn it into line/column.
        if (exception is not PostgresException { Position: > 0 } pg) return (null, null);

        var offset = Math.Min(pg.Position - 1, sql.Length - 1);
        var line = 1;
        var column = 1;
        for (var i = 0; i < offset; i++)
        {
            if (sql[i] == '\n') { line++; column = 1; }
            else column++;
        }
        return (line, column);
    }

    // --- helpers -----------------------------------------------------------

    private static string ReferentialAction(char code) => code switch
    {
        'a' => "NO ACTION", 'r' => "RESTRICT", 'c' => "CASCADE",
        'n' => "SET NULL", 'd' => "SET DEFAULT", _ => code.ToString(),
    };

    private static SchemaNode Folder(SchemaNodeKind kind, string schema, string slug, string label) =>
        new(new SchemaNodeRef(kind, [schema, slug]), label, true);

    private static DbCommand Command(IDbSession session, string sql, string schema, string? table = null)
    {
        var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter("s", schema));
        if (table is not null) cmd.Parameters.Add(new NpgsqlParameter("t", table));
        return cmd;
    }

    private static async Task<IReadOnlyList<SchemaNode>> QueryNodesAsync(
        IDbSession session, CancellationToken ct, string sql, Func<string, SchemaNode> map)
    {
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) nodes.Add(map(reader.GetString(0)));
        return nodes;
    }

    private static async Task<IReadOnlyList<SchemaNode>> ListAsync(
        IDbSession session, CancellationToken ct, string schema, SchemaNodeKind kind, string sql)
    {
        await using var cmd = Command(session, sql, schema);

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            nodes.Add(new SchemaNode(new SchemaNodeRef(kind, [schema, name]), name,
                HasChildren: kind is SchemaNodeKind.Table or SchemaNodeKind.View));
        }
        return nodes;
    }
}
