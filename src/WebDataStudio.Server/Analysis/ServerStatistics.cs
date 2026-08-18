using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

public sealed record ServerMetric(string Name, string Value, string? Detail);
public sealed record BlockingEntry(string SessionId, string BlockedBy, string Query, long WaitMs);
public sealed record SlowQuery(string Query, long Calls, double TotalMs, double MeanMs);

/// One normalised shape for every engine, so the UI never branches on the driver.
public static class ServerStatistics
{
    public static async Task<object> ReadAsync(IDbDriver driver, IDbSession session, CancellationToken ct)
    {
        var metrics = new List<ServerMetric>();
        var blocking = new List<BlockingEntry>();

        switch (driver.Info.Id)
        {
            case "postgresql":
                metrics.AddRange(await MetricsAsync(session, """
                    SELECT 'Connections', count(*)::text, 'active: ' ||
                           count(*) FILTER (WHERE state = 'active')::text
                      FROM pg_stat_activity
                    UNION ALL
                    SELECT 'Cache hit ratio',
                           round(100.0 * sum(blks_hit) / NULLIF(sum(blks_hit + blks_read), 0), 1)::text || '%',
                           NULL
                      FROM pg_stat_database
                    UNION ALL
                    SELECT 'Locks waiting', count(*)::text, NULL FROM pg_locks WHERE NOT granted
                    """, ct));

                blocking.AddRange(await BlockingAsync(session, """
                    SELECT a.pid::text, blocking.pid::text, left(a.query, 200),
                           EXTRACT(milliseconds FROM (now() - a.query_start))::bigint
                      FROM pg_stat_activity a
                      JOIN LATERAL unnest(pg_blocking_pids(a.pid)) AS blocking(pid) ON true
                    """, ct));
                break;

            case "mysql":
                metrics.AddRange(await MetricsAsync(session, """
                    SELECT 'Connections', variable_value, NULL
                      FROM performance_schema.global_status WHERE variable_name = 'Threads_connected'
                    UNION ALL
                    SELECT 'Queries', variable_value, NULL
                      FROM performance_schema.global_status WHERE variable_name = 'Queries'
                    UNION ALL
                    SELECT 'Slow queries', variable_value, NULL
                      FROM performance_schema.global_status WHERE variable_name = 'Slow_queries'
                    """, ct));
                break;

            case "sqlserver":
                metrics.AddRange(await MetricsAsync(session, """
                    SELECT 'Connections', CAST(COUNT(*) AS varchar), NULL FROM sys.dm_exec_sessions
                    UNION ALL
                    SELECT 'Buffer cache hit ratio', CAST(cntr_value AS varchar), NULL
                      FROM sys.dm_os_performance_counters
                     WHERE counter_name LIKE 'Buffer cache hit ratio%'
                       AND object_name LIKE '%Buffer Manager%'
                    UNION ALL
                    SELECT 'Blocked requests', CAST(COUNT(*) AS varchar), NULL
                      FROM sys.dm_exec_requests WHERE blocking_session_id <> 0
                    """, ct));

                blocking.AddRange(await BlockingAsync(session, """
                    SELECT CAST(r.session_id AS varchar), CAST(r.blocking_session_id AS varchar),
                           LEFT(t.text, 200), CAST(r.wait_time AS bigint)
                      FROM sys.dm_exec_requests r
                     CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
                     WHERE r.blocking_session_id <> 0
                    """, ct));
                break;
        }

        return new { metrics, blocking };
    }

    public static async Task<IReadOnlyList<SlowQuery>> SlowQueriesAsync(IDbDriver driver, IDbSession session,
        CancellationToken ct)
    {
        var sql = driver.Info.Id switch
        {
            "postgresql" => """
                SELECT left(query, 400), calls, total_exec_time, mean_exec_time
                  FROM pg_stat_statements ORDER BY total_exec_time DESC LIMIT 20
                """,
            "mysql" => """
                SELECT LEFT(digest_text, 400), count_star, sum_timer_wait / 1000000000,
                       avg_timer_wait / 1000000000
                  FROM performance_schema.events_statements_summary_by_digest
                 WHERE digest_text IS NOT NULL
                 ORDER BY sum_timer_wait DESC LIMIT 20
                """,
            "sqlserver" => """
                SELECT TOP 20 LEFT(t.text, 400), s.execution_count,
                       s.total_elapsed_time / 1000.0, s.total_elapsed_time / 1000.0 / s.execution_count
                  FROM sys.dm_exec_query_stats s
                 CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) t
                 ORDER BY s.total_elapsed_time DESC
                """,
            _ => null,
        };

        if (sql is null) return [];

        var result = new List<SlowQuery>();
        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new SlowQuery(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Convert.ToInt64(reader.GetValue(1)),
                    Convert.ToDouble(reader.GetValue(2)),
                    Convert.ToDouble(reader.GetValue(3))));
        }
        catch (DbException e)
        {
            // pg_stat_statements is an extension that may not be installed; say so instead of failing.
            result.Add(new SlowQuery($"-- unavailable: {e.Message}", 0, 0, 0));
        }

        return result;
    }

    private static async Task<List<ServerMetric>> MetricsAsync(IDbSession session, string sql, CancellationToken ct)
    {
        var metrics = new List<ServerMetric>();
        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                metrics.Add(new ServerMetric(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString() ?? "",
                    reader.IsDBNull(2) ? null : reader.GetValue(2).ToString()));
        }
        catch (DbException)
        {
            // A metric the role cannot read is simply absent from the list.
        }
        return metrics;
    }

    private static async Task<List<BlockingEntry>> BlockingAsync(IDbSession session, string sql, CancellationToken ct)
    {
        var entries = new List<BlockingEntry>();
        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                entries.Add(new BlockingEntry(
                    reader.GetValue(0).ToString() ?? "",
                    reader.GetValue(1).ToString() ?? "",
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3))));
        }
        catch (DbException)
        {
            // No permission to see other sessions: report nothing rather than failing the panel.
        }
        return entries;
    }
}
