using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// The drift report says what moved since the last snapshot. This is the other half: the statements
/// that would carry another database from there to here.
public class DriftMigrationTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-drift").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");
        await RunAsync("""
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE leaving (id INTEGER PRIMARY KEY);
            """);
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
                ["WDS_SCHEMA_SNAPSHOT_DIR"] = Path.Combine(_dir, "snapshots"),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_script_carries_another_database_from_the_snapshot_to_here()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        // A snapshot of where things were.
        (await client.PostAsJsonAsync("/api/schema/snapshot", new { }, ct)).EnsureSuccessStatusCode();

        // Then somebody changes the schema, the way somebody does on a Tuesday.
        await RunAsync("""
            CREATE TABLE orders (id INTEGER PRIMARY KEY, total REAL);
            ALTER TABLE people ADD COLUMN city TEXT;
            CREATE INDEX ix_people_name ON people (name);
            DROP TABLE leaving;
            """);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/schema/{conn}/drift/script", ct);
        var script = body.GetProperty("script").GetString()!;

        // The new table is written as it is now, not as the snapshot's summary of it.
        Assert.Contains("CREATE TABLE", script);
        Assert.Contains("orders", script);

        // The new column and the new index. (The writer says `ALTER TABLE t ADD "city" TEXT`.)
        Assert.Contains("ALTER TABLE", script);
        Assert.Contains("city", script);
        Assert.Contains("CREATE INDEX", script);
        Assert.Contains("ix_people_name", script);

        // And what is gone.
        Assert.Contains("DROP TABLE", script);
        Assert.Contains("leaving", script);
        Assert.True(body.GetProperty("destructive").GetBoolean());
    }

    [Fact]
    public async Task A_schema_that_did_not_move_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        (await client.PostAsJsonAsync("/api/schema/snapshot", new { }, ct)).EnsureSuccessStatusCode();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/schema/{conn}/drift/script", ct);

        Assert.Equal(0, body.GetProperty("statements").GetInt32());
        Assert.Equal("", body.GetProperty("script").GetString());
    }

    /// Before the first snapshot there is nothing to compare against, and saying so beats a script
    /// that would recreate the whole database.
    [Fact]
    public async Task Without_an_earlier_snapshot_it_says_so_rather_than_writing_everything()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/schema/{conn}/drift/script", ct);

        Assert.Empty(body.GetProperty("script").GetString()!);
        Assert.Contains("no earlier snapshot",
            string.Join(" ", body.GetProperty("needsAPerson").EnumerateArray().Select(x => x.GetString())));
    }

    [Fact]
    public async Task Without_a_snapshot_directory_the_endpoint_says_which_setting_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds2.db"),
                ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
            })));

        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var response = await client.GetAsync($"/api/schema/{conn}/drift/script", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WDS_SCHEMA_SNAPSHOT_DIR", await response.Content.ReadAsStringAsync(ct));
    }
}
