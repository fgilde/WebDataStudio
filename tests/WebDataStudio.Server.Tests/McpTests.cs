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
                id INTEGER PRIMARY KEY, name TEXT NOT NULL, api_key TEXT, city TEXT,
                profile TEXT);
            INSERT INTO customers VALUES
                (1, 'ada', 'tok-42', 'london', '{"plan":"pro","seats":4}'),
                (2, 'grace', 'tok-43', 'new york', '{"plan":"free"}');
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER, total REAL);
            INSERT INTO orders VALUES (1, 1, 10.0), (2, NULL, 20.0);
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

    // --- the newer capabilities, as tools ---------------------------------------------------

    [Fact]
    public async Task The_newer_capabilities_are_offered_too()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var tools = (await RpcAsync(client, "tools/list"))
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("find_data", tools);
        Assert.Contains("json_shape", tools);
        Assert.Contains("table_sizes", tools);
        Assert.Contains("query_stats", tools);
        Assert.Contains("inspect_sql", tools);
        Assert.Contains("quality_rules", tools);
        Assert.Contains("run_quality_rules", tools);
        // Writing a rule changes the studio's state, so it waits for WDS_MCP_ALLOW_WRITE.
        Assert.DoesNotContain("save_quality_rule", tools);
    }

    [Fact]
    public async Task A_value_can_be_found_without_knowing_the_schema()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var (text, failed) = await CallAsync(client, "find_data", new
        {
            connectionId = await IdAsync(client), value = "london",
        });

        Assert.False(failed);
        Assert.Contains("customers", text);
        Assert.Contains("city", text);
    }

    [Fact]
    public async Task The_shape_of_a_document_column_is_a_tool_call()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var (text, failed) = await CallAsync(client, "json_shape", new
        {
            connectionId = await IdAsync(client), @ref = "Table:customers", column = "profile",
        });

        Assert.False(failed);
        Assert.Contains("plan", text);
        Assert.Contains("seats", text);
        // And the SELECT that turns those paths into columns.
        Assert.Contains("flatten", text);
    }

    [Fact]
    public async Task A_column_that_is_not_there_says_so_rather_than_building_sql()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var (text, failed) = await CallAsync(client, "json_shape", new
        {
            connectionId = await IdAsync(client), @ref = "Table:customers", column = "nope",
        });

        Assert.True(failed);
        Assert.Contains("no column", text);
    }

    [Fact]
    public async Task A_statement_can_be_read_before_it_runs()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var (text, failed) = await CallAsync(client, "inspect_sql", new
        {
            sql = "DELETE FROM customers",
        });

        Assert.False(failed);
        Assert.Contains("WHERE", text);
    }

    [Fact]
    public async Task Rules_about_the_data_can_be_written_read_and_run()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"), ("WDS_MCP_ALLOW_WRITE", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var (saved, savedFailed) = await CallAsync(client, "save_quality_rule", new
        {
            connectionId = id, table = "orders", column = "customer_id", kind = "NotNull",
            message = "every order needs a customer",
        });

        Assert.False(savedFailed);
        Assert.Contains("NotNull", saved);

        var (listed, _) = await CallAsync(client, "quality_rules", new { connectionId = id });
        Assert.Contains("every order needs a customer", listed);

        var (ran, ranFailed) = await CallAsync(client, "run_quality_rules", new { connectionId = id });

        Assert.False(ranFailed);
        // One of the two orders has no customer.
        Assert.Contains("\"violations\": 1", ran);
    }

    [Fact]
    public async Task A_rule_nobody_has_heard_of_lists_the_ones_that_exist()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"), ("WDS_MCP_ALLOW_WRITE", "true"));
        var client = factory.CreateClient();

        var (text, failed) = await CallAsync(client, "save_quality_rule", new
        {
            connectionId = await IdAsync(client), table = "orders", kind = "PleaseInvent",
        });

        Assert.True(failed);
        Assert.Contains("Freshness", text);
    }

    [Fact]
    public async Task Sizes_are_answered_or_the_engine_says_it_cannot()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var (text, failed) = await CallAsync(client, "table_sizes", new
        {
            connectionId = await IdAsync(client),
        });

        // SQLite has no size per table, and that is an answer rather than an error.
        Assert.False(failed);
        Assert.Contains("does not report a size per table", text);
    }

    [Fact]
    public async Task A_table_can_be_profiled_and_the_values_give_the_columns_away()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var (text, failed) = await CallAsync(client, "profile_table", new
        {
            connectionId = await IdAsync(client), @ref = "Table:customers",
        });

        Assert.False(failed);
        // Counted: two rows, and a column where every value is different.
        Assert.Contains("\"rows\": 2", text);
        Assert.Contains("\"unique\": true", text);
        // And read from the values: the api_key column holds tokens, the city column does not.
        Assert.Contains("suggestions", text);
    }

    [Fact]
    public async Task Notes_can_be_read_and_left()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"), ("WDS_MCP_ALLOW_WRITE", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var (written, writeFailed) = await CallAsync(client, "add_note", new
        {
            connectionId = id, @ref = "Table:customers",
            body = "The api_key column is a token, not a password.",
        });

        Assert.False(writeFailed);
        // Named for what it is: a note from an agent should not read as though a person wrote it.
        Assert.Contains("\"author\": \"mcp\"", written);

        var (read, readFailed) = await CallAsync(client, "object_notes", new
        {
            connectionId = id, @ref = "Table:customers",
        });

        Assert.False(readFailed);
        Assert.Contains("not a password", read);
    }

    [Fact]
    public async Task Leaving_a_note_needs_the_write_flag()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();

        var tools = (await RpcAsync(client, "tools/list"))
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("profile_table", tools);
        Assert.Contains("object_notes", tools);
        // Writing changes the studio's own state, so it waits for WDS_MCP_ALLOW_WRITE.
        Assert.DoesNotContain("add_note", tools);
    }

    [Fact]
    public async Task What_this_studio_has_run_is_a_tool_call()
    {
        using var factory = Factory(("WDS_MCP_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        // One statement in the history, put there the way the browser does.
        await client.PostAsJsonAsync("/api/history", new
        {
            connectionId = id, sql = "SELECT * FROM customers WHERE id = 1", elapsedMs = 3,
            rowCount = 1,
        }, TestContext.Current.CancellationToken);

        var (text, failed) = await CallAsync(client, "query_stats", new { connectionId = id });

        Assert.False(failed);
        Assert.Contains("SELECT * FROM customers WHERE id = ?", text);
    }
}
