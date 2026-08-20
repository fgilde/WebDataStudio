using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Masking happens on the server. A value that never leaves it cannot be read out of a network tab,
/// a proxy log or a screenshot of somebody's developer tools.
public class MaskingTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-mask").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE accounts (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                password_hash TEXT,
                password_changed_at TEXT,
                api_key TEXT,
                comment TEXT);
            INSERT INTO accounts VALUES
                (1, 'ada', 'hash-abc', '2026-01-01', 'key-123', NULL);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(string? colour = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
        };
        if (colour is not null) settings["WDS_CONN_SHOP_COLOR"] = colour;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings)));
    }

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_secret_column_arrives_masked_and_marked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{id}?ref=Table:main/accounts", ct);

        var columns = body.GetProperty("columns").EnumerateArray().ToList();
        var row = body.GetProperty("rows").EnumerateArray().First().EnumerateArray().ToList();

        var hashIndex = columns.FindIndex(c => c.GetProperty("name").GetString() == "password_hash");
        var keyIndex = columns.FindIndex(c => c.GetProperty("name").GetString() == "api_key");

        Assert.True(columns[hashIndex].GetProperty("masked").GetBoolean());
        Assert.True(columns[keyIndex].GetProperty("masked").GetBoolean());
        Assert.Equal(SensitiveColumns.Mask, row[hashIndex].GetString());
        Assert.Equal(SensitiveColumns.Mask, row[keyIndex].GetString());
    }

    [Fact]
    public async Task Everything_else_comes_through_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{id}?ref=Table:main/accounts", ct);

        var columns = body.GetProperty("columns").EnumerateArray().ToList();
        var row = body.GetProperty("rows").EnumerateArray().First().EnumerateArray().ToList();

        var name = columns.FindIndex(c => c.GetProperty("name").GetString() == "name");
        // A timestamp about a password is not a password.
        var changed = columns.FindIndex(c => c.GetProperty("name").GetString() == "password_changed_at");

        Assert.Equal("ada", row[name].GetString());
        Assert.False(columns[changed].GetProperty("masked").GetBoolean());
        Assert.Equal("2026-01-01", row[changed].GetString());
    }

    [Fact]
    public async Task Revealing_is_a_request_of_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{id}?ref=Table:main/accounts&reveal=true", ct);

        var columns = body.GetProperty("columns").EnumerateArray().ToList();
        var row = body.GetProperty("rows").EnumerateArray().First().EnumerateArray().ToList();
        var hashIndex = columns.FindIndex(c => c.GetProperty("name").GetString() == "password_hash");

        Assert.Equal("hash-abc", row[hashIndex].GetString());
        Assert.False(columns[hashIndex].GetProperty("masked").GetBoolean());
    }

    [Fact]
    public async Task The_policy_can_overrule_the_word_list_both_ways()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var saved = await client.PutAsJsonAsync($"/api/data/{id}/mask-policy",
            new { maskByDefault = true, extra = new[] { "comment" }, never = new[] { "api_key" } }, ct);
        saved.EnsureSuccessStatusCode();

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{id}?ref=Table:main/accounts", ct);
        var columns = body.GetProperty("columns").EnumerateArray().ToList();

        bool MaskedOf(string name) => columns
            .First(c => c.GetProperty("name").GetString() == name)
            .GetProperty("masked").GetBoolean();

        // Somebody who wrote the lists down knows their schema better than a word list does.
        Assert.True(MaskedOf("comment"));
        Assert.False(MaskedOf("api_key"));
        // Everything the lists say nothing about still follows the heuristic.
        Assert.True(MaskedOf("password_hash"));
    }

    [Fact]
    public async Task A_query_cannot_be_the_way_around_the_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/query/execute",
            new { connectionId = id, sql = "SELECT name, password_hash FROM accounts" }, ct);
        var ndjson = await response.Content.ReadAsStringAsync(ct);

        Assert.DoesNotContain("hash-abc", ndjson);
        Assert.Contains("\"masked\":true", ndjson);
        // The stream escapes non-ASCII, so the mask travels as • rather than as the bullet.
        Assert.Contains("u2022", ndjson);
    }

    [Fact]
    public async Task An_export_is_masked_unless_it_asks_otherwise()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var masked = await client.PostAsJsonAsync("/api/export/csv",
            new { connectionId = id, sql = "SELECT name, password_hash FROM accounts" }, ct);
        var maskedCsv = await masked.Content.ReadAsStringAsync(ct);

        Assert.DoesNotContain("hash-abc", maskedCsv);
        Assert.Contains(SensitiveColumns.Mask, maskedCsv);

        var revealed = await client.PostAsJsonAsync("/api/export/csv",
            new { connectionId = id, sql = "SELECT name, password_hash FROM accounts", includeSensitive = true }, ct);

        Assert.Contains("hash-abc", await revealed.Content.ReadAsStringAsync(ct));
    }

    /// A red connection is production by the studio's own convention. Exporting its secrets to a
    /// downloads folder should not be one click away, whatever the caller asks for.
    [Fact]
    public async Task A_production_connection_refuses_to_export_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory("red");
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/export/csv",
            new { connectionId = id, sql = "SELECT password_hash FROM accounts", includeSensitive = true }, ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("production", await response.Content.ReadAsStringAsync(ct));

        // Masked is still fine — the export itself is not the problem.
        var masked = await client.PostAsJsonAsync("/api/export/csv",
            new { connectionId = id, sql = "SELECT password_hash FROM accounts" }, ct);

        masked.EnsureSuccessStatusCode();
        Assert.DoesNotContain("hash-abc", await masked.Content.ReadAsStringAsync(ct));
    }

    // A null is not a secret, and masking it would make a masked column impossible to reason about.
    [Fact]
    public void A_null_stays_null()
    {
        var columns = new List<ColumnMeta> { new("password", "text", true) };
        var masked = Masking.IndexesOf(columns, MaskPolicy.Default);

        var rows = Masking.Apply([new object?[] { null }], masked);

        Assert.Null(rows[0][0]);
    }
}
