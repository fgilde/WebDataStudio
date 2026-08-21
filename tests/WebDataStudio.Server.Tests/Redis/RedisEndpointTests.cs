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
            })));

    private static async Task<string> IdAsync(HttpClient client, string name)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));

        return document.RootElement.EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == name)
            .GetProperty("id").GetString()!;
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

    /// A key is one value, not a page of rows. Asking the data tab for it used to reach the driver
    /// as `SELECT * FROM key` and come back as "ERR wrong number of arguments for 'select' command",
    /// which told the user nothing about what to do instead.
    [Fact]
    public async Task Browsing_rows_on_redis_says_where_to_look_instead()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client, "CACHE");

        var response = await client.GetAsync($"/api/data/{id}?ref=Table:0/user/1", ct);
        var message = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("key browser", message);
        Assert.DoesNotContain("select", message, StringComparison.OrdinalIgnoreCase);
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
