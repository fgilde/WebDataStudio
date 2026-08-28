using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Two runs of the same query with different values are the same statement. The fingerprint is what
/// makes that true, so it is worth pinning down.
public class QueryFingerprintTests
{
    [Fact]
    public void The_values_are_what_does_not_matter()
    {
        Assert.Equal(
            QueryStats.Fingerprint("SELECT * FROM people WHERE city = 'london'"),
            QueryStats.Fingerprint("SELECT * FROM people WHERE city = 'new york'"));
    }

    [Fact]
    public void And_neither_is_the_length_of_an_in_list() =>
        // Otherwise "the same query" is a different row for every basket size.
        Assert.Equal(
            QueryStats.Fingerprint("SELECT * FROM orders WHERE id IN (1, 2, 3)"),
            QueryStats.Fingerprint("SELECT * FROM orders WHERE id IN (7)"));

    [Fact]
    public void Nor_is_the_whitespace_or_a_trailing_semicolon() =>
        Assert.Equal(
            QueryStats.Fingerprint("SELECT   *\n  FROM people;"),
            QueryStats.Fingerprint("SELECT * FROM people"));

    [Fact]
    public void Nor_a_comment() =>
        Assert.Equal(
            QueryStats.Fingerprint("-- nightly\nSELECT 1"),
            QueryStats.Fingerprint("SELECT 1"));

    [Fact]
    public void Nor_the_name_of_a_bind_parameter() =>
        Assert.Equal(
            QueryStats.Fingerprint("SELECT * FROM people WHERE id = @id"),
            QueryStats.Fingerprint("SELECT * FROM people WHERE id = $person"));

    [Fact]
    public void But_a_different_table_is_a_different_statement() =>
        Assert.NotEqual(
            QueryStats.Fingerprint("SELECT * FROM people"),
            QueryStats.Fingerprint("SELECT * FROM orders"));

    [Fact]
    public void And_a_column_with_a_number_in_its_name_is_not_a_literal() =>
        // `address2` is a column; replacing the 2 would merge it with `address3`.
        Assert.Contains("address2", QueryStats.Fingerprint("SELECT address2 FROM people"));
}

/// The report itself: what ran, how often, and what got slower.
public class QueryStatsTests
{
    private static HistoryEntry Run(string sql, long? elapsed, int minutesAgo, string? error = null) =>
        new(minutesAgo, "c1", sql, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), elapsed, 1, error,
            false);

    [Fact]
    public void Runs_of_one_statement_are_one_row()
    {
        var report = QueryStats.Report(
        [
            Run("SELECT * FROM people WHERE city = 'london'", 100, 30),
            Run("SELECT * FROM people WHERE city = 'lisbon'", 300, 20),
            Run("SELECT * FROM people WHERE city = 'berlin'", 200, 10),
        ]);

        var statement = Assert.Single(report);

        Assert.Equal(3, statement.Runs);
        Assert.Equal(200, statement.AverageMs);
        Assert.Equal(300, statement.SlowestMs);
        Assert.Equal(100, statement.FastestMs);
        // The example is a real statement somebody can open, not the fingerprint.
        Assert.Contains("berlin", statement.Example);
    }

    [Fact]
    public void The_slowest_on_average_comes_first()
    {
        var report = QueryStats.Report(
        [
            Run("SELECT 1", 10, 5),
            Run("SELECT * FROM big_table", 5000, 4),
            Run("SELECT 2", 20, 3),
        ]);

        Assert.Equal("SELECT * FROM big_table", report[0].Fingerprint);
    }

    [Fact]
    public void A_statement_that_got_slower_says_so()
    {
        var report = QueryStats.Report(
        [
            Run("SELECT * FROM orders", 100, 40),
            Run("SELECT * FROM orders", 100, 30),
            Run("SELECT * FROM orders", 200, 20),
            Run("SELECT * FROM orders", 200, 10),
        ]);

        var statement = Assert.Single(report);

        Assert.Equal(2, statement.Trend);
        Assert.Equal("2.0× slower than it was", QueryStats.Describe(statement.Trend));
    }

    [Fact]
    public void And_one_that_got_faster()
    {
        var report = QueryStats.Report(
        [
            Run("SELECT * FROM orders", 400, 40),
            Run("SELECT * FROM orders", 400, 30),
            Run("SELECT * FROM orders", 100, 20),
            Run("SELECT * FROM orders", 100, 10),
        ]);

        Assert.Equal("4.0× faster than it was", QueryStats.Describe(Assert.Single(report).Trend));
    }

    [Fact]
    public void Two_runs_are_not_a_trend()
    {
        // With two measurements, "twice as slow" is noise.
        var report = QueryStats.Report([Run("SELECT 1", 10, 20), Run("SELECT 1", 20, 10)]);

        Assert.Null(Assert.Single(report).Trend);
        Assert.Equal("not enough history", QueryStats.Describe(null));
    }

    [Fact]
    public void A_statement_that_only_ever_failed_is_counted_rather_than_dropped()
    {
        var report = QueryStats.Report(
        [
            Run("SELECT * FROM nope", null, 10, "relation \"nope\" does not exist"),
            Run("SELECT * FROM nope", null, 5, "relation \"nope\" does not exist"),
        ]);

        var statement = Assert.Single(report);

        Assert.Equal(2, statement.Runs);
        Assert.Equal(2, statement.Failures);
        Assert.Equal(0, statement.AverageMs);
    }

    [Fact]
    public void The_first_and_last_time_it_ran_are_both_kept()
    {
        var report = QueryStats.Report([Run("SELECT 1", 10, 100), Run("SELECT 1", 10, 1)]);
        var statement = Assert.Single(report);

        Assert.True(statement.LastSeen > statement.FirstSeen);
    }

    [Fact]
    public void An_empty_history_is_an_empty_report() => Assert.Empty(QueryStats.Report([]));
}

/// End to end: the history endpoint answers with the grouped statements.
public class QueryStatsEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task What_ran_here_comes_back_grouped_and_slowest_first()
    {
        var directory = Directory.CreateTempSubdirectory("wds-stats").FullName;

        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["DB_PATH"] = Path.Combine(directory, "wds.db"),
                    })));

            using var client = factory.CreateClient();

            foreach (var (sql, elapsed) in new (string, int)[]
                     {
                         ("SELECT * FROM people WHERE id = 1", 20),
                         ("SELECT * FROM people WHERE id = 2", 40),
                         ("SELECT count(*) FROM events", 900),
                     })
                (await client.PostAsJsonAsync("/api/history", new
                {
                    connectionId = "c1", sql, elapsedMs = elapsed, rowCount = 1,
                    error = (string?)null,
                }, Ct)).EnsureSuccessStatusCode();

            var report = JsonDocument.Parse(
                await client.GetStringAsync("/api/history/stats?connectionId=c1", Ct)).RootElement;

            Assert.Equal(3, report.GetProperty("runs").GetInt32());

            var statements = report.GetProperty("statements").EnumerateArray().ToList();

            // Two runs of the same query with different ids are one statement, and the 900 ms one
            // is first.
            Assert.Equal(2, statements.Count);
            Assert.Contains("events", statements[0].GetProperty("fingerprint").GetString());
            Assert.Equal(2, statements[1].GetProperty("runs").GetInt32());
            Assert.Equal(30, statements[1].GetProperty("averageMs").GetInt64());
        }
        finally
        {
            TestDirectory.Remove(directory);
        }
    }
}
