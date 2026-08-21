using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// Generated rows have to be usable: keys that exist, values that fit, and the same seed twice
/// giving the same data — otherwise a generated dataset cannot be talked about.
public class DataGeneratorTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-generate").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE regions (code TEXT PRIMARY KEY, label TEXT NOT NULL);
            INSERT INTO regions VALUES ('eu', 'Europe'), ('us', 'North America');

            CREATE TABLE customers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                email TEXT NOT NULL UNIQUE,
                city VARCHAR(6),
                region_code TEXT NOT NULL REFERENCES regions(code),
                active INTEGER,
                joined TEXT);
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

    private static async Task<JsonElement> PreviewAsync(HttpClient client, string id, object body)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync($"/api/data/{id}/generate/preview?ref={Ref}", body, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task ApplyAsync(HttpClient client, string id, string hash)
    {
        var response = await client.PostAsJsonAsync($"/api/data/{id}/apply-changes?ref={Ref}",
            new { hash }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_preview_inserts_exactly_the_rows_that_were_asked_for()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await PreviewAsync(client, id, new { rows = 50 });

        Assert.Equal(50, body.GetProperty("statementCount").GetInt32());
        Assert.False(body.GetProperty("destructive").GetBoolean());
        // The identity column is the database's to fill in.
        Assert.DoesNotContain("\"id\"", body.GetProperty("script").GetString());
    }

    [Fact]
    public async Task A_foreign_key_only_points_at_rows_that_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await PreviewAsync(client, id, new { rows = 30 });
        await ApplyAsync(client, id, body.GetProperty("hash").GetString()!);

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM customers
             WHERE region_code NOT IN (SELECT code FROM regions)
            """;

        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
    }

    [Fact]
    public async Task A_unique_column_gets_distinct_values()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await PreviewAsync(client, id, new { rows = 100 });
        await ApplyAsync(client, id, body.GetProperty("hash").GetString()!);

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*), count(DISTINCT email) FROM customers";

        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(100L, reader.GetInt64(0));
        Assert.Equal(100L, reader.GetInt64(1));
    }

    [Fact]
    public async Task The_same_seed_produces_the_same_rows()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var first = await PreviewAsync(client, id, new { rows = 10, seed = 7 });
        var again = await PreviewAsync(client, id, new { rows = 10, seed = 7 });
        var other = await PreviewAsync(client, id, new { rows = 10, seed = 8 });

        Assert.Equal(first.GetProperty("hash").GetString(), again.GetProperty("hash").GetString());
        Assert.NotEqual(first.GetProperty("hash").GetString(), other.GetProperty("hash").GetString());
    }

    /// A value must fit what the column can hold, or the very first row fails.
    [Fact]
    public async Task A_value_respects_the_columns_length()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await PreviewAsync(client, id, new
        {
            rows = 20,
            strategies = new Dictionary<string, string> { ["city"] = "sentence" },
        });
        await ApplyAsync(client, id, body.GetProperty("hash").GetString()!);

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT max(length(city)) FROM customers";

        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync(ct)) <= 6);
    }

    [Fact]
    public async Task The_strategies_call_says_what_each_column_would_get()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{id}/generate/strategies?ref={Ref}", ct);

        var byName = body.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!,
                          c => c.GetProperty("strategy").GetString());

        Assert.Equal("skip", byName["id"]);
        Assert.Equal("name", byName["name"]);
        Assert.Equal("email", byName["email"]);
        Assert.Equal("city", byName["city"]);
        Assert.Equal("fk", byName["region_code"]);
        Assert.Contains("auto", body.GetProperty("available").EnumerateArray()
            .Select(v => v.GetString()));
    }

    [Fact]
    public async Task A_row_count_nobody_wants_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/data/{id}/generate/preview?ref={Ref}", new { rows = 0 }, ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
