using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// The document somebody asks for when they join the team, and what has to be in it.
public class DataDictionaryTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-dict").FullName;
    private string _db = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var db = new SqliteConnection($"Data Source={_db}");
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                email TEXT);
            INSERT INTO customers VALUES (1,'ada','ada@example.com');

            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                customer_id INTEGER NOT NULL REFERENCES customers(id),
                total NUMERIC);
            CREATE INDEX ix_orders_customer ON orders(customer_id);
            """;
        await command.ExecuteNonQueryAsync(Ct);
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
                ["WDS_CONN_SHOP"] = $"sqlite:///{_db.Replace('\\', '/')}",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Describes_every_table_its_columns_and_what_points_where()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var markdown = await client.GetStringAsync($"/api/dictionary/{await IdAsync(client)}", Ct);

        // The overview first: the half most people actually read.
        Assert.Contains("## Tables", markdown);
        Assert.Contains("[customers](#customers)", markdown);

        // Then each table in full.
        Assert.Contains("## orders", markdown);
        Assert.Contains("| customer_id | `INTEGER` | no |", markdown);
        Assert.Contains("**Points at**", markdown);
        Assert.Contains("`customers(id)`", markdown);
        Assert.Contains("`ix_orders_customer` on `customer_id`", markdown);
    }

    [Fact]
    public async Task Carries_the_notes_people_left_in_the_studio()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var posted = await client.PostAsJsonAsync($"/api/notes/{id}",
            new { @ref = "Table:main/customers", body = "one row per person, not per account" }, Ct);
        posted.EnsureSuccessStatusCode();

        var markdown = await client.GetStringAsync($"/api/dictionary/{id}", Ct);

        // The part that was never derivable from the schema is exactly the part worth keeping.
        Assert.Contains("**Notes**", markdown);
        Assert.Contains("one row per person, not per account", markdown);
    }

    [Fact]
    public async Task Says_when_it_stopped_rather_than_pretending_that_was_all()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var markdown = await client.GetStringAsync(
            $"/api/dictionary/{await IdAsync(client)}?limit=1", Ct);

        Assert.Contains("1 more table(s) are not described here", markdown);
    }

    [Fact]
    public async Task Is_markdown_a_person_can_be_sent()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/dictionary/{await IdAsync(client)}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
    }
}
