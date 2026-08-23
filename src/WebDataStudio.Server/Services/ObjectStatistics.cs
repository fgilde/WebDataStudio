using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One line of the statistics tab: a name, a value, and what the value is.
public sealed record Statistic(string Name, string? Value, string Kind);

/// One index of the object, with what the engine knows about how it is used.
public sealed record IndexStatistic(
    string Name, long? SizeBytes, long? Scans, bool Unique, bool Primary);

public sealed record ObjectStatistics(
    bool Supported, IReadOnlyList<Statistic> Table, IReadOnlyList<IndexStatistic> Indexes);

/// What the engine knows about one table beyond its shape: how big it is, how much of it is dead,
/// when it was last vacuumed or analysed, and which of its indexes anybody actually reads.
///
/// The questions before "should I add an index" and "why is this table 40 GB". Kept in one place per
/// engine, like the activity queries, because the shape of the answer is the same for all of them
/// and only the SQL differs.
public static class ObjectStatisticsReader
{
    public static async Task<ObjectStatistics> ReadAsync(
        IDbDriver driver, IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var schema = target.Path.Count > 1 ? target.Path[0] : null;
        var name = target.Name;

        var table = TableSql(driver.Info.Id) is { } tableSql
            ? await ReadStatisticsAsync(driver, session, tableSql, schema, name, ct)
            : [];

        var indexes = IndexSql(driver.Info.Id) is { } indexSql
            ? await ReadIndexesAsync(driver, session, indexSql, schema, name, ct)
            : [];

        // SQLite answers none of this, and pretending otherwise with empty rows would read as
        // "this table has no indexes".
        return new ObjectStatistics(table.Count > 0 || indexes.Count > 0, table, indexes);
    }

