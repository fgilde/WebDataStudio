using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WebDataStudio.Server.Tests;

/// "Here is what I am seeing", without a screenshot. A link is a snapshot: it cannot run anything,
/// it cannot show more than the person who made it could see, and it expires.
public class ShareTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-share").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, api_key TEXT);
            INSERT INTO customers VALUES (1, 'ada', 'tok-42'), (2, 'grace', 'tok-43');
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

    private static async Task<JsonElement> ShareAsync(HttpClient client, string id, string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync("/api/share",
            new { connectionId = id, sql }, ct);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    [Fact]
    public async Task Off_by_default()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/share",
            new { connectionId = id, sql = "SELECT 1" }, ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health", ct);
        Assert.Equal(JsonValueKind.Null, health.GetProperty("share").ValueKind);
    }

    [Fact]
    public async Task A_link_shows_the_rows_as_they_were()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_SHARE_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var created = await ShareAsync(client, id, "SELECT id, name FROM customers ORDER BY id");
        var shareId = created.GetProperty("id").GetString()!;

        Assert.Equal(2, created.GetProperty("rows").GetInt32());
        Assert.StartsWith("/share/", created.GetProperty("url").GetString());

        var shared = await client.GetFromJsonAsync<JsonElement>($"/api/share/{shareId}", ct);

        Assert.Equal("SHOP", shared.GetProperty("connectionName").GetString());
        Assert.Equal(["id", "name"],
            shared.GetProperty("columns").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("ada", shared.GetProperty("rows")[0][1].GetString());

        // The rows are kept, so changing the table afterwards does not change the link.
        await using (var connection = new SqliteConnection($"Data Source={_db}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE customers SET name = 'changed' WHERE id = 1";
            await command.ExecuteNonQueryAsync(ct);
        }

        var again = await client.GetFromJsonAsync<JsonElement>($"/api/share/{shareId}", ct);
        Assert.Equal("ada", again.GetProperty("rows")[0][1].GetString());
    }

    /// A masked column is masked in the snapshot for good: the rows are stored after masking.
    [Fact]
    public async Task A_masked_column_is_masked_in_the_link()
    {
        using var factory = Factory(("WDS_SHARE_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var created = await ShareAsync(client, id, "SELECT name, api_key FROM customers");
        var shared = await client.GetStringAsync($"/api/share/{created.GetProperty("id").GetString()}",
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("tok-42", shared);
    }

    /// A link is a record, not a button: a statement that writes cannot be shared.
    [Fact]
    public async Task Only_a_reading_statement_can_be_shared()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_SHARE_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync("/api/share",
            new { connectionId = id, sql = "DELETE FROM customers" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reading statement", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_link_that_never_existed_and_one_that_expired_answer_the_same()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(("WDS_SHARE_ENABLED", "true"));
        var client = factory.CreateClient();

        var missing = await client.GetAsync("/api/share/deadbeefdeadbeefdeadbeefdeadbeef", ct);
        // The id becomes part of a workspace key, so anything that is not hex is refused rather
        // than looked up.
        var nonsense = await client.GetAsync("/api/share/item%3Atabs", ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonsense.StatusCode);
    }

    [Fact]
    public async Task An_expired_link_stops_working()
    {
        var ct = TestContext.Current.CancellationToken;
        // The shortest life the options allow is an hour, so the expiry is checked by writing a
        // snapshot that is already old.
        using var factory = Factory(("WDS_SHARE_ENABLED", "true"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var created = await ShareAsync(client, id, "SELECT 1");
        var shareId = created.GetProperty("id").GetString()!;

        var store = factory.Services.GetRequiredService<Server.Services.WorkspaceStore>();
        var json = store.LoadItem($"share:{shareId}")!;
        store.SaveItem($"share:{shareId}",
            json.Replace(created.GetProperty("expiresAt").GetString()!, "2020-01-01T00:00:00+00:00"));

        var response = await client.GetAsync($"/api/share/{shareId}", ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// The point of a public link: somebody without an account can open it. And the point of the
    /// flag: that only happens when the deployment said so.
    [Fact]
    public async Task A_public_link_opens_without_a_login()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(
            ("WDS_SHARE_ENABLED", "true"), ("WDS_SHARE_PUBLIC", "true"),
            ("WDS_USERS", $"ada:admin:{Server.Services.UserStore.Hash("one")}"));

        var signedIn = factory.CreateClient();
        var login = await signedIn.PostAsJsonAsync("/api/auth/login",
            new { username = "ada", password = "one" }, ct);
        login.EnsureSuccessStatusCode();

        var id = await IdAsync(signedIn);
        var created = await ShareAsync(signedIn, id, "SELECT name FROM customers");
        var shareId = created.GetProperty("id").GetString()!;

        // A fresh client has no cookie at all.
        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/share/{shareId}", ct);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("ada", await response.Content.ReadAsStringAsync(ct));
        Assert.True(created.GetProperty("isPublic").GetBoolean());
    }

    [Fact]
    public async Task A_private_link_still_needs_an_account()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(
            ("WDS_SHARE_ENABLED", "true"),
            ("WDS_USERS", $"ada:admin:{Server.Services.UserStore.Hash("one")}"));

        var signedIn = factory.CreateClient();
        await signedIn.PostAsJsonAsync("/api/auth/login", new { username = "ada", password = "one" }, ct);

        var id = await IdAsync(signedIn);
        var created = await ShareAsync(signedIn, id, "SELECT name FROM customers");

        var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync(
            $"/api/share/{created.GetProperty("id").GetString()}", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_row_cap_is_reported_as_truncated()
    {
        using var factory = Factory(("WDS_SHARE_ENABLED", "true"), ("WDS_SHARE_MAX_ROWS", "1"));
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var created = await ShareAsync(client, id, "SELECT name FROM customers");

        Assert.Equal(1, created.GetProperty("rows").GetInt32());
        Assert.True(created.GetProperty("truncated").GetBoolean());
    }
}
