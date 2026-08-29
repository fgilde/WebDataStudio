using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A page of statements somebody wants on a screen. The studio keeps the page; the tiles run
/// through the same query endpoint as everything else.
public class DashboardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-dashboards").FullName;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory(bool workspace = true)
    {
        if (!workspace) File.WriteAllText(Path.Combine(_dir, "not-a-directory"), "not a directory");

        return Build(workspace);
    }

    private WebApplicationFactory<Program> Build(bool workspace) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A path inside a file is a path no SQLite can open, which is what "this studio has
                // no workspace" looks like from here.
                ["DB_PATH"] = workspace
                    ? Path.Combine(_dir, "wds.db")
                    : Path.Combine(_dir, "not-a-directory", "wds.db"),
                ["WDS_CONN_SHOP"] = "sqlite:///:memory:",
            })));

    private static object Tile(string title = "Orders today", string view = "number", int width = 1) =>
        new { title, connectionId = "c1", sql = "SELECT count(*) FROM orders", view, width };

    [Fact]
    public async Task A_dashboard_is_kept_and_comes_back_with_its_tiles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var saved = await (await client.PostAsJsonAsync("/api/dashboards", new
        {
            name = "Morning", tiles = new[] { Tile(), Tile("By status", "chart", 2) }, refreshSeconds = 60,
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("Morning", saved.GetProperty("name").GetString());
        Assert.Equal(2, saved.GetProperty("tiles").GetArrayLength());
        Assert.Equal(60, saved.GetProperty("refreshSeconds").GetInt32());

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/dashboards", ct);
        var one = Assert.Single(listed.GetProperty("dashboards").EnumerateArray().ToList());

        Assert.Equal("Morning", one.GetProperty("name").GetString());
    }

    [Fact]
    public async Task It_is_edited_in_place_and_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var saved = await (await client.PostAsJsonAsync("/api/dashboards", new
        {
            name = "Morning", tiles = new[] { Tile() },
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        var id = saved.GetProperty("id").GetString()!;

        var updated = await (await client.PutAsJsonAsync($"/api/dashboards/{id}", new
        {
            name = "Evening", tiles = new[] { Tile(), Tile("Second") },
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("Evening", updated.GetProperty("name").GetString());
        Assert.Equal(id, updated.GetProperty("id").GetString());
        Assert.Equal(2, updated.GetProperty("tiles").GetArrayLength());

        (await client.DeleteAsync($"/api/dashboards/{id}", ct)).EnsureSuccessStatusCode();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/dashboards", ct);
        Assert.Empty(listed.GetProperty("dashboards").EnumerateArray());
    }

    [Fact]
    public async Task Editing_one_that_is_not_there_is_a_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();

        var response = await factory.CreateClient().PutAsJsonAsync("/api/dashboards/nope", new
        {
            name = "Morning", tiles = Array.Empty<object>(),
        }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// A typo in the view is not a blank box, and a refresh of one second is a load test.
    [Fact]
    public async Task What_a_tile_says_is_checked_rather_than_trusted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var saved = await (await client.PostAsJsonAsync("/api/dashboards", new
        {
            name = "Morning",
            tiles = new[]
            {
                new { title = "", connectionId = "c1", sql = "SELECT 1", view = "hologram", width = 99 },
                new { title = "no sql", connectionId = "c1", sql = "   ", view = "table", width = 1 },
            },
            refreshSeconds = 1,
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        var tile = Assert.Single(saved.GetProperty("tiles").EnumerateArray().ToList());

        Assert.Equal("untitled", tile.GetProperty("title").GetString());
        Assert.Equal("table", tile.GetProperty("view").GetString());
        Assert.Equal(4, tile.GetProperty("width").GetInt32());
        Assert.Equal(10, saved.GetProperty("refreshSeconds").GetInt32());
    }

    [Fact]
    public async Task A_dashboard_needs_a_name()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/dashboards", new
        {
            name = "  ", tiles = Array.Empty<object>(),
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// A studio with no workspace file keeps nothing, and says so instead of losing the page.
    [Fact]
    public async Task Without_a_workspace_it_says_it_cannot_keep_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(workspace: false);
        var client = factory.CreateClient();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/dashboards", ct);
        Assert.False(listed.GetProperty("available").GetBoolean());

        var response = await client.PostAsJsonAsync("/api/dashboards", new
        {
            name = "Morning", tiles = Array.Empty<object>(),
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
