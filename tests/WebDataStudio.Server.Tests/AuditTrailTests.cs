using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// What counts as worth writing down, and what the line is called. Pure: no server needed.
public class AuditShapeTests
{
    private static HttpContext Request(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    [Theory]
    [InlineData("POST", "/api/query/execute")]
    [InlineData("DELETE", "/api/storage/s3")]
    [InlineData("PUT", "/api/quality/pg")]
    [InlineData("PATCH", "/api/data/pg")]
    public void Anything_that_changes_something_is_written_down(string method, string path) =>
        Assert.True(Audit.Interesting(Request(method, path)));

    [Theory]
    // A file leaving the building is the question a trail is usually opened to answer.
    [InlineData("GET", "/api/export/csv")]
    [InlineData("GET", "/api/admin/backup/pg")]
    [InlineData("GET", "/api/archive/pg")]
    public void And_so_are_the_reads_that_take_data_out(string method, string path) =>
        Assert.True(Audit.Interesting(Request(method, path)));

    [Theory]
    [InlineData("GET", "/api/connections")]
    [InlineData("GET", "/api/schema/pg")]
    [InlineData("HEAD", "/api/health")]
    // Not the API at all: the SPA's own files.
    [InlineData("GET", "/index.html")]
    public void Looking_at_something_is_not(string method, string path) =>
        Assert.False(Audit.Interesting(Request(method, path)));

    [Fact]
    public void A_line_is_named_after_the_route_rather_than_the_url()
    {
        // No endpoint matched: the path is the honest fallback.
        Assert.Equal("POST query/execute", Audit.Action(Request("POST", "/api/query/execute")));
    }

    [Fact]
    public void A_detail_is_kept_but_not_a_novel()
    {
        var context = Request("POST", "/api/query/execute");
        Audit.Detail(context, new string('x', Audit.MaxDetail + 500), "pg");

        Assert.Equal(Audit.MaxDetail, ((string)context.Items[Audit.DetailKey]!).Length);
        Assert.Equal("pg", context.Items[Audit.ConnectionKey]);
    }

    [Fact]
    public void Nothing_said_is_nothing_written()
    {
        var context = Request("POST", "/api/query/execute");
        Audit.Detail(context, "", "");

        Assert.False(context.Items.ContainsKey(Audit.DetailKey));
        Assert.False(context.Items.ContainsKey(Audit.ConnectionKey));
    }

    [Fact]
    public void Off_is_off()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WDS_AUDIT"] = "false" }).Build();

        Assert.False(AuditOptions.FromConfiguration(config).Enabled);
        // And a retention nobody named is the default rather than nothing.
        Assert.Equal(90, AuditOptions.FromConfiguration(
            new ConfigurationBuilder().Build()).Days);
    }
}

/// The store: filtering and retention, without a server in the way.
public class AuditStoreTests
{
    private static WorkspaceStore Store(string dir) =>
        new(Path.Combine(dir, "wds.db"));

    private static AuditEntry Entry(string user, string conn, string action, string detail,
        DateTimeOffset? at = null) =>
        new(0, at ?? DateTimeOffset.UtcNow, user, "admin", conn, action, detail, 200, 5, "::1");

    [Fact]
    public void The_trail_is_newest_first_and_filtered_by_who_where_and_what()
    {
        var dir = Directory.CreateTempSubdirectory("wds-audit").FullName;

        try
        {
            var store = Store(dir);

            store.AddAudit(Entry("ada", "pg", "POST query/execute", "DELETE FROM orders"));
            store.AddAudit(Entry("grace", "pg", "POST export/{format}", "csv (result)"));
            store.AddAudit(Entry("ada", "mysql", "DELETE storage", "reports/2026.parquet"));

            Assert.Equal(3, store.ListAudit(null, null, null, 100).Count);
            Assert.Equal("DELETE storage", store.ListAudit(null, null, null, 100)[0].Action);
            Assert.Equal(2, store.ListAudit("ada", null, null, 100).Count);
            Assert.Equal(2, store.ListAudit(null, "pg", null, 100).Count);
            // Searching finds the statement, not only the route name.
            Assert.Single(store.ListAudit(null, null, "DELETE FROM", 100));
            Assert.Single(store.ListAudit(null, null, "export", 100));
            Assert.Single(store.ListAudit(null, null, null, 1));
        }
        finally { TestDirectory.Remove(dir); }
    }

