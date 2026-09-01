using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A pager that says "1-200 of 12,345" has to be right about the 12,345. The number the browse
/// endpoint carries is what the catalogue holds — cheap, an estimate on some engines, and blind to
/// any filter. /count is the one that answers for the rows actually being paged through.
public class RowCountTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-count").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT);
            INSERT INTO customers (name, city) VALUES
                ('Ada', 'Berlin'), ('Bo', 'Porto'), ('Cai', 'Berlin'),
                ('Dee', 'Lisbon'), ('Eve', 'Berlin');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private const string Ref = "Table:main/customers";

    [Fact]
    public async Task Counts_every_row_of_the_table()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        using var answer = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/data/{id}/count?ref={Ref}", TestContext.Current.CancellationToken));

        Assert.Equal(5, answer.RootElement.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Counts_what_the_filter_leaves_rather_than_the_table()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        using var answer = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/data/{id}/count?ref={Ref}&filterColumn=city&filter=Berlin",
            TestContext.Current.CancellationToken));

        Assert.Equal(3, answer.RootElement.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task A_page_says_where_its_total_came_from()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        using var page = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/data/{id}?ref={Ref}&limit=2&filterColumn=city&filter=Berlin",
            TestContext.Current.CancellationToken));

        // The pager needs both: that the number is the catalogue's, and that a filter is in force —
        // together they say "this total is not the size of what you are looking at".
        Assert.True(page.RootElement.GetProperty("totalIsEstimate").GetBoolean());
        Assert.True(page.RootElement.GetProperty("filtered").GetBoolean());
        Assert.Equal(2, page.RootElement.GetProperty("rows").GetArrayLength());
    }
}
