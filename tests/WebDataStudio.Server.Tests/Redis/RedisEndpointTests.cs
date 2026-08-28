using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace WebDataStudio.Server.Tests.Redis;

/// The HTTP surface the key browser talks to. Redis is not SQL, so it has endpoints of its own
/// rather than a pretend table.
public class RedisEndpointTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-redis-api").FullName;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        var db = multiplexer.GetDatabase();

        await db.StringSetAsync("user:1", "ada");
        await db.StringSetAsync("user:2", "linus");
        await db.HashSetAsync("profile:1", [new HashEntry("city", "london")]);
        await multiplexer.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONNECTIONS"] = JsonSerializer.Serialize(new[]
                {
                    new { name = "CACHE", engine = "redis", connectionString = _container.GetConnectionString() },
                    new { name = "FILE", engine = "sqlite", connectionString = "Data Source=:memory:" },
                }),
                // An agent reads the same key space the data tab does.
                ["WDS_MCP_ENABLED"] = "true",
            })));

    private static async Task<string> IdAsync(HttpClient client, string name)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));

        return document.RootElement.EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == name)
            .GetProperty("id").GetString()!;
    }

    /// The text one MCP tool call produced.
    private static async Task<string> CallAsync(HttpClient client, string tool, object arguments)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments },
        }, ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        return body.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString() ?? "";
    }

    /// browse_rows used to refuse a Redis connection outright ("it is a key/value store"), so an
    /// agent had one fewer way to look at a cache than a person did.
    [Fact]
    public async Task An_agent_browses_the_key_space_too()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var text = await CallAsync(client, "browse_rows", new { connectionId = id, @ref = "Schema:0" });

        Assert.Contains("user:1", text);
        Assert.Contains("profile:1", text);
    }

    [Fact]
    public async Task An_agent_can_filter_the_keys_it_asks_for()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var text = await CallAsync(client, "browse_rows", new
        {
            connectionId = id, @ref = "Schema:0", filterColumn = "key", filter = "^profile",
        });

        Assert.Contains("profile:1", text);
        Assert.DoesNotContain("user:1", text);
    }

    /// The grid says it exports like any other, so it has to. An export used to be
    /// `SELECT * FROM key`, which Redis has no answer for.
    [Fact]
    public async Task The_key_space_exports_as_a_csv()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var response = await client.PostAsJsonAsync("/api/export/csv", new
        {
            connectionId = id, objectRef = "Schema:0", scope = "table",
        }, ct);

        response.EnsureSuccessStatusCode();
        var csv = await response.Content.ReadAsStringAsync(ct);

        Assert.Contains("key,type,ttl,length,memory", csv);
        Assert.Contains("user:1", csv);
        Assert.Contains("profile:1", csv);
    }

    [Fact]
    public async Task Keys_come_back_with_their_type_and_a_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/redis/{id}/keys?match=user:*&count=100", ct);

        var keys = body.GetProperty("keys").EnumerateArray().ToList();
        Assert.Equal(2, keys.Count);
        Assert.All(keys, key => Assert.Equal("string", key.GetProperty("type").GetString()));
        Assert.True(body.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task A_type_filter_reaches_the_server()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/redis/{id}/keys?type=hash&count=100", ct);

        var key = Assert.Single(body.GetProperty("keys").EnumerateArray().ToList());
        Assert.Equal("profile:1", key.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Databases_report_how_many_keys_they_hold()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/redis/{id}/databases", ct);

        var first = body.EnumerateArray().First();
        Assert.Equal(0, first.GetProperty("database").GetInt32());
        Assert.Equal(3, first.GetProperty("keys").GetInt64());
    }

    // Pointing a Redis endpoint at a SQL connection is a caller mistake, and it should read as one.
    [Fact]
    public async Task A_connection_that_is_not_redis_is_a_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "FILE");

        var response = await client.GetAsync($"/api/redis/{id}/keys", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// The data tab used to answer "ERR wrong number of arguments for 'select' command" here,
    /// because it built `SELECT * FROM key`. The driver builds the page itself now: a key is the
    /// table its type makes.
    [Fact]
    public async Task Browsing_rows_on_a_redis_key_reads_the_value()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/data/{id}?ref=Table:0/user/1", ct);

        Assert.Contains("value", body.GetProperty("columns").EnumerateArray()
            .Select(column => column.GetProperty("name").GetString()));

        var row = Assert.Single(body.GetProperty("rows").EnumerateArray().ToList());
        Assert.Equal("ada", row[0].GetString());

        // Not editable, and it says why rather than offering a save button that cannot work.
        Assert.False(body.GetProperty("editable").GetBoolean());
        Assert.Contains("SET", body.GetProperty("reason").GetString()!);
    }

    [Fact]
    public async Task Browsing_rows_on_a_redis_database_lists_the_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/data/{id}?ref=Schema:0", ct);

        var names = body.GetProperty("columns").EnumerateArray()
            .Select(column => column.GetProperty("name").GetString()).ToList();

        Assert.Equal(["key", "type", "ttl", "length", "memory"], names);
        Assert.Equal(3, body.GetProperty("totalEstimate").GetInt64());

        var keys = body.GetProperty("rows").EnumerateArray()
            .Select(row => row[0].GetString()).ToList();

        Assert.Equal(["profile:1", "user:1", "user:2"], keys);
    }

    [Fact]
    public async Task A_prefix_folder_lists_only_the_keys_under_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{id}?ref=TableFolder:0/user&filterColumn=key&filter=$2", ct);

        var row = Assert.Single(body.GetProperty("rows").EnumerateArray().ToList());
        Assert.Equal("user:2", row[0].GetString());
        Assert.Equal("string", row[1].GetString());
    }

    /// Counting the values of a column is a GROUP BY. Redis browses now, so the refusal has to name
    /// the real reason rather than "has no columns to count".
    [Fact]
    public async Task Distinct_values_are_refused_with_a_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var response = await client.GetAsync($"/api/data/{id}/distinct?ref=Schema:0&column=key", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot count", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task An_unknown_connection_is_a_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/redis/nope/keys", ct)).StatusCode);
    }
}
