using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests.Admin;

public class DiagramEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-diagram").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                customer_id INTEGER NOT NULL REFERENCES customers(id),
                total REAL);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        // No ClearAllPools here: other suites hold pooled SQLite connections in parallel.
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
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
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Every_table_becomes_a_node_and_every_foreign_key_an_edge()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/diagram/{await IdAsync(client)}", ct);

        var nodes = body.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(2, nodes.Count);

        var orders = nodes.Single(n => n.GetProperty("name").GetString() == "orders");
        var columns = orders.GetProperty("columns").EnumerateArray().ToList();
        Assert.True(columns.Single(c => c.GetProperty("name").GetString() == "id")
            .GetProperty("primaryKey").GetBoolean());
        Assert.True(columns.Single(c => c.GetProperty("name").GetString() == "customer_id")
            .GetProperty("foreignKey").GetBoolean());

        var edge = Assert.Single(body.GetProperty("edges").EnumerateArray().ToList());
        Assert.Equal("main.orders", edge.GetProperty("source").GetString());
        Assert.Equal("main.customers", edge.GetProperty("target").GetString());
        Assert.True(edge.GetProperty("resolved").GetBoolean());
        Assert.Equal("customer_id", edge.GetProperty("sourceColumns")[0].GetString());
    }

    [Fact]
    public async Task An_unknown_connection_is_a_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/diagram/nope", ct)).StatusCode);
    }
}
