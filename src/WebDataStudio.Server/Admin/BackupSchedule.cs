using System.Text.Json;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Admin;

/// One scheduled backup, as the file describes it.
///
/// The same two ways of saying when a scheduled query has — every N minutes, or once a day at a
/// time in UTC. No cron parser: nobody asked the studio to be a scheduler, only to take the dump
/// somebody would otherwise take by hand every morning.
public sealed record ScheduledBackup(
    string Name, string Connection, int? EveryMinutes, string? DailyAtUtc,
    /// plain, custom or tar, where the engine's tool has a choice.
    string? Format = null,
    bool SchemaOnly = false,
    /// How many files to keep for this job. The oldest go once there are more, because a volume
    /// that fills up is how a backup schedule stops being one.
    int Keep = 7);

/// What a backup job did the last time it ran.
public sealed record BackupRun(
    string Name, DateTimeOffset At, string? File, long Bytes, string? Error);

/// Where the schedule is and where the dumps go. Off without a file: a studio that shells out to
/// pg_dump on its own without being asked is not one anybody should deploy.
public sealed record BackupScheduleOptions(bool Configured, string File, string OutputDirectory)
{
    public static BackupScheduleOptions FromConfiguration(IConfiguration config)
    {
        var file = config["WDS_BACKUP_SCHEDULE_FILE"]?.Trim();
        var output = config["WDS_BACKUP_DIR"]?.Trim();

        return string.IsNullOrEmpty(file)
            ? new BackupScheduleOptions(false, "", "")
            : new BackupScheduleOptions(true, file,
                string.IsNullOrEmpty(output) ? "/data/backups" : output);
    }
}

/// Takes the backups a deployment asked for, on a schedule.
///
/// The studio could already take a dump on request; this is the half that makes a dev stack
/// something you can leave running. Everything about *how* each engine is dumped stays in
/// BackupService — this only decides when, where, and how many to keep.
public sealed class BackupSchedule(
    BackupScheduleOptions options, SessionFactory factory, ILogger<BackupSchedule> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, BackupRun> _runs = [];
    private readonly Dictionary<string, DateTimeOffset> _lastStarted = [];

    public bool Configured => options.Configured;

    public IReadOnlyList<BackupRun> Runs =>
        [.. _runs.Values.OrderBy(run => run.Name, StringComparer.Ordinal)];

    /// The schedule as the file has it. Read on every sweep, so editing it does not need a restart.
    public IReadOnlyList<ScheduledBackup> Read()
    {
        if (!options.Configured || !File.Exists(options.File)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<ScheduledBackup>>(
                File.ReadAllText(options.File), Json) ?? [];
        }
        catch (Exception e)
        {
            log.LogWarning(e, "the backup schedule at {File} could not be read", options.File);
            return [];
        }
    }

    public async Task SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var job in Read())
        {
            if (job.Name is not { Length: > 0 } || job.Connection is not { Length: > 0 }) continue;
            if (!IsDue(job, now)) continue;

            _lastStarted[job.Name] = now;

            try
            {
                _runs[job.Name] = await RunAsync(job, now, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                log.LogWarning(e, "the backup job {Job} failed", job.Name);
                _runs[job.Name] = new BackupRun(job.Name, now, null, 0, e.Message);
            }
        }
    }

    private async Task<BackupRun> RunAsync(ScheduledBackup job, DateTimeOffset now,
        CancellationToken ct)
    {
        var (driver, session) = await factory.OpenAsync(job.Connection, ct);
        await using (session)
        {
            var plan = BackupService.Plan(driver, session.Spec,
                new BackupOptions(job.SchemaOnly, false, null, job.Format));

            if (!BackupService.ToolAvailable(plan.File))
                return new BackupRun(job.Name, now, null, 0,
                    $"'{plan.File}' is not installed in this container");

            Directory.CreateDirectory(options.OutputDirectory);

            var path = Path.Combine(options.OutputDirectory,
                $"{Sanitise(job.Name)}-{now.UtcDateTime:yyyyMMdd-HHmmss}.{plan.Extension}");

            long written;

            await using (var file = File.Create(path))
            {
                // The same counting the download does: a tool that fails before writing anything
                // would otherwise leave a zero-byte file that looks like an empty database.
                await using var counted = new CountingStream(file);
                var result = await ProcessRunner.RunAsync(plan.File, plan.Arguments, plan.Environment,
                    counted, ct);

                written = counted.Written;

                if (result.ExitCode != 0)
                {
                    log.LogWarning("backup tool {Tool} exited with {Code}: {Error}",
                        plan.File, result.ExitCode, result.StandardError);

                    await counted.FlushAsync(ct);
                    file.Close();
                    File.Delete(path);

                    return new BackupRun(job.Name, now, null, written,
                        $"{plan.File} exited with {result.ExitCode}: {result.StandardError}".Trim());
                }
            }

            Prune(job);

            return new BackupRun(job.Name, now, path, written, null);
        }
    }

    /// Keeps the newest files of this job and removes the rest. Only this job's own files: two
    /// schedules writing into one directory should not delete each other's dumps.
    private void Prune(ScheduledBackup job)
    {
        if (job.Keep <= 0) return;

        try
        {
            var mine = new DirectoryInfo(options.OutputDirectory)
                .GetFiles($"{Sanitise(job.Name)}-*")
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(job.Keep);

            foreach (var file in mine) file.Delete();
        }
        catch (Exception e)
        {
            // A dump that was taken and not pruned is still a dump.
            log.LogWarning(e, "old backups of {Job} could not be removed", job.Name);
        }
    }

    private bool IsDue(ScheduledBackup job, DateTimeOffset now)
    {
        var last = _lastStarted.TryGetValue(job.Name, out var started) ? started : (DateTimeOffset?)null;

        if (job.EveryMinutes is { } minutes and > 0)
            return last is null || now - last >= TimeSpan.FromMinutes(minutes);

        if (job.DailyAtUtc is { Length: > 0 } daily && TimeOnly.TryParse(daily, out var at))
        {
            var today = new DateTimeOffset(now.UtcDateTime.Date + at.ToTimeSpan(), TimeSpan.Zero);
            return now >= today && (last is null || last < today);
        }

        return false;
    }

    private static string Sanitise(string name) =>
        new([.. name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)]);
}

/// Checks the backup schedule once a minute. Cheap: reading a small file and comparing times.
public sealed class BackupScheduleRunner(BackupSchedule schedule, ILogger<BackupScheduleRunner> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!schedule.Configured) return;

        // Not immediately: a studio that starts by dumping every database would make a restart
        // expensive, and "every 24 hours" does not mean "now".
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await schedule.SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "the backup sweep failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
