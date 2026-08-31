using System.Data.Common;
using MySqlConnector;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.MySql;

public sealed class MySqlDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } =
        new("mysql", "MySQL / MariaDB", 3306, "Server=localhost;Port=3306;Database=mysql;User ID=root;Password=");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, MultiDatabase = true, Transactions = true, Ddl = true,
        EstimatedPlan = true, ActualPlan = true, StoredProcedures = true, Triggers = true,
        Views = true, ForeignKeys = true, FullTextIndexes = true, Backup = true, Restore = true,
        UserManagement = true, SessionList = true, KillSession = true, ServerStats = true,
        SlowQueryLog = true, SystemCommands = true,
        // The event scheduler: the same concept under another name.
        Jobs = true,
        ActivityProgress = true, Replication = true,
    };

    public override SqlDialect Dialect { get; } = new MySqlDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new MySqlConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct, bool systemObjects = false)
    {
        // In MySQL a schema IS a database, so the root lists databases as schema nodes.
        if (parent is null)
        {
            var visible = systemObjects
                ? "true"
                : "schema_name NOT IN ('mysql','information_schema','performance_schema','sys')";

            return await QueryAsync(session, ct,
                $"SELECT schema_name FROM information_schema.schemata WHERE {visible} ORDER BY schema_name",
                name => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [name]), name, true));
        }

        if (parent.Kind == SchemaNodeKind.Schema)
        {
            var s = parent.Name;
            return
            [
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TableFolder, [s, "tables"]), "Tables", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ViewFolder, [s, "views"]), "Views", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ProcedureFolder, [s, "procedures"]), "Procedures", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.FunctionFolder, [s, "functions"]), "Functions", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TriggerFolder, [s, "triggers"]), "Triggers", true),
            ];
        }

        var schema = parent.Path[0];
        var (sql, kind) = parent.Kind switch
        {
            SchemaNodeKind.TableFolder => (
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE' ORDER BY table_name",
                SchemaNodeKind.Table),
            SchemaNodeKind.ViewFolder => (
                "SELECT table_name FROM information_schema.views WHERE table_schema = @s ORDER BY table_name",
                SchemaNodeKind.View),
            SchemaNodeKind.ProcedureFolder => (
                "SELECT routine_name FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'PROCEDURE' ORDER BY routine_name",
                SchemaNodeKind.Procedure),
            SchemaNodeKind.FunctionFolder => (
                "SELECT routine_name FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'FUNCTION' ORDER BY routine_name",
                SchemaNodeKind.Function),
            SchemaNodeKind.TriggerFolder => (
                "SELECT trigger_name FROM information_schema.triggers WHERE trigger_schema = @s ORDER BY trigger_name",
                SchemaNodeKind.Trigger),
            _ => (null, SchemaNodeKind.Table),
        };
        if (sql is null) return [];

        return await QueryAsync(session, ct, sql,
            name => new SchemaNode(new SchemaNodeRef(kind, [schema, name]), name,
                HasChildren: kind is SchemaNodeKind.Table or SchemaNodeKind.View),
            schema);
    }

    public override async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var schema = target.Path[0];
        var name = target.Name;

        var columns = new List<ColumnInfo>();
        await using (var cmd = Command(session,
            """
            SELECT column_name, column_type, is_nullable = 'YES', column_default,
                   column_key = 'PRI', extra LIKE '%auto_increment%', column_comment, ordinal_position
              FROM information_schema.columns
             WHERE table_schema = @s AND table_name = @t
             ORDER BY ordinal_position
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

        var indexes = new Dictionary<string, IndexInfo>();
        await using (var cmd = Command(session,
            """
            SELECT index_name, column_name, non_unique = 0, index_type = 'FULLTEXT'
              FROM information_schema.statistics
             WHERE table_schema = @s AND table_name = @t
             ORDER BY index_name, seq_in_index
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var indexName = reader.GetString(0);
                var column = reader.GetString(1);
                if (!indexes.TryGetValue(indexName, out var existing))
                    existing = new IndexInfo(indexName, [], reader.GetBoolean(2), indexName == "PRIMARY",
                        null, reader.GetBoolean(3));
                indexes[indexName] = existing with { Columns = existing.Columns.Append(column).ToList() };
            }
        }

        var foreignKeys = new Dictionary<string, ForeignKeyInfo>();
        await using (var cmd = Command(session,
            """
            SELECT k.constraint_name, k.column_name, k.referenced_table_schema,
                   k.referenced_table_name, k.referenced_column_name,
                   r.delete_rule, r.update_rule
              FROM information_schema.key_column_usage k
              JOIN information_schema.referential_constraints r
                ON r.constraint_schema = k.constraint_schema AND r.constraint_name = k.constraint_name
             WHERE k.table_schema = @s AND k.table_name = @t AND k.referenced_table_name IS NOT NULL
             ORDER BY k.constraint_name, k.ordinal_position
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                if (!foreignKeys.TryGetValue(key, out var existing))
                    existing = new ForeignKeyInfo(key, [], reader.GetString(2), reader.GetString(3), [],
                        reader.GetString(5), reader.GetString(6));
                foreignKeys[key] = existing with
                {
                    Columns = existing.Columns.Append(reader.GetString(1)).ToList(),
                    ReferencedColumns = existing.ReferencedColumns.Append(reader.GetString(4)).ToList(),
                };
            }
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
            SELECT table_rows, data_length + index_length, table_comment
              FROM information_schema.tables WHERE table_schema = @s AND table_name = @t
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

        return new ObjectDetail(target, columns, indexes.Values.ToList(), foreignKeys.Values.ToList(),
            triggers, rows, size, comment, null);
    }

    public override Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope,
        SchemaNodeRef? target, CancellationToken ct) =>
        Analysis.MySqlAnalyzer.RunAsync(session, target?.Path.FirstOrDefault(), ct);

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        // MySQL 8 returns a tree; ANALYZE adds measured timings.
        var prefix = mode == PlanMode.Actual ? "EXPLAIN ANALYZE " : "EXPLAIN FORMAT=TREE ";
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = prefix + sql;

        var text = (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "";
        var children = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => new PlanNode(l.Trim().TrimStart('-', '>', ' '), null, null, null, null, null, [],
                l.Contains("Table scan", StringComparison.OrdinalIgnoreCase) ? ["full table scan"] : []))
            .ToList();

        return new PlanNode("EXPLAIN", null, null, null, null, null, children, []);
    }

    // --- helpers -----------------------------------------------------------

    private static DbCommand Command(IDbSession session, string sql, string schema, string? table = null)
    {
        var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new MySqlParameter("@s", schema));
        if (table is not null) cmd.Parameters.Add(new MySqlParameter("@t", table));
        return cmd;
    }

    private static async Task<IReadOnlyList<SchemaNode>> QueryAsync(
        IDbSession session, CancellationToken ct, string sql, Func<string, SchemaNode> map, string? schema = null)
    {
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        if (schema is not null) cmd.Parameters.Add(new MySqlParameter("@s", schema));

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) nodes.Add(map(reader.GetString(0)));
        return nodes;
    }
}
