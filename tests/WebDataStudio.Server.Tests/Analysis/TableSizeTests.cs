using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests.Analysis;

/// Growth from a series of samples. Pure, because the cases that matter are the awkward ones: a
/// table that shrank, one that appeared halfway through, one sampled once.
public class SizeGrowthTests
{
    private static SizeGrowth.Sample Sample(string table, long bytes, int daysAgo) =>
        new("public", table, bytes, 100, DateTimeOffset.UtcNow.AddDays(-daysAgo));

    [Fact]
    public void A_table_with_two_samples_has_a_growth()
    {
        var growth = Assert.Single(SizeGrowth.Between(
            [Sample("events", 1000, 7), Sample("events", 1500, 0)]));

        Assert.Equal(500, growth.Delta);
        Assert.Equal(50, growth.Percent);
        // Bytes a day is the number that says when the disk runs out.
        Assert.Equal(71, growth.PerDay);
    }

    [Fact]
    public void A_table_sampled_once_is_a_size_rather_than_a_growth() =>
        // Saying "0 %" for it would read as "not growing", which is not what is known.
        Assert.Empty(SizeGrowth.Between([Sample("events", 1000, 1)]));

    [Fact]
    public void A_table_that_shrank_is_as_interesting_as_one_that_grew()
    {
        var growth = SizeGrowth.Between(
        [
            Sample("events", 1000, 7), Sample("events", 1100, 0),
            Sample("archive", 9000, 7), Sample("archive", 1000, 0),
        ]);

        // The biggest change first, whichever direction it went.
        Assert.Equal("archive", growth[0].Table);
        Assert.Equal(-8000, growth[0].Delta);
    }

    [Fact]
    public void A_table_that_started_at_nothing_has_no_percentage()
    {
        var growth = Assert.Single(SizeGrowth.Between(
            [Sample("events", 0, 7), Sample("events", 5000, 0)]));

        // "Infinite growth" is a true number and a useless one.
        Assert.Null(growth.Percent);
        Assert.Equal(5000, growth.Delta);
    }

    [Fact]
    public void Two_samples_a_minute_apart_do_not_become_a_daily_rate()
    {
        var now = DateTimeOffset.UtcNow;
        var growth = Assert.Single(SizeGrowth.Between(
        [
            new SizeGrowth.Sample("public", "events", 1000, 1, now.AddMinutes(-1)),
            new SizeGrowth.Sample("public", "events", 2000, 1, now),
        ]));

        // A minute of history extrapolated to a day would report 1.4 GB a day from 1 kB.
        Assert.Equal(0, growth.PerDay);
        Assert.Equal(1000, growth.Delta);
    }

    [Fact]
    public void The_first_and_the_last_sample_are_what_is_compared()
    {
        var growth = Assert.Single(SizeGrowth.Between(
            [Sample("events", 1000, 7), Sample("events", 9999, 4), Sample("events", 1200, 0)]));

        // A spike in the middle is not the story; where it started and where it is now, is.
        Assert.Equal(1000, growth.FirstBytes);
        Assert.Equal(1200, growth.LastBytes);
    }
}

/// Which engines can be asked at all.
public class TableSizeSupportTests
{
    [Theory]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("sqlserver")]
    [InlineData("clickhouse")]
    public void The_engines_with_a_catalogue_for_it(string engine) =>
        Assert.True(TableSizes.Supported(engine));

    [Theory]
    // SQLite's per-table size needs the dbstat module, which is not in every build; the others have
    // no tables at all.
    [InlineData("sqlite")]
    [InlineData("redis")]
    [InlineData("mongodb")]
    [InlineData("storage")]
    public void And_the_ones_that_say_so_instead(string engine) =>
        Assert.False(TableSizes.Supported(engine));
}

/// End to end: every table's size in one query, recorded, and growth once there are two samples.
public class TableSizeEndpointTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-sizes").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE events (id bigserial, payload text);
            INSERT INTO events (payload) SELECT repeat('x', 200) FROM generate_series(1, 2000);
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_PG"] = _container.GetConnectionString(),
                ["WDS_CONN_PG_ENGINE"] = "postgresql",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Every_table_reports_its_size_and_the_look_records_it()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var first = JsonDocument.Parse(
            await client.GetStringAsync($"/api/admin/sizes/{id}", Ct)).RootElement;

        Assert.True(first.GetProperty("available").GetBoolean());

        var events = first.GetProperty("tables").EnumerateArray()
            .Single(table => table.GetProperty("table").GetString() == "events");

        Assert.True(events.GetProperty("bytes").GetInt64() > 100_000);

        // One sample is not a growth yet, and the panel does not pretend otherwise.
        Assert.Empty(first.GetProperty("growth").EnumerateArray());

        // Something arrives, and the next look has two samples to compare.
        await using (var db = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await db.OpenAsync(Ct);
            await using var insert = db.CreateCommand();
            insert.CommandText =
                "INSERT INTO events (payload) SELECT repeat('y', 400) FROM generate_series(1, 4000)";
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var second = JsonDocument.Parse(
            await client.GetStringAsync($"/api/admin/sizes/{id}", Ct)).RootElement;

        var growth = second.GetProperty("growth").EnumerateArray()
            .Single(entry => entry.GetProperty("table").GetString() == "events");

        Assert.True(growth.GetProperty("delta").GetInt64() > 0);
    }

    [Fact]
    public async Task An_engine_without_a_size_per_table_says_so()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds-sqlite.db"),
                ["WDS_CONN_LOCAL"] = "sqlite:///" + Path.Combine(_dir, "demo.db").Replace('\\', '/'),
            })));

        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var report = JsonDocument.Parse(
            await client.GetStringAsync($"/api/admin/sizes/{id}", Ct)).RootElement;

        Assert.False(report.GetProperty("available").GetBoolean());
        Assert.Contains("size per table", report.GetProperty("reason").GetString());
    }
}

/// The samples themselves: written, read back, and trimmed so the file does not grow forever.
public class SizeSampleStoreTests
{
    [Fact]
    public void Samples_are_written_read_and_trimmed()
    {
        var directory = Directory.CreateTempSubdirectory("wds-samples").FullName;

        try
        {
            var store = new WorkspaceStore(Path.Combine(directory, "wds.db"));

            store.AddSizeSamples("c1", [("public", "events", 1000, 10)]);
            store.AddSizeSamples("c1", [("public", "events", 2000, 20)]);
            store.AddSizeSamples("c2", [("public", "other", 5000, 1)]);

            var mine = store.ListSizeSamples("c1", DateTimeOffset.UtcNow.AddDays(-1));

            Assert.Equal(2, mine.Count);
            Assert.All(mine, sample => Assert.Equal("events", sample.Table));

            // Nothing is older than a day, so nothing is trimmed; a year of daily samples is a
            // history, ten years of them is a habit nobody chose.
            Assert.Equal(0, store.TrimSizeSamples(TimeSpan.FromDays(1)));
            Assert.Equal(3, store.TrimSizeSamples(TimeSpan.Zero));
        }
        finally
        {
            TestDirectory.Remove(directory);
        }
    }
}
