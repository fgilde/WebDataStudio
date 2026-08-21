using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;
using WebDataStudio.Server.Mcp;

namespace WebDataStudio.Server.Services;

/// One scheduled query, as the file describes it.
public sealed record ScheduledQuery(
    string Name, string Connection, string Sql, int? EveryMinutes, string? DailyAtUtc,
    string? Format, int? MaxRows);

/// What a job did the last time it ran.
public sealed record ScheduleRun(
    string Name, DateTimeOffset At, long Rows, string? File, string? Error);

/// Where the schedule is, and where its files go. Off without a schedule file: a studio that runs
/// queries on its own without being asked is not one anybody should deploy.
public sealed record ScheduleOptions(bool Configured, string File, string OutputDirectory)
{
    public static ScheduleOptions FromConfiguration(IConfiguration config)
    {
        var file = config["WDS_SCHEDULE_FILE"]?.Trim();
        var output = config["WDS_SCHEDULE_OUTPUT_DIR"]?.Trim();

        return string.IsNullOrEmpty(file)
            ? new ScheduleOptions(false, "", "")
            : new ScheduleOptions(true, file,
                string.IsNullOrEmpty(output) ? "/data/exports" : output);
    }
}

/// Runs the queries a deployment asked for on a schedule and writes the results as files — the
/// nightly report nobody wants to remember to run.
///
/// Reading only: a scheduled statement goes through the same guard `run_query` uses, so a schedule
/// file cannot become a way to run a `DELETE` at 03:00 every night.
public sealed class ScheduledQueries(
    ScheduleOptions options, ConnectionRegistry registry, SessionFactory factory,
    MaskPolicyStore policies, ExporterRegistry exporters, HealthAlertSink alerts,
    ILogger<ScheduledQueries> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, ScheduleRun> _runs = [];
    private readonly Dictionary<string, DateTimeOffset> _lastStarted = [];

    public bool Configured => options.Configured;

    public IReadOnlyList<ScheduleRun> Runs => [.. _runs.Values.OrderBy(run => run.Name, StringComparer.Ordinal)];

    /// The schedule as the file has it. Read on every sweep, so editing the file does not need a
    /// restart.
    public IReadOnlyList<ScheduledQuery> Read()
    {
        if (!options.Configured || !File.Exists(options.File)) return [];

        try
        {
            var jobs = JsonSerializer.Deserialize<List<ScheduledQuery>>(
                File.ReadAllText(options.File), Json) ?? [];

            return [.. jobs.Where(job =>
                !string.IsNullOrWhiteSpace(job.Name)
                && !string.IsNullOrWhiteSpace(job.Connection)
                && !string.IsNullOrWhiteSpace(job.Sql))];
        }
        catch (Exception e)
        {
            log.LogWarning(e, "the schedule file {File} could not be read", options.File);
            return [];
        }
    }

    /// Runs whatever is due. Returns how many jobs ran.
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        var ran = 0;

        foreach (var job in Read())
        {
            if (!IsDue(job, now)) continue;

            _lastStarted[job.Name] = now;
            await RunAsync(job, ct);
            ran++;
        }

        return ran;
    }

    /// One job, now, whether it is due or not — the button behind "run it again".
    public async Task<ScheduleRun> RunAsync(ScheduledQuery job, CancellationToken ct)
    {
        var run = await ExecuteAsync(job, ct);
        _runs[job.Name] = run;

        if (run.Error is not null)
            log.LogWarning("the scheduled query {Name} failed: {Error}", job.Name, run.Error);
        else
            log.LogInformation("{Name} wrote {Rows} rows to {File}", job.Name, run.Rows, run.File);

        // Worth a message when something is watching: a report that stopped producing rows is the
        // kind of thing nobody notices for a month.
        if (run.Error is not null && alerts.Configured)
            await alerts.PostAsync(new
            {
                text = $"*{job.Name}* — the scheduled query failed: {run.Error}",
                studio = "webdatastudio",
                kind = "schedule-failed",
                job = job.Name,
            }, ct);

        return run;
    }

    private async Task<ScheduleRun> ExecuteAsync(ScheduledQuery job, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var spec = registry.All().FirstOrDefault(candidate =>
            candidate.Name.Equals(job.Connection, StringComparison.OrdinalIgnoreCase)
            || candidate.Id.Equals(job.Connection, StringComparison.OrdinalIgnoreCase));

        if (spec is null)
            return new ScheduleRun(job.Name, now, 0, null, $"there is no connection '{job.Connection}'");

        // The same rule the read-only tools use: a schedule file must not become a way to run a
        // DELETE every night.
        if (!ReadOnlyStatement.Looks(job.Sql))
            return new ScheduleRun(job.Name, now, 0, null,
                "a scheduled query has to be a single reading statement");

        try
        {
            var (driver, session) = await factory.OpenAsync(spec.Id, ct);
            await using (session)
            {
                var format = string.IsNullOrWhiteSpace(job.Format) ? "csv" : job.Format.Trim();
                var exporter = exporters.Get(format);

                Directory.CreateDirectory(options.OutputDirectory);

                var stamp = now.ToString("yyyyMMdd-HHmmss");
                var file = Path.Combine(options.OutputDirectory,
                    $"{Sanitise(job.Name)}-{stamp}.{exporter.FileExtension}");

                var request = new ScriptRequest(job.Sql, job.MaxRows ?? 100_000, 300);
                var rows = 0L;
                string? error = null;

                // Counted on the way past, and masked on the way past: a report is a file that
                // leaves the machine.
                var chunks = Count(
                    Masking.Stream(driver.ExecuteAsync(session, request, ct), policies.For(spec.Id), ct),
                    counted => rows += counted, failure => error = failure);

                await using (var stream = File.Create(file))
                    await exporter.WriteAsync(stream, chunks,
                        ExportOptions.Default with { Dialect = driver.Dialect, TableName = job.Name }, ct);

                if (error is not null)
                {
                    File.Delete(file);
                    return new ScheduleRun(job.Name, now, 0, null, error);
                }

                return new ScheduleRun(job.Name, now, rows, file, null);
            }
        }
        catch (Exception e)
        {
            return new ScheduleRun(job.Name, now, 0, null, e.Message);
        }
    }

    private static async IAsyncEnumerable<ResultChunk> Count(
        IAsyncEnumerable<ResultChunk> chunks, Action<long> rows, Action<string> error)
    {
        await foreach (var chunk in chunks)
        {
            switch (chunk)
            {
                case ResultChunk.Rows page: rows(page.Items.Count); break;
                case ResultChunk.Error failure: error(failure.Text); break;
            }

            yield return chunk;
        }
    }

    /// Two ways to say when: every N minutes, or once a day at a time in UTC. No cron parser —
    /// nobody asked the studio to be a scheduler, only to run a report.
    private bool IsDue(ScheduledQuery job, DateTimeOffset now)
    {
        var last = _lastStarted.TryGetValue(job.Name, out var started) ? started : (DateTimeOffset?)null;

        if (job.EveryMinutes is { } minutes and > 0)
            return last is null || now - last >= TimeSpan.FromMinutes(minutes);

        if (job.DailyAtUtc is { Length: > 0 } daily && TimeOnly.TryParse(daily, out var at))
        {
            // Due when the clock has passed the time today and this job has not run today.
            var today = new DateTimeOffset(now.UtcDateTime.Date + at.ToTimeSpan(), TimeSpan.Zero);
            return now >= today && (last is null || last < today);
        }

        return false;
    }

    private static string Sanitise(string name) =>
        new([.. name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)]);
}

/// Checks the schedule once a minute. Cheap: reading a small file and comparing times.
public sealed class ScheduledQueryRunner(ScheduledQueries queries, ILogger<ScheduledQueryRunner> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!queries.Configured) return;

        // Not immediately: a studio that starts by running every job would make a restart
        // expensive, and a job due "every 60 minutes" does not mean "now".
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queries.SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "the schedule sweep failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
