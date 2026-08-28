using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

/// One table, as big as it is now.
public sealed record TableSize(string Schema, string Table, long Bytes, long? Rows);

/// How big every table is, in one query.
///
/// The structure panel can say this for one table, and the treemap says it for whole databases.
/// Neither answers "which table grew this week", and that needs every table at once, cheaply, so it
/// can be recorded on a schedule.
public static class TableSizes
{
    /// Whether this engine can be asked at all. SQLite's per-table size needs the dbstat module,
    /// which is not compiled into every build, so it is left out rather than guessed at.
    public static bool Supported(string engine) => Sql(engine) is not null;

    private static string? Sql(string engine) => engine switch
    {
        "postgresql" => """
            SELECT n.nspname, c.relname, pg_total_relation_size(c.oid), c.reltuples::bigint
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind IN ('r', 'p', 'm')
               AND n.nspname NOT IN ('pg_catalog', 'information_schema')
             ORDER BY 3 DESC
            """,
        "mysql" => """
            SELECT table_schema, table_name,
                   COALESCE(data_length, 0) + COALESCE(index_length, 0), table_rows
              FROM information_schema.tables
             WHERE table_type = 'BASE TABLE'
               AND table_schema NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys')
             ORDER BY 3 DESC
            """,
        "sqlserver" => """
            SELECT s.name, t.name,
                   SUM(p.reserved_page_count) * 8192,
                   SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END)
              FROM sys.dm_db_partition_stats p
              JOIN sys.tables t ON t.object_id = p.object_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
             GROUP BY s.name, t.name
             ORDER BY 3 DESC
            """,
        "clickhouse" => """
            SELECT database, table, sum(bytes_on_disk), sum(rows)
              FROM system.parts
             WHERE active AND database NOT IN ('system', 'information_schema')
             GROUP BY database, table
             ORDER BY 3 DESC
            """,
        _ => null,
    };

    public static async Task<IReadOnlyList<TableSize>> ReadAsync(
        IDbDriver driver, IDbSession session, CancellationToken ct)
    {
        if (Sql(driver.Info.Id) is not { } sql) return [];

        var sizes = new List<TableSize>();

        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                sizes.Add(new TableSize(
                    reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "",
                    reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString() ?? "",
                    reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2)),
                    reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3))));
        }
        catch (DbException)
        {
            // No permission on the catalogue: an empty list, not a failed panel.
            return [];
        }

        return sizes;
    }
}

/// One table's size then and now.
public sealed record TableGrowth(
    string Schema,
    string Table,
    long FirstBytes,
    long LastBytes,
    DateTimeOffset From,
    DateTimeOffset To,
    long? Rows)
{
    public long Delta => LastBytes - FirstBytes;

    /// The change as a percentage of where it started, or null for a table that started at nothing —
    /// where "infinite growth" would be a true number and a useless one.
    public double? Percent => FirstBytes <= 0 ? null : Math.Round(Delta * 100.0 / FirstBytes, 1);

    /// Bytes a day, which is the number that says when the disk runs out.
    public double PerDay
    {
        get
        {
            var days = (To - From).TotalDays;
            return days < 0.5 ? 0 : Math.Round(Delta / days, 0);
        }
    }
}

/// Growth from a series of samples. Pure: the interesting cases — a table that shrank, one that
/// appeared halfway through, one sampled twice in a minute — are tests without a database.
public static class SizeGrowth
{
    public sealed record Sample(string Schema, string Table, long Bytes, long? Rows,
        DateTimeOffset At);

    public static IReadOnlyList<TableGrowth> Between(IEnumerable<Sample> samples, int top = 25)
    {
        var growth = new List<TableGrowth>();

        foreach (var group in samples.GroupBy(sample => (sample.Schema, sample.Table)))
        {
            var ordered = group.OrderBy(sample => sample.At).ToList();

            // One sample is a size, not a growth: saying "0 %" for it would read as "it is not
            // growing", which is not what is known.
            if (ordered.Count < 2) continue;

            growth.Add(new TableGrowth(group.Key.Schema, group.Key.Table,
                ordered[0].Bytes, ordered[^1].Bytes, ordered[0].At, ordered[^1].At,
                ordered[^1].Rows));
        }

        return growth
            // Biggest change first, and a table that shrank is as interesting as one that grew.
            .OrderByDescending(entry => Math.Abs(entry.Delta))
            .Take(Math.Clamp(top, 1, 500))
            .ToList();
    }
}
