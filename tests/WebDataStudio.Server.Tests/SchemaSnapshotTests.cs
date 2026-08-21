using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Drift nobody meant: a column added by hand, an index a migration dropped on the way past. The
/// studio knows the schema anyway; this writes it down and says what moved.
public class SchemaSnapshotTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-snapshot").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");
        await ExecuteAsync("""
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE INDEX ix_customers_name ON customers(name);
            """);
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private string Snapshots => Path.Combine(_dir, "snapshots");

    private WebApplicationFactory<Program> Factory(bool configured = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                    ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
                };

                if (configured) settings["WDS_SCHEMA_SNAPSHOT_DIR"] = Snapshots;
                c.AddInMemoryCollection(settings);
            }));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_first_snapshot_is_a_baseline_not_a_change()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var moved = await factory.Services.GetRequiredService<SchemaSnapshots>().SweepAsync(ct);

        Assert.Equal(0, moved);
        Assert.True(File.Exists(Path.Combine(Snapshots, $"schema-{id}.json")));

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/schema/{id}/drift", ct);
        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Equal("no change", body.GetProperty("drift").GetProperty("summary").GetString());
    }

    [Fact]
    public async Task A_new_table_a_dropped_index_and_a_new_column_are_all_named()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);
        var snapshots = factory.Services.GetRequiredService<SchemaSnapshots>();

        await snapshots.SweepAsync(ct);

        await ExecuteAsync("""
            CREATE TABLE orders (id INTEGER PRIMARY KEY, total REAL);
            DROP INDEX ix_customers_name;
            ALTER TABLE customers ADD COLUMN city TEXT;
            """);

        Assert.Equal(1, await snapshots.SweepAsync(ct));

        var drift = (await client.GetFromJsonAsync<JsonElement>($"/api/schema/{id}/drift", ct))
            .GetProperty("drift");

        Assert.Contains("Table:main/orders",
            drift.GetProperty("added").EnumerateArray().Select(v => v.GetString()));

        var changed = string.Join(" | ",
            drift.GetProperty("changed").EnumerateArray().Select(v => v.GetString()));

        Assert.Contains("index gone: ix_customers_name", changed);
        Assert.Contains("column now: city", changed);
    }

    [Fact]
    public async Task A_dropped_table_is_reported_as_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);
        var snapshots = factory.Services.GetRequiredService<SchemaSnapshots>();

        await ExecuteAsync("CREATE TABLE temporary_thing (id INTEGER PRIMARY KEY);");
        await snapshots.SweepAsync(ct);

        await ExecuteAsync("DROP TABLE temporary_thing;");
        await snapshots.SweepAsync(ct);

        var drift = (await client.GetFromJsonAsync<JsonElement>($"/api/schema/{id}/drift", ct))
            .GetProperty("drift");

        Assert.Contains("Table:main/temporary_thing",
            drift.GetProperty("removed").EnumerateArray().Select(v => v.GetString()));
    }

    /// The second sweep after a change finds nothing: the snapshot moved with it.
    [Fact]
    public async Task The_snapshot_becomes_the_new_baseline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var snapshots = factory.Services.GetRequiredService<SchemaSnapshots>();

        await snapshots.SweepAsync(ct);
        await ExecuteAsync("CREATE TABLE audit (id INTEGER PRIMARY KEY);");

        Assert.Equal(1, await snapshots.SweepAsync(ct));
        Assert.Equal(0, await snapshots.SweepAsync(ct));
    }

    [Fact]
    public async Task Without_a_directory_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(configured: false);
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        Assert.Equal(0, await factory.Services.GetRequiredService<SchemaSnapshots>().SweepAsync(ct));
        Assert.False(Directory.Exists(Snapshots));

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/schema/{id}/drift", ct);
        Assert.False(body.GetProperty("configured").GetBoolean());

        var taken = await client.PostAsync("/api/schema/snapshot", null, ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, taken.StatusCode);
    }

    [Fact]
    public async Task A_snapshot_can_be_taken_on_demand()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        await IdAsync(client);

        var first = await client.PostAsync("/api/schema/snapshot", null, ct);
        first.EnsureSuccessStatusCode();

        await ExecuteAsync("CREATE TABLE later (id INTEGER PRIMARY KEY);");

        var second = await client.PostAsync("/api/schema/snapshot", null, ct);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal(1, body.GetProperty("moved").GetInt32());
    }

    /// A half-written file must not become the baseline the next comparison trusts.
    [Fact]
    public async Task A_damaged_snapshot_is_treated_as_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);
        var snapshots = factory.Services.GetRequiredService<SchemaSnapshots>();

        await snapshots.SweepAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(Snapshots, $"schema-{id}.json"), "{ not json", ct);

        // No exception, and no drift invented out of a file it could not read.
        Assert.Equal(0, await snapshots.SweepAsync(ct));
    }
}
