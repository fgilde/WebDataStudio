using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebDataStudio.Server.Tests;

/// A join across two connections. The point of the feature is that the two databases never learn
/// about each other: each query runs where it lives, and the rows meet in DuckDB.
public class FederationTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-federate").FullName;
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private string _sqlite = "";

    public async ValueTask InitializeAsync()
    {
        _sqlite = Path.Combine(_dir, "local.db");

        await using (var connection = new SqliteConnection($"Data Source={_sqlite}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE regions (code TEXT PRIMARY KEY, label TEXT NOT NULL);
                INSERT INTO regions VALUES ('eu', 'Europe'), ('us', 'North America');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await _postgres.StartAsync();

        await using var pg = new NpgsqlConnection(_postgres.GetConnectionString());
        await pg.OpenAsync();
        await using var seed = pg.CreateCommand();
        seed.CommandText = """
            CREATE TABLE customers (id serial PRIMARY KEY, name text NOT NULL, region text NOT NULL);
            INSERT INTO customers (name, region) VALUES
                ('ada', 'eu'), ('grace', 'us'), ('linus', 'eu');
            """;
        await seed.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_LOCAL"] = "sqlite:///" + _sqlite.Replace(Path.DirectorySeparatorChar, '/'),
                ["WDS_CONN_SHOP"] = _postgres.GetConnectionString(),
                ["WDS_CONN_SHOP_ENGINE"] = "postgresql",
            })));

    private static async Task<Dictionary<string, string>> IdsAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));

        return document.RootElement.EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetString()!);
    }

    /// The NDJSON stream, parsed into the chunks the grid would see.
    private static async Task<List<JsonElement>> RunAsync(HttpClient client, object request)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync("/api/federate/run", request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())];
    }

    [Fact]
    public async Task Two_connections_are_joined_by_one_query()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var chunks = await RunAsync(client, new
        {
            sources = new[]
            {
                new { connectionId = ids["SHOP"], sql = "SELECT id, name, region FROM customers", alias = "c" },
                new { connectionId = ids["LOCAL"], sql = "SELECT code, label FROM regions", alias = "r" },
            },
            sql = """
                SELECT r.label, count(*) AS people
                  FROM c JOIN r ON r.code = c.region
                 GROUP BY r.label
                 ORDER BY r.label
                """,
        });

        Assert.DoesNotContain(chunks, c => c.GetProperty("type").GetString() == "error");

        var rows = chunks.Where(c => c.GetProperty("type").GetString() == "rows")
            .SelectMany(c => c.GetProperty("rows").EnumerateArray())
            .Select(row => (row[0].GetString(), row[1].GetInt64()))
            .ToList();

        Assert.Equal([("Europe", 2L), ("North America", 1L)], rows);
    }

    [Fact]
    public async Task An_unknown_alias_says_which_ones_exist()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var chunks = await RunAsync(client, new
        {
            sources = new[]
            {
                new { connectionId = ids["LOCAL"], sql = "SELECT code FROM regions", alias = "r" },
            },
            sql = "SELECT * FROM nope",
        });

        var error = Assert.Single(chunks, c => c.GetProperty("type").GetString() == "error");
        Assert.Contains("staged sources: r", error.GetProperty("text").GetString());
    }

    /// Staging is copying. A source that would copy too much is refused by name, rather than
    /// quietly filling the server's memory.
    [Fact]
    public async Task A_source_that_is_too_large_is_refused_by_name()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var chunks = await RunAsync(client, new
        {
            sources = new[]
            {
                new { connectionId = ids["SHOP"], sql = "SELECT * FROM customers", alias = "c" },
            },
            sql = "SELECT count(*) FROM c",
            maxRowsPerSource = 2,
        });

        var error = Assert.Single(chunks, c => c.GetProperty("type").GetString() == "error");
        Assert.Contains("'c' returned more than 2 rows", error.GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_preview_says_what_it_would_stage_without_copying_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var response = await client.PostAsJsonAsync("/api/federate/preview", new
        {
            sources = new[]
            {
                new { connectionId = ids["SHOP"], sql = "SELECT id, name FROM customers", alias = "c" },
            },
            sql = "SELECT * FROM c",
        }, ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var ddl = body.GetProperty("sources").EnumerateArray().First().GetProperty("ddl").GetString();

        Assert.Contains("CREATE OR REPLACE TABLE \"c\"", ddl);
        Assert.Contains("\"id\" BIGINT", ddl);
        Assert.Contains("\"name\" VARCHAR", ddl);
    }

    [Fact]
    public async Task An_alias_that_cannot_be_a_table_name_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var response = await client.PostAsJsonAsync("/api/federate/preview", new
        {
            sources = new[]
            {
                new { connectionId = ids["LOCAL"], sql = "SELECT 1", alias = "drop table x;--" },
            },
            sql = "SELECT 1",
        }, ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot be a table name", await response.Content.ReadAsStringAsync(ct));
    }

    /// The mask policy holds here too: a federated query is another way into the same data.
    [Fact]
    public async Task A_secret_column_is_still_masked_after_staging()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var chunks = await RunAsync(client, new
        {
            sources = new[]
            {
                new
                {
                    connectionId = ids["SHOP"],
                    sql = "SELECT name, 'hunter2' AS password FROM customers WHERE name = 'ada'",
                    alias = "c",
                },
            },
            sql = "SELECT password FROM c",
        });

        var text = string.Join("", chunks.Select(c => c.ToString()));
        Assert.DoesNotContain("hunter2", text);
    }
}
