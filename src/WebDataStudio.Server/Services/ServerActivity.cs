using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// Work the server is doing right now: an index build at 43%, a vacuum halfway through.
public sealed record RunningOperation(
    string Id, string Kind, string Target, double? PercentComplete, long ElapsedMs, string? Statement);

/// One session waiting for another. The chain is built from these on the client.
public sealed record LockWait(
    string Blocker, string Blocked, string Resource, long WaitMs, string? Statement);

/// A replica and how far behind it is.
public sealed record ReplicaState(string Name, string Role, string State, long? LagBytes, long? LagSeconds);

public sealed record ActivityDto(
    IReadOnlyList<RunningOperation> Operations, IReadOnlyList<LockWait> Waits);

/// "What is going on right now" per engine, as the queries each one answers it with. Kept in one
/// place rather than in the drivers: these are monitoring views, not part of reading a schema, and
/// the shape they come back in is the same for all of them.
///
/// An engine with no answer returns nothing rather than an error, and the capability flag keeps the
/// tab from appearing at all.
public static class ServerActivity
{
    public static async Task<ActivityDto> ReadAsync(
        IDbDriver driver, IDbSession session, CancellationToken ct)
    {
        var engine = driver.Info.Id;

        var operations = OperationsSql(engine) is { } operationsSql
            ? await QueryAsync(driver, session, operationsSql, row => new RunningOperation(
                Text(row, 0), Text(row, 1), Text(row, 2), Number(row, 3), (long)(Number(row, 4) ?? 0),
                row.Length > 5 ? row[5]?.ToString() : null), ct)
            : [];

        var waits = WaitsSql(engine) is { } waitsSql
            ? await QueryAsync(driver, session, waitsSql, row => new LockWait(
                Text(row, 0), Text(row, 1), Text(row, 2), (long)(Number(row, 3) ?? 0),
                row.Length > 4 ? row[4]?.ToString() : null), ct)
            : [];

        return new ActivityDto(operations, waits);
    }

    public static async Task<IReadOnlyList<ReplicaState>> ReplicationAsync(
        IDbDriver driver, IDbSession session, CancellationToken ct)
    {
        if (ReplicationSql(driver.Info.Id) is not { } sql) return [];

        return await QueryAsync(driver, session, sql, row => new ReplicaState(
            Text(row, 0), Text(row, 1), Text(row, 2),
            (long?)Number(row, 3), (long?)Number(row, 4)), ct);
    }

    /// Progress of long-running work. Only PostgreSQL and SQL Server report a percentage; MySQL and
    /// Oracle report the statement and its age, which is still the useful half.
    private static string? OperationsSql(string engine) => engine switch
    {
        "postgresql" => """
            SELECT pid::text,
                   'vacuum' AS kind,
                   coalesce(relid::regclass::text, '') AS target,
                   CASE WHEN heap_blks_total > 0
                        THEN round(100.0 * heap_blks_scanned / heap_blks_total, 1)
                        ELSE NULL END AS percent,
                   0 AS elapsed_ms,
                   phase AS statement
              FROM pg_stat_progress_vacuum
            UNION ALL
            SELECT pid::text, 'create index', coalesce(index_relid::regclass::text, ''),
                   CASE WHEN blocks_total > 0
                        THEN round(100.0 * blocks_done / blocks_total, 1)
                        ELSE NULL END,
                   0, phase
              FROM pg_stat_progress_create_index
            UNION ALL
            SELECT pid::text, 'query', coalesce(datname, ''), NULL,
                   (extract(epoch FROM (clock_timestamp() - query_start)) * 1000)::bigint,
                   left(query, 400)
              FROM pg_stat_activity
             WHERE state = 'active' AND pid <> pg_backend_pid()
                   AND query_start < clock_timestamp() - interval '1 second'
            """,

        "sqlserver" => """
            SELECT CAST(r.session_id AS varchar(20)),
                   r.command,
                   COALESCE(DB_NAME(r.database_id), ''),
                   NULLIF(r.percent_complete, 0),
                   CAST(r.total_elapsed_time AS bigint),
                   SUBSTRING(t.text, 1, 400)
              FROM sys.dm_exec_requests r
              CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
             WHERE r.session_id <> @@SPID
            """,

        "mysql" => """
            SELECT CAST(ID AS CHAR), COMMAND, COALESCE(DB, ''), NULL,
                   TIME * 1000, LEFT(COALESCE(INFO, ''), 400)
              FROM information_schema.PROCESSLIST
             WHERE COMMAND <> 'Sleep' AND ID <> CONNECTION_ID()
            """,

        "oracle" => """
            SELECT TO_CHAR(sid), opname, COALESCE(target, ' '),
                   CASE WHEN totalwork > 0 THEN ROUND(sofar / totalwork * 100, 1) ELSE NULL END,
                   elapsed_seconds * 1000, message
              FROM v$session_longops
             WHERE sofar < totalwork
            """,

        _ => null,
    };

