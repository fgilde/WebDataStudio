using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Admin;

/// One scheduled job, whatever the engine calls it: a SQL Server Agent job, a pg_cron entry, a MySQL
/// event. The shape is the same because the question is the same — what runs, when, and did it work.
public sealed record JobEntry(
    string Id,
    string Name,
    bool Enabled,
    string Schedule,
    DateTimeOffset? LastRun,
    string? LastOutcome,
    DateTimeOffset? NextRun,
    string? Command);

/// One run of a job. MySQL keeps no history — it remembers only the last execution — and says so
/// rather than inventing rows.
public sealed record JobRun(
    DateTimeOffset? Started,
    DateTimeOffset? Finished,
    string Outcome,
    long? DurationMs,
    string? Message);

/// What a job can be asked to do here, as a statement rather than an execution: the studio's rule is
/// that anything that changes something is read before it runs.
public sealed record JobAction(string Id, string Label, bool Destructive);

public static class JobService
{
    /// Where the jobs live per engine, and what they are called there.
    public static string? SchedulerOf(string engine) => engine switch
    {
        "sqlserver" => "SQL Server Agent",
        "postgresql" => "pg_cron",
        "mysql" => "events",
        _ => null,
    };

    public static async Task<IReadOnlyList<JobEntry>> ListAsync(
        IDbDriver driver, IDbSession session, CancellationToken ct)
    {
        var sql = driver.Info.Id switch
        {
            // sysjobactivity carries the current run and the next scheduled one; the outcome of the
            // last run is in sysjobhistory at step 0, which is the job rather than one of its steps.
            "sqlserver" => """
                SELECT CAST(j.job_id AS varchar(36)), j.name, j.enabled,
                       COALESCE(sc.name, ''),
                       a.last_executed_step_date, h.run_status, a.next_scheduled_run_date,
                       COALESCE(st.command, '')
                  FROM msdb.dbo.sysjobs j
                  OUTER APPLY (SELECT TOP 1 js.schedule_id FROM msdb.dbo.sysjobschedules js
                                WHERE js.job_id = j.job_id) x
                  LEFT JOIN msdb.dbo.sysschedules sc ON sc.schedule_id = x.schedule_id
                  OUTER APPLY (SELECT TOP 1 * FROM msdb.dbo.sysjobactivity act
                                WHERE act.job_id = j.job_id
                                ORDER BY act.start_execution_date DESC) a
                  OUTER APPLY (SELECT TOP 1 hi.run_status FROM msdb.dbo.sysjobhistory hi
                                WHERE hi.job_id = j.job_id AND hi.step_id = 0
                                ORDER BY hi.instance_id DESC) h
                  OUTER APPLY (SELECT TOP 1 s.command FROM msdb.dbo.sysjobsteps s
                                WHERE s.job_id = j.job_id ORDER BY s.step_id) st
                 ORDER BY j.name
                """,
            // pg_cron: one row per schedule, and the last run comes from its own detail table.
            "postgresql" => """
                SELECT j.jobid::text, COALESCE(j.jobname, j.jobid::text), j.active, j.schedule,
                       d.start_time, d.status, NULL::timestamptz, j.command
                  FROM cron.job j
                  LEFT JOIN LATERAL (
                       SELECT start_time, status FROM cron.job_run_details r
                        WHERE r.jobid = j.jobid ORDER BY r.start_time DESC LIMIT 1) d ON true
                 ORDER BY 2
                """,
            "mysql" => """
                SELECT event_name, event_name, status = 'ENABLED',
                       CASE WHEN interval_value IS NULL THEN COALESCE(CAST(execute_at AS CHAR), 'once')
                            ELSE CONCAT('every ', interval_value, ' ', interval_field) END,
                       last_executed, NULL, starts, event_definition
                  FROM information_schema.events
                 WHERE event_schema = DATABASE()
                 ORDER BY event_name
                """,
            _ => null,
        };

        if (sql is null) return [];

        var jobs = new List<JobEntry>();

        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                jobs.Add(new JobEntry(
                    Text(reader, 0), Text(reader, 1),
                    !reader.IsDBNull(2) && Convert.ToBoolean(reader.GetValue(2)),
                    Text(reader, 3),
                    When(reader, 4),
                    Outcome(driver.Info.Id, reader, 5),
                    When(reader, 6),
                    reader.IsDBNull(7) ? null : Text(reader, 7)));
        }
        catch (DbException)
        {
            // No pg_cron installed, no permission on msdb, the event scheduler off: an empty list is
            // the honest answer, and the panel says which scheduler it looked for.
            return [];
        }

