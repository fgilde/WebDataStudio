using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// The studio as an MCP server. What matters most: it does not exist unless it was asked for, an
/// agent gets the same rules a person gets, and a write is previewed before it runs.
public class McpTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-mcp").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL, api_key TEXT, city TEXT);
            INSERT INTO customers VALUES
                (1, 'ada', 'tok-42', 'london'),
                (2, 'grace', 'tok-43', 'new york');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                    ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
                };

                foreach (var (key, value) in extra) settings[key] = value;
                c.AddInMemoryCollection(settings);
            }));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    /// One JSON-RPC call, returning the whole envelope.
    private static async Task<JsonElement> RpcAsync(HttpClient client, string method,
        object? parameters = null, string path = "/mcp", int id = 1)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync(path, new
        {
            jsonrpc = "2.0", id, method, @params = parameters,
        }, ct);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// The text a tool call produced.
    private static async Task<(string Text, bool IsError)> CallAsync(
        HttpClient client, string tool, object arguments)
    {
        var body = await RpcAsync(client, "tools/call", new { name = tool, arguments });
        var result = body.GetProperty("result");

        return (result.GetProperty("content")[0].GetProperty("text").GetString() ?? "",
            result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Without_configuration_there_is_no_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" }, ct);

        // The SPA fallback claims the path for a GET, so a POST is 405 rather than 404 — either
        // way there is nothing here to talk to.
        Assert.False(post.IsSuccessStatusCode);
        Assert.Contains(post.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health", ct);
        Assert.Equal(JsonValueKind.Null, health.GetProperty("mcp").ValueKind);
    }

    [Fact]
    public async Task Health_says_where_the_endpoint_is()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));

        var health = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/health", ct);
        var mcp = health.GetProperty("mcp");

        Assert.Equal("/mcp", mcp.GetProperty("path").GetString());
        Assert.False(mcp.GetProperty("writes").GetBoolean());
        Assert.False(mcp.GetProperty("needsKey").GetBoolean());
    }

    [Fact]
    public async Task It_handshakes_and_lists_its_tools()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var initialize = await RpcAsync(client, "initialize");
        Assert.Equal("webdatastudio",
            initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        var tools = (await RpcAsync(client, "tools/list"))
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("list_connections", tools);
        Assert.Contains("run_query", tools);
        // Read-only by default, so the two writing tools are not even offered.
        Assert.DoesNotContain("apply_script", tools);
    }

    [Fact]
    public async Task An_agent_can_find_its_way_around_and_read()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var connections = await CallAsync(client, "list_connections", new { });
        Assert.False(connections.IsError);
        Assert.Contains("SHOP", connections.Text);

        var objects = await CallAsync(client, "list_objects", new { connectionId = id });
        Assert.False(objects.IsError);

        var described = await CallAsync(client, "describe_object",
            new { connectionId = id, @ref = "Table:main/customers" });
        Assert.Contains("api_key", described.Text);
        // The agent is told which columns it will not see the values of.
        Assert.Contains("\"masked\": true", described.Text);

        var rows = await CallAsync(client, "run_query",
            new { connectionId = id, sql = "SELECT name, city FROM customers ORDER BY id" });
        Assert.Contains("london", rows.Text);
    }

    /// The mask policy is the studio's, not the caller's: an agent that asks for a secret column
    /// gets the same dots a person gets.
    [Fact]
    public async Task A_masked_column_stays_masked_for_an_agent()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var query = await CallAsync(client, "run_query",
            new { connectionId = id, sql = "SELECT api_key FROM customers" });
        var browse = await CallAsync(client, "browse_rows",
            new { connectionId = id, @ref = "Table:main/customers" });

        Assert.DoesNotContain("tok-42", query.Text);
        Assert.DoesNotContain("tok-42", browse.Text);
    }

    [Fact]
    public async Task Read_only_means_read_only()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var write = await CallAsync(client, "run_query",
            new { connectionId = id, sql = "DELETE FROM customers" });

        Assert.True(write.IsError);
        Assert.Contains("only runs statements that read", write.Text);

        // And the writing tools are refused by name, not silently missing.
        var apply = await CallAsync(client, "apply_script", new { connectionId = id, hash = "x" });
        Assert.True(apply.IsError);
        Assert.Contains("WDS_MCP_ALLOW_WRITE", apply.Text);
    }

    /// Two statements where the first one reads would otherwise sail past the guard.
    [Fact]
    public async Task A_read_followed_by_a_write_is_still_a_write()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var sneaky = await CallAsync(client, "run_query",
            new { connectionId = id, sql = "SELECT 1; DELETE FROM customers" });

        Assert.True(sneaky.IsError);
    }

    [Fact]
    public async Task Writing_goes_through_a_preview_and_its_hash()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"), ("WDS_MCP_ALLOW_WRITE", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var preview = await CallAsync(client, "preview_script",
            new { connectionId = id, sql = "DELETE FROM customers WHERE id = 2" });

        Assert.False(preview.IsError);
        using var document = JsonDocument.Parse(preview.Text);
        var hash = document.RootElement.GetProperty("hash").GetString();
        Assert.True(document.RootElement.GetProperty("statements")[0]
            .GetProperty("destructive").GetBoolean());

        // Nothing has run yet.
        var before = await CallAsync(client, "run_query",
            new { connectionId = id, sql = "SELECT count(*) FROM customers" });
        Assert.Contains("2", before.Text);

        var applied = await CallAsync(client, "apply_script", new { connectionId = id, hash });
        Assert.False(applied.IsError);

        var after = await CallAsync(client, "run_query",
            new { connectionId = id, sql = "SELECT count(*) FROM customers" });
        Assert.Contains("1", after.Text);

        // The hash is consumed, so the same call cannot run twice by accident.
        var again = await CallAsync(client, "apply_script", new { connectionId = id, hash });
        Assert.True(again.IsError);
    }

    [Fact]
    public async Task An_unknown_hash_is_refused()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"), ("WDS_MCP_ALLOW_WRITE", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var applied = await CallAsync(client, "apply_script",
            new { connectionId = id, hash = "deadbeef" });

        Assert.True(applied.IsError);
        Assert.Contains("preview_script", applied.Text);
    }

    [Fact]
    public async Task A_key_is_required_when_one_is_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_MCP_ENABLED", "true"), ("WDS_MCP_KEY", "s3cret"));
        var client = factory.CreateClient();

        var anonymous = await client.PostAsJsonAsync("/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Add("X-API-Key", "s3cret");
        var authorised = await client.PostAsJsonAsync("/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" }, ct);
        Assert.True(authorised.IsSuccessStatusCode);
    }

    /// An agent endpoint that skips the login screen would be a back door, so it does not open —
    /// and health says why, because otherwise the header dialog advertises a path that answers the
    /// SPA's HTML and the user gets "Unexpected token '<'".
    [Fact]
    public async Task A_studio_with_accounts_needs_an_mcp_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(
            ("WDS_MCP_ENABLED", "true"), ("WDS_USERS", "ada:admin:one"));
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp",
            new { jsonrpc = "2.0", id = 1, method = "ping" }, ct);

        Assert.False(response.IsSuccessStatusCode);

        var mcp = (await client.GetFromJsonAsync<JsonElement>("/api/health", ct))
            .GetProperty("mcp");

        Assert.False(mcp.GetProperty("enabled").GetBoolean());
        Assert.Contains("WDS_MCP_KEY", mcp.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_served_endpoint_reports_itself_as_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));

        var mcp = (await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/health", ct))
            .GetProperty("mcp");

        Assert.True(mcp.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mcp.GetProperty("reason").ValueKind);
    }

    /// With accounts and a key it works, and the key is what guards it.
    [Fact]
    public async Task Accounts_and_a_key_together_are_fine()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(
            ("WDS_MCP_ENABLED", "true"), ("WDS_USERS", "ada:admin:one"), ("WDS_MCP_KEY", "k"));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "k");

        var response = await client.PostAsJsonAsync("/mcp",
            new { jsonrpc = "2.0", id = 1, method = "ping" }, ct);

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task It_explains_a_plan_and_reports_health()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var plan = await CallAsync(client, "explain_plan",
            new { connectionId = id, sql = "SELECT * FROM customers WHERE city = 'london'" });

        Assert.False(plan.IsError);
        Assert.Contains("operation", plan.Text);

        var health = await CallAsync(client, "health_report", new { connectionId = id });

        Assert.False(health.IsError);
        // A report with no findings is still a report; the shape is what matters.
        Assert.Contains("findings", health.Text);
    }

    /// An actual plan runs the statement, so it obeys the rule run_query obeys.
    [Fact]
    public async Task An_actual_plan_of_a_write_is_refused()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var plan = await CallAsync(client, "explain_plan",
            new { connectionId = id, sql = "DELETE FROM customers", actual = "true" });

        Assert.True(plan.IsError);
    }

    [Fact]
    public async Task Activity_is_empty_rather_than_an_error_on_an_engine_that_cannot_answer()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var activity = await CallAsync(client, "server_activity", new { connectionId = id });

        Assert.False(activity.IsError);
        Assert.Contains("running", activity.Text);
    }

    [Fact]
    public async Task Redis_value_says_when_the_connection_is_not_redis()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var value = await CallAsync(client, "redis_value", new { connectionId = id, key = "x" });

        Assert.True(value.IsError);
        Assert.Contains("not Redis", value.Text);
    }

    /// A deployment can narrow the endpoint to the tools it wants an agent to have.
    [Fact]
    public async Task The_tools_can_be_named_by_the_deployment()
    {
        using var factory = Factory(
            ("WDS_MCP_ENABLED", "true"), ("WDS_MCP_TOOLS", "list_connections, list_tables"));
        var client = factory.CreateClient();

        var tools = (await RpcAsync(client, "tools/list"))
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(["list_connections", "list_tables"], tools.Order());

        // A tool left out is refused by name, not silently missing.
        var id = await IdAsync(client);
        var refused = await CallAsync(client, "run_query",
            new { connectionId = id, sql = "SELECT 1" });

        Assert.True(refused.IsError);
        Assert.Contains("WDS_MCP_TOOLS", refused.Text);
    }

    [Fact]
    public async Task The_path_can_be_moved()
    {
        using var factory = Factory(("WDS_MCP_PATH", "agents/db"));
        var client = factory.CreateClient();

        var body = await RpcAsync(client, "ping", path: "/agents/db");

        Assert.Equal("2.0", body.GetProperty("jsonrpc").GetString());
    }

    [Fact]
    public async Task An_unknown_method_is_a_protocol_error_not_a_crash()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var body = await RpcAsync(client, "sing/aSong");

        Assert.Equal(-32601, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_notification_is_answered_with_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp",
            new { jsonrpc = "2.0", method = "notifications/initialized" }, ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task The_get_says_what_this_is()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));

        var body = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/mcp", ct);

        Assert.Equal("webdatastudio", body.GetProperty("name").GetString());
        Assert.Equal("none", body.GetProperty("authentication").GetString());
        Assert.NotEmpty(body.GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public async Task The_assistant_reports_whether_it_has_tools()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));

        var without = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/api/assist/capabilities", ct);

        // No assistance endpoint: no tools either, whatever MCP says.
        Assert.False(without.GetProperty("configured").GetBoolean());
        Assert.False(without.GetProperty("tools").GetBoolean());
    }
}
