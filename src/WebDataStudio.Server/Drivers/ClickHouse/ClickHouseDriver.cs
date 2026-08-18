using ClickHouse.Client.ADO;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.ClickHouse;

public sealed class ClickHouseDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "`" + name.Replace("`", "``") + "`";
    public override string ParameterPrefix => "@";
    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";
}

public sealed class ClickHouseDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } =
        new("clickhouse", "ClickHouse", 8123, "Host=localhost;Port=8123;Database=default;Username=default;Password=");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, MultiDatabase = true, MultiSchema = true, Ddl = true,
        EstimatedPlan = true, Views = true, MaterializedViews = true,
        ServerStats = true, SystemCommands = true, SessionList = true, KillSession = true,
        // No transactions, no foreign keys, no triggers: ClickHouse has none of them, and the UI
        // hides the corresponding features rather than offering something that cannot work.
    };

    public override SqlDialect Dialect { get; } = new ClickHouseDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new ClickHouseConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct)
    {
        if (parent is null)
            return await QueryAsync(session, ct,
                """
                SELECT name FROM system.databases
                 WHERE name NOT IN ('system', 'INFORMATION_SCHEMA', 'information_schema')
                 ORDER BY name
                """,
                name => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [name]), name, true));

        if (parent.Kind == SchemaNodeKind.Schema)
        {
            var s = parent.Name;
            return
            [
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TableFolder, [s, "tables"]), "Tables", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ViewFolder, [s, "views"]), "Views", true),
            ];
        }

        var schema = parent.Path[0].Replace("'", "''");
        var (sql, kind) = parent.Kind switch
        {
            SchemaNodeKind.TableFolder => (
                $"SELECT name FROM system.tables WHERE database = '{schema}' AND engine NOT LIKE '%View' ORDER BY name",
                SchemaNodeKind.Table),
            SchemaNodeKind.ViewFolder => (
                $"SELECT name FROM system.tables WHERE database = '{schema}' AND engine LIKE '%View' ORDER BY name",
                SchemaNodeKind.View),
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
                SELECT name, type, default_expression, comment, position, is_in_primary_key
                  FROM system.columns
                 WHERE database = '{schema}' AND table = '{name}'
                 ORDER BY position
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(1);
                columns.Add(new ColumnInfo(
                    reader.GetString(0), type,
                    // ClickHouse spells nullability inside the type, not as a flag.
                    Nullable: type.StartsWith("Nullable(", StringComparison.OrdinalIgnoreCase),
                    reader.IsDBNull(2) || reader.GetString(2).Length == 0 ? null : reader.GetString(2),
                    Convert.ToInt32(reader.GetValue(5)) == 1, false,
                    reader.IsDBNull(3) || reader.GetString(3).Length == 0 ? null : reader.GetString(3),
                    Convert.ToInt32(reader.GetValue(4))));
            }
        }

        var indexes = new List<IndexInfo>();
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT name, expr, type FROM system.data_skipping_indices
                 WHERE database = '{schema}' AND table = '{name}'
                 ORDER BY name
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                indexes.Add(new IndexInfo(reader.GetString(0), [reader.GetString(1)], false, false,
                    reader.GetString(2)));
        }

        long? rows = null;
        long? size = null;
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT sum(rows), sum(bytes_on_disk) FROM system.parts
                 WHERE database = '{schema}' AND table = '{name}' AND active
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                rows = reader.IsDBNull(0) ? null : Convert.ToInt64(reader.GetValue(0));
                size = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1));
            }
        }

        return new ObjectDetail(target, columns, indexes, [], [], rows, size, null, null);
    }

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        if (mode == PlanMode.Actual)
            throw new NotSupportedException("ClickHouse does not return an actual execution plan");

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "EXPLAIN indexes = 1 " + sql;

        var lines = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(0)) lines.Add(reader.GetString(0));

        var children = lines
            .Select(l => new PlanNode(l.Trim(), null, null, null, null, null, [],
                l.Contains("ReadFromMergeTree", StringComparison.OrdinalIgnoreCase) && !l.Contains("Index")
                    ? ["full part scan"] : []))
            .ToList();

        return new PlanNode("EXPLAIN", null, null, null, null, null, children, []);
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