        return jobs;
    }

    public static async Task<IReadOnlyList<JobRun>> HistoryAsync(
        IDbDriver driver, IDbSession session, string id, int limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, 200);

        // The id is a parameter everywhere; nothing from the client is interpolated into this SQL.
        var sql = driver.Info.Id switch
        {
            // run_date and run_time are integers (20260827, 143000) and run_duration is HHMMSS, all
            // three of them decimal-encoded — which is why this looks the way it does.
            "sqlserver" => $"""
                SELECT TOP {take}
                       msdb.dbo.agent_datetime(h.run_date, h.run_time),
                       h.run_status,
                       (h.run_duration / 10000) * 3600000
                         + ((h.run_duration / 100) % 100) * 60000
                         + (h.run_duration % 100) * 1000,
                       h.message
                  FROM msdb.dbo.sysjobhistory h
                 WHERE h.job_id = CAST(@id AS uniqueidentifier) AND h.step_id = 0
                 ORDER BY h.instance_id DESC
                """,
            "postgresql" => $"""
                SELECT start_time, status,
                       (EXTRACT(epoch FROM (end_time - start_time)) * 1000)::bigint, return_message
                  FROM cron.job_run_details
                 WHERE jobid = @id::bigint
                 ORDER BY start_time DESC
                 LIMIT {take}
                """,
            // MySQL keeps the last execution on the event itself and nothing before it.
            "mysql" => """
                SELECT last_executed, status, NULL, NULL
                  FROM information_schema.events
                 WHERE event_schema = DATABASE() AND event_name = @id AND last_executed IS NOT NULL
                """,
            _ => null,
        };

        if (sql is null) return [];

        var runs = new List<JobRun>();

        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            var parameter = command.CreateParameter();
            parameter.ParameterName = driver.Dialect.ParameterPrefix + "id";
            parameter.Value = id;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var started = When(reader, 0);
                var duration = reader.IsDBNull(2) ? (long?)null : Convert.ToInt64(reader.GetValue(2));

                runs.Add(new JobRun(started,
                    started is { } from && duration is { } ms ? from.AddMilliseconds(ms) : null,
                    Outcome(driver.Info.Id, reader, 1) ?? "unknown",
                    duration,
                    reader.IsDBNull(3) ? null : Text(reader, 3)));
            }
        }
        catch (DbException)
        {
            return [];
        }

        return runs;
    }

    /// What this engine's scheduler can be asked to do. Everything here is written as a statement the
    /// person reads before it runs — a job that starts a nightly rebuild is not a one-click button.
    public static IReadOnlyList<JobAction> ActionsFor(string engine) => engine switch
    {
        "sqlserver" =>
        [
            new("enable", "Enable", false),
            new("disable", "Disable", false),
            new("run", "Run now", true),
        ],
        // pg_cron and MySQL have no "run now": the schedule is the only way in, and pretending
        // otherwise would mean running the job body behind the person's back.
        "postgresql" => [new("enable", "Enable", false), new("disable", "Disable", false)],
        "mysql" => [new("enable", "Enable", false), new("disable", "Disable", false)],
        _ => [],
    };

    /// The statement for one action, for a query tab. Null when this engine cannot do it.
    public static string? Statement(IDbDriver driver, string action, JobEntry job)
    {
        var name = job.Name.Replace("'", "''");

        return (driver.Info.Id, action.ToLowerInvariant()) switch
        {
            ("sqlserver", "enable") => $"EXEC msdb.dbo.sp_update_job @job_name = N'{name}', @enabled = 1",
            ("sqlserver", "disable") => $"EXEC msdb.dbo.sp_update_job @job_name = N'{name}', @enabled = 0",
            ("sqlserver", "run") => $"EXEC msdb.dbo.sp_start_job @job_name = N'{name}'",

            ("postgresql", "enable") => $"SELECT cron.alter_job({Numeric(job.Id)}, active := true)",
            ("postgresql", "disable") => $"SELECT cron.alter_job({Numeric(job.Id)}, active := false)",

            ("mysql", "enable") => $"ALTER EVENT {driver.Dialect.QuoteIdentifier(job.Name)} ENABLE",
            ("mysql", "disable") =>
                $"ALTER EVENT {driver.Dialect.QuoteIdentifier(job.Name)} DISABLE",

            _ => null,
        };
    }

    /// A pg_cron job id is a number, and the statement puts it in unquoted — so it is checked here
    /// rather than trusted.
    private static long Numeric(string id) =>
        long.TryParse(id, out var value)
            ? value
            : throw new FormatException($"'{id}' is not a job id");

    private static string Text(DbDataReader reader, int index) =>
        reader.IsDBNull(index) ? "" : reader.GetValue(index).ToString() ?? "";

    private static DateTimeOffset? When(DbDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;

        var value = reader.GetValue(index);

        return value switch
        {
            DateTimeOffset offset => offset,
            DateTime time => new DateTimeOffset(time.ToUniversalTime(), TimeSpan.Zero),
            _ => DateTimeOffset.TryParse(value.ToString(), out var parsed) ? parsed : null,
        };
    }

    /// The engines spell an outcome three ways: a number, a word, and "the event is still enabled".
    private static string? Outcome(string engine, DbDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;

        var value = reader.GetValue(index);

        if (engine == "sqlserver")
            return Convert.ToInt32(value) switch
            {
                0 => "failed",
                1 => "succeeded",
                2 => "retry",
                3 => "cancelled",
                4 => "in progress",
                _ => "unknown",
            };

        // pg_cron says "succeeded"/"failed"/"running"; MySQL says ENABLED/DISABLED, which is a state
        // rather than an outcome and reads as one.
        return value.ToString()?.ToLowerInvariant();
    }
}
