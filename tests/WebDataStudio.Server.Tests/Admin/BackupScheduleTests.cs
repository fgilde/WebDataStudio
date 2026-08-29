using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WebDataStudio.Server.Admin;

namespace WebDataStudio.Server.Tests.Admin;

/// When a scheduled backup is due, where the file goes, and how many are kept. The dumping itself
/// is BackupService's, and shelling out to pg_dump is not what these are about.
public class BackupScheduleTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-backup-schedule").FullName;
    private string _db = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var db = new SqliteConnection($"Data Source={_db}");
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE people (id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private static BackupSchedule Schedule(string file, string output) =>
        new(new BackupScheduleOptions(true, file, output), null!, NullLogger<BackupSchedule>.Instance);

    private string WriteSchedule(string json)
    {
        var path = Path.Combine(_dir, "backups.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Reads_the_jobs_a_deployment_wrote()
    {
        var file = WriteSchedule("""
            [{ "name": "nightly", "connection": "SHOP", "dailyAtUtc": "02:00", "keep": 3 }]
            """);

        var job = Assert.Single(Schedule(file, _dir).Read());

        Assert.Equal("nightly", job.Name);
        Assert.Equal("02:00", job.DailyAtUtc);
        Assert.Equal(3, job.Keep);
    }

    [Fact]
    public void A_schedule_file_that_cannot_be_read_is_no_schedule_rather_than_a_crash()
    {
        var file = WriteSchedule("{ this is not json");
        Assert.Empty(Schedule(file, _dir).Read());
    }

    [Fact]
    public void Without_a_file_the_studio_never_shells_out_to_a_dump_tool()
    {
        var schedule = new BackupSchedule(new BackupScheduleOptions(false, "", ""), null!,
            NullLogger<BackupSchedule>.Instance);

        Assert.False(schedule.Configured);
        Assert.Empty(schedule.Read());
    }

    [Fact]
    public async Task Nothing_is_due_before_its_time_and_a_sweep_of_nothing_writes_nothing()
    {
        // A daily job at 02:00, swept at noon of the same day, has already missed its slot for
        // today only if it never ran — which is the case here, so it *is* due. The one that is not
        // is the one that has not come round yet.
        var file = WriteSchedule("""
            [{ "name": "later", "connection": "SHOP", "dailyAtUtc": "23:59" }]
            """);

        var schedule = Schedule(file, Path.Combine(_dir, "out"));
        await schedule.SweepAsync(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero), Ct);

        Assert.Empty(schedule.Runs);
        Assert.False(Directory.Exists(Path.Combine(_dir, "out")));
    }

    [Fact]
    public async Task Reports_a_job_whose_connection_does_not_exist_rather_than_failing_silently()
    {
        var file = WriteSchedule("""
            [{ "name": "gone", "connection": "NOPE", "everyMinutes": 1 }]
            """);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SHOP"] = $"sqlite:///{_db.Replace('\\', '/')}",
                ["WDS_BACKUP_SCHEDULE_FILE"] = file,
                ["WDS_BACKUP_DIR"] = Path.Combine(_dir, "out"),
            })));

        // Force the services to build.
        using var client = factory.CreateClient();
        var schedule = factory.Services.GetRequiredService<BackupSchedule>();

        await schedule.SweepAsync(DateTimeOffset.UtcNow, Ct);

        var run = Assert.Single(schedule.Runs);
        Assert.Equal("gone", run.Name);
        Assert.NotNull(run.Error);
    }

    [Fact]
    public async Task The_schedule_is_visible_through_the_api()
    {
        var file = WriteSchedule("""
            [{ "name": "nightly", "connection": "SHOP", "dailyAtUtc": "02:00" }]
            """);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SHOP"] = $"sqlite:///{_db.Replace('\\', '/')}",
                ["WDS_BACKUP_SCHEDULE_FILE"] = file,
            })));

        var body = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/backup-schedule", Ct);

        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Equal("nightly", body.GetProperty("jobs")[0].GetProperty("name").GetString());
    }
}