    /// Name, value, kind — three columns, so one shape fits every engine's very different catalog.
    private static string? TableSql(string engine) => engine switch
    {
        "postgresql" => """
            SELECT * FROM (
              SELECT 'Total size' AS name, pg_size_pretty(pg_total_relation_size(c.oid)) AS value, 'size' AS kind, 1 AS ord
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE c.relname = @name AND (@schema IS NULL OR n.nspname = @schema)
              UNION ALL
              SELECT 'Table size', pg_size_pretty(pg_table_size(c.oid)), 'size', 2
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE c.relname = @name AND (@schema IS NULL OR n.nspname = @schema)
              UNION ALL
              SELECT 'Index size', pg_size_pretty(pg_indexes_size(c.oid)), 'size', 3
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE c.relname = @name AND (@schema IS NULL OR n.nspname = @schema)
              UNION ALL
              -- reltuples rather than n_live_tup: ANALYZE sets it there and then, while the
              -- statistics collector reports its own numbers whenever it gets round to it.
              SELECT 'Live rows (estimate)', c.reltuples::bigint::text, 'rows', 4
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE c.relname = @name AND (@schema IS NULL OR n.nspname = @schema)
              UNION ALL
              SELECT 'Dead rows', s.n_dead_tup::text, 'rows', 5
                FROM pg_stat_all_tables s
               WHERE s.relname = @name AND (@schema IS NULL OR s.schemaname = @schema)
              UNION ALL
              SELECT 'Sequential scans', s.seq_scan::text, 'count', 6
                FROM pg_stat_all_tables s
               WHERE s.relname = @name AND (@schema IS NULL OR s.schemaname = @schema)
              UNION ALL
              SELECT 'Index scans', coalesce(s.idx_scan, 0)::text, 'count', 7
                FROM pg_stat_all_tables s
               WHERE s.relname = @name AND (@schema IS NULL OR s.schemaname = @schema)
              UNION ALL
              SELECT 'Last vacuum', coalesce(to_char(greatest(s.last_vacuum, s.last_autovacuum), 'YYYY-MM-DD HH24:MI'), 'never'), 'time', 8
                FROM pg_stat_all_tables s
               WHERE s.relname = @name AND (@schema IS NULL OR s.schemaname = @schema)
              UNION ALL
              SELECT 'Last analyze', coalesce(to_char(greatest(s.last_analyze, s.last_autoanalyze), 'YYYY-MM-DD HH24:MI'), 'never'), 'time', 9
                FROM pg_stat_all_tables s
               WHERE s.relname = @name AND (@schema IS NULL OR s.schemaname = @schema)
            ) stats ORDER BY ord
            """,

        "mysql" => """
            SELECT 'Total size' AS name,
                   concat(round((data_length + index_length) / 1048576, 1), ' MiB') AS value,
                   'size' AS kind
              FROM information_schema.tables
             WHERE table_name = @name AND (@schema IS NULL OR table_schema = @schema)
            UNION ALL
            SELECT 'Table size', concat(round(data_length / 1048576, 1), ' MiB'), 'size'
              FROM information_schema.tables
             WHERE table_name = @name AND (@schema IS NULL OR table_schema = @schema)
            UNION ALL
            SELECT 'Index size', concat(round(index_length / 1048576, 1), ' MiB'), 'size'
              FROM information_schema.tables
             WHERE table_name = @name AND (@schema IS NULL OR table_schema = @schema)
            UNION ALL
            SELECT 'Rows (estimate)', cast(table_rows AS char), 'rows'
              FROM information_schema.tables
             WHERE table_name = @name AND (@schema IS NULL OR table_schema = @schema)
            UNION ALL
            SELECT 'Free space', concat(round(data_free / 1048576, 1), ' MiB'), 'size'
              FROM information_schema.tables
             WHERE table_name = @name AND (@schema IS NULL OR table_schema = @schema)
            UNION ALL
            SELECT 'Engine', engine, 'text'
              FROM information_schema.tables
             WHERE table_name = @name AND (@schema IS NULL OR table_schema = @schema)
            """,

        "sqlserver" => """
            SELECT 'Total size' AS name,
                   cast(cast(sum(a.total_pages) * 8.0 / 1024 AS decimal(18,1)) AS varchar) + ' MiB' AS value,
                   'size' AS kind
              FROM sys.tables t
              JOIN sys.indexes i ON i.object_id = t.object_id
              JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
              JOIN sys.allocation_units a ON a.container_id = p.partition_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
             WHERE t.name = @name AND (@schema IS NULL OR s.name = @schema)
            UNION ALL
            SELECT 'Used size',
                   cast(cast(sum(a.used_pages) * 8.0 / 1024 AS decimal(18,1)) AS varchar) + ' MiB', 'size'
              FROM sys.tables t
              JOIN sys.indexes i ON i.object_id = t.object_id
              JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
              JOIN sys.allocation_units a ON a.container_id = p.partition_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
             WHERE t.name = @name AND (@schema IS NULL OR s.name = @schema)
            UNION ALL
            SELECT 'Rows', cast(sum(CASE WHEN i.index_id IN (0, 1) THEN p.rows ELSE 0 END) AS varchar), 'rows'
              FROM sys.tables t
              JOIN sys.indexes i ON i.object_id = t.object_id
              JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
             WHERE t.name = @name AND (@schema IS NULL OR s.name = @schema)
            """,

        "oracle" => """
            SELECT 'Rows (last analyzed)' AS name, to_char(num_rows) AS value, 'rows' AS kind
              FROM all_tables
             WHERE table_name = upper(:name) AND (:schema IS NULL OR owner = upper(:schema))
            UNION ALL
            SELECT 'Blocks', to_char(blocks), 'count'
              FROM all_tables
             WHERE table_name = upper(:name) AND (:schema IS NULL OR owner = upper(:schema))
            UNION ALL
            SELECT 'Last analyzed', to_char(last_analyzed, 'YYYY-MM-DD HH24:MI'), 'time'
              FROM all_tables
             WHERE table_name = upper(:name) AND (:schema IS NULL OR owner = upper(:schema))
            """,

        _ => null,
    };

