using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

public class QueryEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-query").FullName;
    private string _dataDb = "";

    public async ValueTask InitializeAsync()
    {
        _dataDb = Path.Combine(_dir, "demo.db");
        await using var db = new SqliteConnection($"Data Source={_dataDb}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people VALUES (1,'ada'),(2,'linus');
            """;
        await cmd.ExecuteNonQueryAsync();
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
                ["WDS_CONN_DEMO"] = $"sqlite:///{_dataDb.Replace('\\', '/')}",
            })));

    private static async Task<string> ConnectionIdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Lists_drivers_with_capabilities()
    {
        using var factory = Factory();
        var raw = await factory.CreateClient().GetStringAsync("/api/drivers", TestContext.Current.CancellationToken);
        Assert.Contains("sqlite", raw);
        Assert.Contains("estimatedPlan", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Returns_the_schema_root()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var raw = await client.GetStringAsync($"/api/schema/{await ConnectionIdAsync(client)}", ct);
        Assert.Contains("Tables", raw);
    }

    [Fact]
    public async Task Returns_a_lazy_child_level()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var raw = await client.GetStringAsync(
            $"/api/schema/{conn}?parent={Uri.EscapeDataString("TableFolder:main/tables")}", ct);
        Assert.Contains("people", raw);
    }

    [Fact]
    public async Task Describes_an_object()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var raw = await client.GetStringAsync(
            $"/api/schema/{conn}/object?ref={Uri.EscapeDataString("Table:main/people")}", ct);
        Assert.Contains("name", raw);
    }

    [Fact]
    public async Task Executes_a_query_and_streams_ndjson()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = await ConnectionIdAsync(client),
            sql = "SELECT id, name FROM people ORDER BY id",
            maxRows = 100,
        }, ct);
        response.EnsureSuccessStatusCode();

        var lines = (await response.Content.ReadAsStringAsync(ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains(lines, l => l.Contains("\"type\":\"columns\""));
        Assert.Contains(lines, l => l.Contains("ada"));
        Assert.Contains(lines, l => l.Contains("\"type\":\"end\""));
    }

    [Fact]
    public async Task A_syntax_error_arrives_as_an_error_line_not_a_500()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = await ConnectionIdAsync(client),
            sql = "SELECT FROM WHERE",
            maxRows = 100,
        }, ct);

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"type\":\"error\"", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Rejects_an_unknown_connection()
    {
        using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = "nope", sql = "SELECT 1", maxRows = 10,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Returns_an_execution_plan()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query/plan", new
        {
            connectionId = await ConnectionIdAsync(client),
            sql = "SELECT * FROM people",
            mode = "estimated",
        }, ct);

        response.EnsureSuccessStatusCode();
        Assert.Contains("QUERY PLAN", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task An_actual_plan_is_refused_where_the_engine_cannot_produce_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query/plan", new
        {
            connectionId = await ConnectionIdAsync(client),
            sql = "SELECT * FROM people",
            mode = "actual",
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Test_connection_probes_the_database_for_real()
    {
        using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/connections/test", new
        {
            name = "probe", engine = "sqlite",
            connectionString = $"Data Source={Path.Combine(_dir, "missing-dir", "x.db")}",
            readOnly = false,
        }, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.GetProperty("ok").GetBoolean());
    }
}
