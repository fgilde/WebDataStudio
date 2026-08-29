using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// What a deployment brings with it, beyond the four folders the studio already read: connections,
/// the masking baseline, dashboards, snippets, and the preferences a studio starts with. Each one
/// belongs to the deployment — the studio shows it and cannot change it — and a broken file is a
/// line in the log rather than a studio that will not start.
public class ShippedFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-shipped").FullName;

    public void Dispose() => TestDirectory.Remove(_dir);

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string? Value)[] extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                };

                foreach (var (key, value) in extra) settings[key] = value;
                c.AddInMemoryCollection(settings);
            }));

    // --- connections ------------------------------------------------------------------------------

    [Fact]
    public async Task Connections_can_come_from_a_file_rather_than_a_wall_of_environment()
    {
        var ct = TestContext.Current.CancellationToken;

        var file = Write("connections.json", """
            [
              { "name": "LEGACY", "engine": "sqlite", "connectionString": "Data Source=:memory:",
                "group": "Old", "readOnly": true }
            ]
            """);

        using var factory = Factory(("WDS_CONNECTIONS_FILE", file));

        var connections = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/api/connections", ct);

        var one = Assert.Single(connections.EnumerateArray().ToList());

        Assert.Equal("LEGACY", one.GetProperty("name").GetString());
        Assert.True(one.GetProperty("readOnly").GetBoolean());
        Assert.Equal("Old", one.GetProperty("group").GetString());

        // Read-only in the UI, like every other connection the environment defines.
        Assert.Equal("Environment", one.GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_connection_file_and_the_variables_both_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Write("more.json",
            """[{ "name": "FROM_FILE", "engine": "sqlite", "connectionString": "Data Source=:memory:" }]""");

        using var factory = Factory(
            ("WDS_CONNECTIONS_FILE", file),
            ("WDS_CONN_FROM_ENV", "sqlite:///:memory:"));

        var names = (await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/connections", ct))
            .EnumerateArray().Select(one => one.GetProperty("name").GetString()).ToList();

        Assert.Contains("FROM_FILE", names);
        Assert.Contains("FROM_ENV", names);
    }

    /// A file nobody can parse must not cost the studio its start.
    [Fact]
    public async Task A_broken_file_is_skipped_rather_than_fatal()
    {
        var ct = TestContext.Current.CancellationToken;
        var broken = Write("broken.json", "{ this is not json");

        using var factory = Factory(
            ("WDS_CONNECTIONS_FILE", broken),
            ("WDS_CONN_STILL_HERE", "sqlite:///:memory:"));

        var names = (await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/connections", ct))
            .EnumerateArray().Select(one => one.GetProperty("name").GetString()).ToList();

        Assert.Equal(["STILL_HERE"], names);
    }

    // --- masking ----------------------------------------------------------------------------------

    /// Three variables are fine for three columns. A long list is a file somebody can review.
    [Fact]
    public async Task The_masking_baseline_can_be_a_file()
    {
        var ct = TestContext.Current.CancellationToken;

        var file = Write("masking.json", """
            { "extra": ["reference", "iban"], "never": ["public_key"] }
            """);

        using var factory = Factory(
            ("WDS_MASK_FILE", file),
            ("WDS_CONN_SHOP", "sqlite:///:memory:"),
            // Both sources count: a file and a variable are two ways of saying the same thing.
            ("WDS_MASK_EXTRA", "from_variable"));

        var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", ct));
        var id = document.RootElement[0].GetProperty("id").GetString()!;

        var policy = await client.GetFromJsonAsync<JsonElement>($"/api/data/{id}/mask-policy", ct);
        var extra = policy.GetProperty("extra").EnumerateArray()
            .Select(one => one.GetString()).ToList();

        Assert.Contains("reference", extra);
        Assert.Contains("iban", extra);
        Assert.Contains("from_variable", extra);
        Assert.Contains("public_key",
            policy.GetProperty("never").EnumerateArray().Select(one => one.GetString()));
    }

    // --- dashboards -------------------------------------------------------------------------------

    [Fact]
    public async Task A_dashboard_the_deployment_ships_is_listed_and_cannot_be_changed()
    {
        var ct = TestContext.Current.CancellationToken;

        var file = Write("dashboards.json", """
            [
              { "name": "Morning", "refreshSeconds": 60,
                "tiles": [ { "title": "Orders", "connectionId": "c1",
                             "sql": "SELECT count(*) FROM orders", "view": "number", "width": 1 } ] }
            ]
            """);

        using var factory = Factory(("WDS_DASHBOARD_FILE", file));
        var client = factory.CreateClient();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/dashboards", ct);
        var one = Assert.Single(listed.GetProperty("dashboards").EnumerateArray().ToList());

        Assert.Equal("Morning", one.GetProperty("name").GetString());
        Assert.True(one.GetProperty("fromFile").GetBoolean());
        Assert.Equal(60, one.GetProperty("refreshSeconds").GetInt32());

        var id = one.GetProperty("id").GetString()!;

        // It belongs to the deployment: the studio shows it and says so rather than pretending.
        var edited = await client.PutAsJsonAsync($"/api/dashboards/{id}", new
        {
            name = "Mine now", tiles = Array.Empty<object>(),
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, edited.StatusCode);
        Assert.Contains("comes with the deployment", await edited.Content.ReadAsStringAsync(ct));

        var deleted = await client.DeleteAsync($"/api/dashboards/{id}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
    }

    // --- snippets ---------------------------------------------------------------------------------

    [Fact]
    public async Task Snippets_a_stack_ships_are_offered_to_everybody_who_opens_it()
    {
        var ct = TestContext.Current.CancellationToken;

        var file = Write("snippets.json", """
            [ { "prefix": "tenant", "label": "tenant filter", "body": "WHERE tenant_id = ${1:1}" } ]
            """);

        using var factory = Factory(("WDS_SNIPPETS_FILE", file));

        var snippets = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/api/deployment/snippets", ct);

        var one = Assert.Single(snippets.EnumerateArray().ToList());

        Assert.Equal("tenant", one.GetProperty("prefix").GetString());
        Assert.Contains("tenant_id", one.GetProperty("body").GetString());
        // Where it came from, so nobody wonders why they cannot delete it.
        Assert.Equal("from the deployment", one.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Without_a_snippet_file_the_list_is_empty_rather_than_an_error()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();

        var snippets = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/api/deployment/snippets", ct);

        Assert.Empty(snippets.EnumerateArray());
    }

    // --- preferences ------------------------------------------------------------------------------

    [Fact]
    public async Task Preferences_a_deployment_sets_are_the_ones_a_studio_starts_with()
    {
        var ct = TestContext.Current.CancellationToken;

        var file = Write("preferences.json", """
            { "timeZone": "UTC", "pageSize": 500, "notifyAfterSeconds": 0 }
            """);

        using var factory = Factory(("WDS_PREFERENCES_FILE", file));

        var body = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/api/deployment/preferences", ct);

        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Equal("UTC", body.GetProperty("preferences").GetProperty("timeZone").GetString());
        Assert.Equal(500, body.GetProperty("preferences").GetProperty("pageSize").GetInt32());

        // What a file does not say keeps the studio's own default, so it is null rather than 0.
        Assert.Equal(JsonValueKind.Null,
            body.GetProperty("preferences").GetProperty("historySnapshots").ValueKind);
    }

    [Fact]
    public async Task Without_a_preferences_file_the_studio_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();

        var body = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/api/deployment/preferences", ct);

        Assert.False(body.GetProperty("configured").GetBoolean());
    }
}
