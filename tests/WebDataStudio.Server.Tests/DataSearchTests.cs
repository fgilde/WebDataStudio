using Npgsql;
using Testcontainers.PostgreSql;
using Microsoft.Data.Sqlite;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// "Find 4711 in any table" — the question the object search cannot answer.
public class DataSearchTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly PostgreSqlDriver _driver = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE people (id int, name text, city text, born date);
            INSERT INTO people VALUES (4711, 'Ada Lovelace', 'london', '1815-12-10'),
                                      (2, 'Grace Hopper', 'new york', '1906-12-09');

            CREATE TABLE orders (id int, reference text, total numeric, placed timestamptz);
            INSERT INTO orders VALUES (1, 'ORD-4711', 99.5, '2026-01-02'),
                                      (2, 'ORD-0815', 4711, '2026-02-03');

            CREATE TABLE pictures (id int, body bytea);
            INSERT INTO pictures VALUES (1, decode('4711', 'hex'));
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private async Task<IDbSession> OpenAsync() =>
        await _driver.OpenAsync(new ConnectionSpec("t", "pg", "postgresql",
            _container.GetConnectionString(), false, null, null, ConnectionSource.Stored), Ct);

    private async Task<DataSearchResult> SearchAsync(string value, bool exact = false)
    {
        await using var session = await OpenAsync();
        return await DataSearch.RunAsync(_driver, session, value, "public", exact, 200, 30, Ct);
    }

    [Fact]
    public async Task A_number_is_found_wherever_it_is_a_number_and_wherever_it_is_text()
    {
        var result = await SearchAsync("4711");

        var places = result.Hits.Select(hit => $"{hit.Table}.{hit.Column}").ToList();

        Assert.Contains("people.id", places);          // an integer column, compared as a number
        Assert.Contains("orders.reference", places);   // inside text
        Assert.Contains("orders.total", places);       // a numeric column
    }

    [Fact]
    public async Task A_column_that_cannot_hold_the_value_is_never_scanned()
    {
        var result = await SearchAsync("4711");

        // A date cannot be 4711, and bytea is not text: neither is cast, so neither can match.
        Assert.DoesNotContain("people.born", result.Hits.Select(hit => $"{hit.Table}.{hit.Column}"));
        Assert.DoesNotContain("pictures.body", result.Hits.Select(hit => $"{hit.Table}.{hit.Column}"));
    }

    [Fact]
    public async Task Text_is_found_without_case_and_counted()
    {
        var result = await SearchAsync("LOVELACE");

        var hit = Assert.Single(result.Hits, entry => entry.Column == "name");
        Assert.Equal(1, hit.Matches);
        Assert.Equal("people", hit.Table);
    }

    [Fact]
    public async Task An_exact_search_is_the_whole_value_rather_than_a_part_of_it()
    {
        Assert.Empty((await SearchAsync("ORD", exact: true)).Hits);
        Assert.Contains("orders.reference",
            (await SearchAsync("ORD-4711", exact: true)).Hits.Select(hit => $"{hit.Table}.{hit.Column}"));
    }

    [Fact]
    public async Task A_date_is_searched_as_a_date_where_one_was_given()
    {
        var result = await SearchAsync("1815-12-10");

        Assert.Contains("people.born", result.Hits.Select(hit => $"{hit.Table}.{hit.Column}"));
    }

    [Fact]
    public async Task The_most_matches_come_first_and_the_count_is_reported()
    {
        var result = await SearchAsync("o");

        Assert.True(result.TablesSearched >= 2);
        Assert.Equal(result.Hits.OrderByDescending(hit => hit.Matches).Select(hit => hit.Matches),
            result.Hits.Select(hit => hit.Matches));
    }

    [Fact]
    public async Task A_search_for_nothing_searches_nothing()
    {
        await using var session = await OpenAsync();

        var result = await DataSearch.RunAsync(_driver, session, "   ", "public", false, 200, 30, Ct);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TablesSearched);
    }

    [Fact]
    public async Task A_cap_on_the_tables_is_reported_rather_than_hidden()
    {
        await using var session = await OpenAsync();

        var result = await DataSearch.RunAsync(_driver, session, "4711", "public", false, 1, 30, Ct);

        Assert.True(result.Truncated);
        Assert.Equal(1, result.TablesSearched + result.TablesSkipped);
    }

    [Fact]
    public async Task A_quote_in_the_search_value_is_a_value_and_not_syntax()
    {
        // The value travels as a parameter; only identifiers are ever interpolated.
        var result = await SearchAsync("'; DROP TABLE people; --");

        Assert.Empty(result.Hits);
        Assert.True(result.TablesSearched > 0);
    }
}

/// SQLite has no information_schema, and its own catalogue answers the same question.
public class SqliteDataSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_file_database_is_searched_through_its_own_catalogue()
    {
        var directory = Directory.CreateTempSubdirectory("wds-search").FullName;
        var path = Path.Combine(directory, "demo.db");

        try
        {
            await using (var db = new SqliteConnection($"Data Source={path}"))
            {
                await db.OpenAsync(Ct);
                await using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE people (id INTEGER, name TEXT);
                    INSERT INTO people VALUES (4711, 'ada'), (2, 'grace');
                    """;
                await command.ExecuteNonQueryAsync(Ct);
            }

            var driver = new SqliteDriver();
            await using var session = await driver.OpenAsync(new ConnectionSpec("t", "s", "sqlite",
                $"Data Source={path}", false, null, null, ConnectionSource.Stored), Ct);

            var result = await DataSearch.RunAsync(driver, session, "4711", null, false, 200, 30, Ct);

            Assert.Contains("people.id", result.Hits.Select(hit => $"{hit.Table}.{hit.Column}"));
        }
        finally
        {
            TestDirectory.Remove(directory);
        }
    }
}