    /// name, size, scans, unique, primary — the four things worth knowing about an index.
    private static string? IndexSql(string engine) => engine switch
    {
        "postgresql" => """
            SELECT i.indexrelname,
                   pg_relation_size(i.indexrelid),
                   i.idx_scan,
                   x.indisunique,
                   x.indisprimary
              FROM pg_stat_all_indexes i
              JOIN pg_index x ON x.indexrelid = i.indexrelid
             WHERE i.relname = @name AND (@schema IS NULL OR i.schemaname = @schema)
             ORDER BY pg_relation_size(i.indexrelid) DESC
            """,

        "mysql" => """
            SELECT s.index_name,
                   NULL,
                   NULL,
                   CASE WHEN s.non_unique = 0 THEN 1 ELSE 0 END,
                   CASE WHEN s.index_name = 'PRIMARY' THEN 1 ELSE 0 END
              FROM information_schema.statistics s
             WHERE s.table_name = @name AND (@schema IS NULL OR s.table_schema = @schema)
               AND s.seq_in_index = 1
             ORDER BY s.index_name
            """,

        "sqlserver" => """
            SELECT i.name,
                   sum(a.used_pages) * 8192,
                   coalesce(max(u.user_seeks + u.user_scans + u.user_lookups), 0),
                   CASE WHEN i.is_unique = 1 THEN 1 ELSE 0 END,
                   CASE WHEN i.is_primary_key = 1 THEN 1 ELSE 0 END
              FROM sys.indexes i
              JOIN sys.tables t ON t.object_id = i.object_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
              JOIN sys.allocation_units a ON a.container_id = p.partition_id
              LEFT JOIN sys.dm_db_index_usage_stats u
                     ON u.object_id = i.object_id AND u.index_id = i.index_id
             WHERE t.name = @name AND (@schema IS NULL OR s.name = @schema) AND i.name IS NOT NULL
             GROUP BY i.name, i.is_unique, i.is_primary_key
             ORDER BY 2 DESC
            """,

        _ => null,
    };

    private static async Task<List<Statistic>> ReadStatisticsAsync(
        IDbDriver driver, IDbSession session, string sql, string? schema, string name,
        CancellationToken ct)
    {
        var statistics = new List<Statistic>();

        await foreach (var row in QueryAsync(driver, session, sql, schema, name, ct))
            statistics.Add(new Statistic(
                Text(row, 0) ?? "", Text(row, 1), Text(row, 2) ?? "text"));

        return statistics;
    }

    private static async Task<List<IndexStatistic>> ReadIndexesAsync(
        IDbDriver driver, IDbSession session, string sql, string? schema, string name,
        CancellationToken ct)
    {
        var indexes = new List<IndexStatistic>();

        await foreach (var row in QueryAsync(driver, session, sql, schema, name, ct))
            indexes.Add(new IndexStatistic(
                Text(row, 0) ?? "", Number(row, 1), Number(row, 2), Flag(row, 3), Flag(row, 4)));

        return indexes;
    }

    private static async IAsyncEnumerable<object?[]> QueryAsync(
        IDbDriver driver, IDbSession session, string sql, string? schema, string name,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Parameters, because a table name is data here and a catalog query is exactly where a
        // quoted identifier would be pasted in by hand.
        var request = new ScriptRequest(sql, 500, 30, Parameters: new Dictionary<string, string?>
        {
            ["name"] = name,
            ["schema"] = schema,
        });

        await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
        {
            // A catalog this engine does not have is not an error worth showing: the tab says the
            // engine cannot answer instead.
            if (chunk is ResultChunk.Error) yield break;
            if (chunk is not ResultChunk.Rows rows) continue;

            foreach (var row in rows.Items) yield return row;
        }
    }

    private static string? Text(object?[] row, int index) =>
        row.Length > index ? row[index]?.ToString() : null;

    private static long? Number(object?[] row, int index) =>
        row.Length > index && row[index] is { } value && long.TryParse(value.ToString(), out var parsed)
            ? parsed
            : null;

    private static bool Flag(object?[] row, int index) =>
        row.Length > index && row[index] is { } value
        && (value is bool flag ? flag : value.ToString() is "1" or "True" or "true");
}
