using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The nightly report nobody wants to remember to run. What matters: it only reads, it writes a
/// file, and a schedule that is not due does not run.
public class ScheduledQueryTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-schedule").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, api_key TEXT);
            INSERT INTO customers VALUES (1, 'ada', 'tok-42'), (2, 'grace', 'tok-43');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private string Output => Path.Combine(_dir, "exports");

    private string WriteSchedule(params object[] jobs)
    {
        var file = Path.Combine(_dir, "schedule.json");
        File.WriteAllText(file, JsonSerializer.Serialize(jobs));
        return file;
    }

    private WebApplicationFactory<Program> Factory(string? scheduleFile) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                    ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
                };

                if (scheduleFile is not null)
                {
                    settings["WDS_SCHEDULE_FILE"] = scheduleFile;
                    settings["WDS_SCHEDULE_OUTPUT_DIR"] = Output;
                }

                c.AddInMemoryCollection(settings);
            }));

    [Fact]
    public async Task A_due_job_writes_a_file_with_its_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = WriteSchedule(new
        {
            name = "customers", connection = "SHOP",
            sql = "SELECT id, name FROM customers ORDER BY id",
            everyMinutes = 60, format = "csv",
        });

        using var factory = Factory(file);
        var queries = factory.Services.GetRequiredService<ScheduledQueries>();

        Assert.Equal(1, await queries.SweepAsync(DateTimeOffset.UtcNow, ct));

        var run = Assert.Single(queries.Runs);
        Assert.Null(run.Error);
        Assert.Equal(2, run.Rows);

        var written = await File.ReadAllTextAsync(run.File!, ct);
        Assert.Contains("ada", written);
        Assert.Contains("name", written);
    }

    /// A file is a file that leaves the machine, so the mask policy applies to it too.
    [Fact]
    public async Task A_masked_column_is_masked_in_the_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = WriteSchedule(new
        {
            name = "keys", connection = "SHOP", sql = "SELECT name, api_key FROM customers",
            everyMinutes = 60,
        });

        using var factory = Factory(file);
        var queries = factory.Services.GetRequiredService<ScheduledQueries>();
        await queries.SweepAsync(DateTimeOffset.UtcNow, ct);

        var written = await File.ReadAllTextAsync(queries.Runs[0].File!, ct);

        Assert.DoesNotContain("tok-42", written);
    }

    /// A schedule file must not become a way to run a DELETE at 03:00 every night.
    [Fact]
    public async Task A_writing_statement_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = WriteSchedule(new
        {
            name = "cleanup", connection = "SHOP", sql = "DELETE FROM customers", everyMinutes = 1,
        });

        using var factory = Factory(file);
        var queries = factory.Services.GetRequiredService<ScheduledQueries>();
        await queries.SweepAsync(DateTimeOffset.UtcNow, ct);

        var run = Assert.Single(queries.Runs);
        Assert.Contains("reading statement", run.Error);

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM customers";
        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
    }

    [Fact]
    public async Task An_interval_job_does_not_run_twice_in_its_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = WriteSchedule(new
        {
            name = "hourly", connection = "SHOP", sql = "SELECT 1", everyMinutes = 60,
        });

        using var factory = Factory(file);
        var queries = factory.Services.GetRequiredService<ScheduledQueries>();
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(1, await queries.SweepAsync(now, ct));
        Assert.Equal(0, await queries.SweepAsync(now.AddMinutes(30), ct));
        Assert.Equal(1, await queries.SweepAsync(now.AddMinutes(61), ct));
    }

    [Fact]
    public async Task A_daily_job_runs_once_a_day_after_its_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = WriteSchedule(new
        {
            name = "nightly", connection = "SHOP", sql = "SELECT 1", dailyAtUtc = "03:00",
        });

        using var factory = Factory(file);
        var queries = factory.Services.GetRequiredService<ScheduledQueries>();
        var day = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

        // Before its time: not due.
        Assert.Equal(0, await queries.SweepAsync(day.AddHours(2), ct));
        Assert.Equal(1, await queries.SweepAsync(day.AddHours(3), ct));
        Assert.Equal(0, await queries.SweepAsync(day.AddHours(20), ct));
        Assert.Equal(1, await queries.SweepAsync(day.AddDays(1).AddHours(4), ct));
    }

    [Fact]
    public async Task An_unknown_connection_is_reported_rather_than_thrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = WriteSchedule(new
        {
            name = "nowhere", connection = "NOPE", sql = "SELECT 1", everyMinutes = 1,
        });

        using var factory = Factory(file);
        var queries = factory.Services.GetRequiredService<ScheduledQueries>();
        await queries.SweepAsync(DateTimeOffset.UtcNow, ct);

        Assert.Contains("no connection", queries.Runs[0].Error);
    }

    [Fact]
    public async Task The_endpoint_lists_the_jobs_and_runs_one_on_demand()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = WriteSchedule(new
        {
            name = "on-demand", connection = "SHOP", sql = "SELECT count(*) FROM customers",
            everyMinutes = 1440,
        });

        using var factory = Factory(file);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/schedule", ct);
        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Single(body.GetProperty("jobs").EnumerateArray());

        var run = await client.PostAsync("/api/schedule/on-demand/run", null, ct);
        run.EnsureSuccessStatusCode();

        var result = await run.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(1, result.GetProperty("rows").GetInt64());

        var missing = await client.PostAsync("/api/schedule/nope/run", null, ct);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Without_a_schedule_file_nothing_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(null);
        var client = factory.CreateClient();

        Assert.Equal(0, await factory.Services.GetRequiredService<ScheduledQueries>()
            .SweepAsync(DateTimeOffset.UtcNow, ct));

        var body = await client.GetFromJsonAsync<JsonElement>("/api/schedule", ct);
        Assert.False(body.GetProperty("configured").GetBoolean());
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task A_damaged_schedule_file_is_a_warning_not_a_crash()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dir, "schedule.json");
        await File.WriteAllTextAsync(file, "{ not json", ct);

        using var factory = Factory(file);

        Assert.Equal(0, await factory.Services.GetRequiredService<ScheduledQueries>()
            .SweepAsync(DateTimeOffset.UtcNow, ct));
    }
}