    [Fact]
    public void Old_lines_are_dropped_and_recent_ones_are_not()
    {
        var dir = Directory.CreateTempSubdirectory("wds-audit").FullName;

        try
        {
            var store = Store(dir);

            store.AddAudit(Entry("ada", "pg", "POST query/execute", "old",
                DateTimeOffset.UtcNow.AddDays(-100)));
            store.AddAudit(Entry("ada", "pg", "POST query/execute", "new"));

            Assert.Equal(1, store.TrimAudit(90));

            var left = store.ListAudit(null, null, null, 100);
            Assert.Equal("new", Assert.Single(left).Detail);
        }
        finally { TestDirectory.Remove(dir); }
    }
}

/// End to end: a request, a line, and an admin reading it.
public class AuditEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-audit-api").FullName;
    private string _db = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE orders (id INTEGER PRIMARY KEY, total REAL)";
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(Dictionary<string, string?>? extra = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                    ["WDS_CONN_SHOP"] = $"sqlite:///{_db.Replace('\\', '/')}",
                }.Concat(extra ?? []).ToDictionary(pair => pair.Key, pair => pair.Value))));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private static async Task<List<JsonElement>> TrailAsync(HttpClient client, string query = "")
    {
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/admin/audit{query}", Ct);
        Assert.True(body.GetProperty("enabled").GetBoolean());
        return body.GetProperty("entries").EnumerateArray().ToList();
    }

    [Fact]
    public async Task A_statement_run_is_written_down_with_the_statement()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = id, sql = "SELECT 1", maxRows = 10,
        }, Ct);
        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync(Ct);

        var entries = await TrailAsync(client);
        var run = entries.First(e => e.GetProperty("action").GetString() == "POST query/execute");

        Assert.Equal("SELECT 1", run.GetProperty("detail").GetString());
        Assert.Equal(id, run.GetProperty("connectionId").GetString());
        // No accounts configured is one person at a machine, and saying so beats an empty column.
        Assert.Equal("anonymous", run.GetProperty("user").GetString());
        Assert.Equal(200, run.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Looking_at_the_schema_is_not_written_down()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        await client.GetStringAsync($"/api/schema/{id}", Ct);

        // The only lines are the ones this test's own reading of the trail cannot create.
        Assert.DoesNotContain(await TrailAsync(client),
            entry => entry.GetProperty("action").GetString()!.StartsWith("GET schema"));
    }

    [Fact]
    public async Task An_export_says_what_left_and_whether_it_was_masked()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/export/csv", new
        {
            connectionId = id, sql = "SELECT 1 AS n", scope = "result",
        }, Ct);
        response.EnsureSuccessStatusCode();

        var entry = (await TrailAsync(client))
            .First(e => e.GetProperty("action").GetString()!.StartsWith("POST export"));

        Assert.Equal("csv (result)", entry.GetProperty("detail").GetString());
        Assert.Equal(id, entry.GetProperty("connectionId").GetString());
    }

    [Fact]
    public async Task A_request_that_was_refused_is_written_down_too()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["WDS_USER"] = "ada", ["WDS_PASSWORD"] = "secret-secret",
        });

        using var stranger = factory.CreateClient();
        var refused = await stranger.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = "shop", sql = "SELECT 1",
        }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);

        using var admin = factory.CreateClient();
        (await admin.PostAsJsonAsync("/api/auth/login", new
        {
            username = "ada", password = "secret-secret",
        }, Ct)).EnsureSuccessStatusCode();

        var entries = await TrailAsync(admin);
        Assert.Contains(entries, entry => entry.GetProperty("status").GetInt32() == 401);
    }

    [Fact]
    public async Task A_signed_in_person_is_named()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["WDS_USER"] = "ada", ["WDS_PASSWORD"] = "secret-secret",
        });

        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "ada", password = "secret-secret",
        }, Ct)).EnsureSuccessStatusCode();

        var id = await IdAsync(client);
        await (await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = id, sql = "SELECT 1",
        }, Ct)).Content.ReadAsStringAsync(Ct);

        var run = (await TrailAsync(client, "?user=ada"))
            .First(entry => entry.GetProperty("action").GetString() == "POST query/execute");

        Assert.Equal("ada", run.GetProperty("user").GetString());
        Assert.Equal("admin", run.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Turned_off_it_writes_nothing()
    {
        using var factory = Factory(new Dictionary<string, string?> { ["WDS_AUDIT"] = "false" });
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        await (await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = id, sql = "SELECT 1",
        }, Ct)).Content.ReadAsStringAsync(Ct);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/admin/audit", Ct);

        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Empty(body.GetProperty("entries").EnumerateArray());
    }
}
