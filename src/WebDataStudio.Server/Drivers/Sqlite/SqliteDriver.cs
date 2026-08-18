using Microsoft.Data.Sqlite;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.Sqlite;

public sealed class SqliteDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } = new("sqlite", "SQLite", 0, "Data Source=/path/to.db");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, Transactions = true, Ddl = true, Views = true, Triggers = true,
        ForeignKeys = true, PartialIndexes = true, EstimatedPlan = true, SystemCommands = true,
        // VACUUM INTO makes a consistent copy without any external tool. Restoring means replacing
        // the file underneath an open connection, so that stays off.
        Backup = true,
    };

    public override SqlDialect Dialect { get; } = new SqliteDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new SqliteConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct)
    {
        // SQLite has one nameless schema: the root shows folders directly.
        if (parent is null)
            return
            [
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TableFolder, ["main", "tables"]), "Tables", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ViewFolder, ["main", "views"]), "Views", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TriggerFolder, ["main", "triggers"]), "Triggers", true),
            ];

        var (type, kind) = parent.Kind switch
        {
            SchemaNodeKind.TableFolder => ("table", SchemaNodeKind.Table),
            SchemaNodeKind.ViewFolder => ("view", SchemaNodeKind.View),
            SchemaNodeKind.TriggerFolder => ("trigger", SchemaNodeKind.Trigger),
            _ => (null, SchemaNodeKind.Table),
        };
        if (type is null) return [];

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%' ORDER BY name";
        cmd.Parameters.Add(new SqliteParameter("$type", type));

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            nodes.Add(new SchemaNode(new SchemaNodeRef(kind, ["main", name]), name,
                HasChildren: kind is SchemaNodeKind.Table or SchemaNodeKind.View));
        }
        return nodes;
    }

    public override async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var table = target.Name;
        var quoted = Dialect.QuoteIdentifier(table);
        var columns = new List<ColumnInfo>();
        var indexes = new List<IndexInfo>();
        var foreignKeys = new List<ForeignKeyInfo>();

        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({quoted})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new ColumnInfo(
                    reader.GetString(1), reader.GetString(2),
                    // PRAGMA reports notnull=0 for an INTEGER PRIMARY KEY, but a rowid alias can
                    // never be null; reporting it as nullable would make the designer think the
                    // column changed on every save.
                    Nullable: reader.GetInt32(3) == 0 && reader.GetInt32(5) == 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5) > 0, false, null, reader.GetInt32(0)));
        }

        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA index_list({quoted})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                indexes.Add(new IndexInfo(reader.GetString(1), [], reader.GetInt32(2) == 1,
                    // origin: 'pk' for the implicit primary key index, 'u' for UNIQUE, 'c' for CREATE INDEX
                    Primary: reader.GetString(3) == "pk", null));
        }

        // index_list gives names only; a second pass fills the columns of each index.
        for (var i = 0; i < indexes.Count; i++)
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = $"PRAGMA index_info({Dialect.QuoteIdentifier(indexes[i].Name)})";
            var cols = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(2)) cols.Add(reader.GetString(2));
            indexes[i] = indexes[i] with { Columns = cols };
        }

        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA foreign_key_list({quoted})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                foreignKeys.Add(new ForeignKeyInfo(
                    $"fk_{table}_{reader.GetInt32(0)}", [reader.GetString(3)],
                    "main", reader.GetString(2), [reader.GetString(4)],
                    reader.GetString(6), reader.GetString(5)));
        }

        long? rowCount;
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {quoted}";
            rowCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        string? ddl;
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name = $name";
            cmd.Parameters.Add(new SqliteParameter("$name", table));
            ddl = await cmd.ExecuteScalarAsync(ct) as string;
        }

        return new ObjectDetail(target, columns, indexes, foreignKeys, [], rowCount, null, null, ddl);
    }

    public override Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope,
        SchemaNodeRef? target, CancellationToken ct) =>
        Analysis.SqliteAnalyzer.RunAsync(session, ct);

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        if (mode == PlanMode.Actual)
            throw new NotSupportedException("SQLite does not produce actual execution plans");

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;

        var children = new List<PlanNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var detail = reader.GetString(3);
            string[] warnings = detail.Contains("SCAN", StringComparison.OrdinalIgnoreCase)
                ? ["full table scan"] : [];
            children.Add(new PlanNode(detail, null, null, null, null, null, [], warnings));
        }

        return new PlanNode("QUERY PLAN", null, null, null, null, null, children, []);
    }
}