    /// Who is waiting for whom. This is the question a locked application asks, and every engine
    /// spells the answer differently.
    private static string? WaitsSql(string engine) => engine switch
    {
        "postgresql" => """
            SELECT blocker.pid::text AS blocker,
                   waiting.pid::text AS blocked,
                   coalesce(waiting.wait_event_type || ':' || waiting.wait_event, 'lock') AS resource,
                   (extract(epoch FROM (clock_timestamp() - waiting.query_start)) * 1000)::bigint,
                   left(waiting.query, 400)
              FROM pg_stat_activity waiting
              CROSS JOIN LATERAL unnest(pg_blocking_pids(waiting.pid)) AS blocker(pid)
            """,

        "sqlserver" => """
            SELECT CAST(r.blocking_session_id AS varchar(20)),
                   CAST(r.session_id AS varchar(20)),
                   COALESCE(r.wait_type, 'lock'),
                   CAST(r.wait_time AS bigint),
                   SUBSTRING(t.text, 1, 400)
              FROM sys.dm_exec_requests r
              CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
             WHERE r.blocking_session_id <> 0
            """,

        "mysql" => """
            SELECT CAST(blocking_pid AS CHAR), CAST(waiting_pid AS CHAR),
                   'lock', 0, LEFT(COALESCE(waiting_query, ''), 400)
              FROM sys.innodb_lock_waits
            """,

        _ => null,
    };

    private static string? ReplicationSql(string engine) => engine switch
    {
        "postgresql" => """
            SELECT coalesce(application_name, client_addr::text, 'replica') AS name,
                   'replica' AS role,
                   coalesce(state, 'unknown') AS state,
                   coalesce(pg_wal_lsn_diff(sent_lsn, replay_lsn), 0)::bigint AS lag_bytes,
                   coalesce(extract(epoch FROM replay_lag)::bigint, 0) AS lag_seconds
              FROM pg_stat_replication
            """,

        "mysql" => """
            SELECT CHANNEL_NAME, 'replica', SERVICE_STATE, NULL, NULL
              FROM performance_schema.replication_connection_status
            """,

        "sqlserver" => """
            SELECT CAST(rs.replica_id AS varchar(64)),
                   CASE rs.is_primary_replica WHEN 1 THEN 'primary' ELSE 'replica' END,
                   COALESCE(rs.synchronization_state_desc, 'unknown'),
                   CAST(rs.log_send_queue_size AS bigint),
                   NULL
              FROM sys.dm_hadr_database_replica_states rs
            """,

        _ => null,
    };

    private static async Task<List<T>> QueryAsync<T>(
        IDbDriver driver, IDbSession session, string sql, Func<object?[], T> read, CancellationToken ct)
    {
        var rows = new List<T>();

        try
        {
            await foreach (var chunk in driver.ExecuteAsync(session, new ScriptRequest(sql, 500, 30), ct))
            {
                if (chunk is not ResultChunk.Rows batch) continue;
                foreach (var row in batch.Items) rows.Add(read(row));
            }
        }
        catch (Exception)
        {
            // A monitoring view the user cannot read is a permission problem, not a broken studio:
            // the panel shows what it could get.
        }

        return rows;
    }

    private static string Text(object?[] row, int index) =>
        index < row.Length ? row[index]?.ToString() ?? "" : "";

    private static double? Number(object?[] row, int index) =>
        index < row.Length && row[index] is { } value && double.TryParse(
            value.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
