using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Admin;
using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Analysis;

/// The capture says what ran; the index advisor says what one statement would like. Together they
/// answer the question somebody has after watching a server for a minute.
public class CaptureAdvisorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly PostgreSqlDriver _driver = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-advice").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE events (id bigserial PRIMARY KEY, kind text, payload text);
            INSERT INTO events (kind, payload) SELECT 'click', 'x' FROM generate_series(1, 500);
            CREATE TABLE people (id int PRIMARY KEY, city text);
            CREATE INDEX ix_people_city ON people (city);
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private async Task<IDbSession> OpenAsync() =>
        await _driver.OpenAsync(new ConnectionSpec("t", "pg", "postgresql",
            _container.GetConnectionString(), false, null, null, ConnectionSource.Stored), Ct);

    private static CapturedStatement Seen(string sql, int samples, long slowestMs) =>
        new(sql, samples, slowestMs, DateTimeOffset.UtcNow.AddSeconds(-samples),
            DateTimeOffset.UtcNow, ["42"], ["reports"], ["shop"], false);

    [Fact]
    public async Task A_captured_statement_that_scans_a_table_gets_the_index_it_wants()
    {
        await using var session = await OpenAsync();

        var advice = await CaptureAdvisor.SuggestAsync(_driver, session,
            [Seen("SELECT * FROM events WHERE kind = 'click'", 3, 2400)], Ct);

        var entry = Assert.Single(advice);

        Assert.Equal("events", entry.Table);
        Assert.Contains("kind", entry.Message);
        Assert.Contains("CREATE INDEX", entry.Sql);
        Assert.Equal(2400, entry.SlowestMs);
    }

    [Fact]
    public async Task Two_statements_that_want_the_same_index_are_one_piece_of_advice()
    {
        await using var session = await OpenAsync();

        var advice = await CaptureAdvisor.SuggestAsync(_driver, session,
        [
            Seen("SELECT * FROM events WHERE kind = 'click'", 3, 900),
            Seen("SELECT count(*) FROM events WHERE kind = 'view'", 5, 2400),
        ], Ct);

        var entry = Assert.Single(advice);

        Assert.Equal(2, entry.Statements);
        Assert.Equal(8, entry.Samples);
        // The example is the slowest of them, which is the one worth reading.
        Assert.Contains("count(*)", entry.Example);
    }

    [Fact]
    public async Task A_column_that_is_already_indexed_gets_no_advice()
    {
        await using var session = await OpenAsync();

        var advice = await CaptureAdvisor.SuggestAsync(_driver, session,
            [Seen("SELECT * FROM people WHERE city = 'london'", 2, 100)], Ct);

        Assert.Empty(advice);
    }

    [Fact]
    public async Task A_statement_that_names_no_table_of_ours_is_skipped()
    {
        await using var session = await OpenAsync();

        // The studio's own polling, a vacuum, somebody's `SELECT 1`: nothing to advise about.
        Assert.Empty(await CaptureAdvisor.SuggestAsync(_driver, session,
            [Seen("SELECT 1", 10, 5), Seen("VACUUM", 1, 900)], Ct));
    }

    [Fact]
    public async Task An_empty_capture_is_an_empty_answer() =>
        Assert.Empty(await CaptureAdvisor.SuggestAsync(_driver, await OpenAsync(), [], Ct));

    [Fact]
    public async Task The_advice_endpoint_says_when_nothing_has_been_captured()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_PG"] = _container.GetConnectionString(),
                ["WDS_CONN_PG_ENGINE"] = "postgresql",
            })));

        using var client = factory.CreateClient();

        using var connections = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", Ct));
        var id = connections.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;

        var answer = JsonDocument.Parse(
            await client.GetStringAsync($"/api/admin/capture/{id}/advice", Ct)).RootElement;

        Assert.Equal("none", answer.GetProperty("state").GetString());
        Assert.Contains("nothing has been captured", answer.GetProperty("reason").GetString());
        Assert.Empty(answer.GetProperty("advice").EnumerateArray());
    }
}
