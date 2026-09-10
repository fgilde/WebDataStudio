using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A canvas of widgets somebody wants on a screen. The studio keeps the page; the widgets run
/// through the same query endpoint as everything else.
public class DashboardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-dashboards").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory(bool workspace = true, string? dashboardFile = null)
    {
        if (!workspace) File.WriteAllText(Path.Combine(_dir, "not-a-directory"), "not a directory");

        var settings = new Dictionary<string, string?>
        {
            // A path inside a file is a path no SQLite can open, which is what "this studio has
            // no workspace" looks like from here.
            ["DB_PATH"] = workspace
                ? Path.Combine(_dir, "wds.db")
                : Path.Combine(_dir, "not-a-directory", "wds.db"),
            ["WDS_CONN_SHOP"] = "sqlite:///:memory:",
        };

        if (dashboardFile is not null) settings["WDS_DASHBOARD_FILE"] = dashboardFile;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings)));
    }

    private static object Widget(string title = "Orders today", string type = "Stat",
        int x = 0, int w = 6) =>
        new
        {
            id = "", type, title,
            position = new { x, y = 0, w, h = 4 },
            source = new { kind = "Sql", connectionId = "c1", sql = "SELECT count(*) FROM orders" },
        };

    /// The shape every dashboard file and app host written before the canvas uses.
    private static object Tile(string title = "Orders today", string view = "number", int width = 1) =>
        new { title, connectionId = "c1", sql = "SELECT count(*) FROM orders", view, width };

    private static async Task<JsonElement> PostAsync(HttpClient client, object body) =>
        await (await client.PostAsJsonAsync("/api/dashboards", body, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_dashboard_is_kept_and_comes_back_with_its_widgets()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var saved = await PostAsync(client, new
        {
            name = "Morning",
            widgets = new[] { Widget(), Widget("By status", "Bar", 6, 12) },
            refreshSeconds = 60,
            timeRange = new { from = "now-6h", to = "now" },
        });

        Assert.Equal("Morning", saved.GetProperty("name").GetString());
        Assert.Equal(2, saved.GetProperty("widgets").GetArrayLength());
        Assert.Equal(60, saved.GetProperty("refreshSeconds").GetInt32());
        Assert.Equal("now-6h", saved.GetProperty("timeRange").GetProperty("from").GetString());

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/dashboards", Ct);
        var one = Assert.Single(listed.GetProperty("dashboards").EnumerateArray().ToList());

        Assert.Equal("Morning", one.GetProperty("name").GetString());
        Assert.Equal(2, one.GetProperty("widgets").GetArrayLength());
    }

    /// The compatibility that matters: a page written as tiles still becomes a dashboard.
    [Fact]
    public async Task A_page_of_tiles_still_saves_and_lays_itself_out()
    {
        using var factory = Factory();

        var saved = await PostAsync(factory.CreateClient(), new
        {
            name = "Morning",
            tiles = new[] { Tile(), Tile("By status", "chart", 2), Tile("Nothing", "table", 1) },
        });

        var widgets = saved.GetProperty("widgets").EnumerateArray().ToList();

        Assert.Equal(3, widgets.Count);
        Assert.Equal("Stat", widgets[0].GetProperty("type").GetString());
        Assert.Equal(6, widgets[0].GetProperty("position").GetProperty("w").GetInt32());
        Assert.Equal("Bar", widgets[1].GetProperty("type").GetString());
        Assert.Equal(12, widgets[1].GetProperty("position").GetProperty("w").GetInt32());
    }

    [Fact]
    public async Task It_is_edited_in_place_and_deleted()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var saved = await PostAsync(client, new { name = "Morning", widgets = new[] { Widget() } });
        var id = saved.GetProperty("id").GetString()!;

        var updated = await (await client.PutAsJsonAsync($"/api/dashboards/{id}", new
        {
            name = "Evening", widgets = new[] { Widget(), Widget("Second", "Table", 6, 12) },
        }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal("Evening", updated.GetProperty("name").GetString());
        Assert.Equal(id, updated.GetProperty("id").GetString());
        Assert.Equal(2, updated.GetProperty("widgets").GetArrayLength());

        (await client.DeleteAsync($"/api/dashboards/{id}", Ct)).EnsureSuccessStatusCode();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/dashboards", Ct);
        Assert.Empty(listed.GetProperty("dashboards").EnumerateArray());
    }

    [Fact]
    public async Task Editing_one_that_is_not_there_is_a_404()
    {
        using var factory = Factory();

        var response = await factory.CreateClient().PutAsJsonAsync("/api/dashboards/nope", new
        {
            name = "Morning", widgets = Array.Empty<object>(),
        }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// A widget at column forty is a widget nobody can see, and a refresh of one second is a load
    /// test.
    [Fact]
    public async Task What_a_widget_says_is_checked_rather_than_trusted()
    {
        using var factory = Factory();

        var saved = await PostAsync(factory.CreateClient(), new
        {
            name = "Morning",
            widgets = new[]
            {
                new
                {
                    id = "", type = "Bar", title = "  ",
                    position = new { x = 40, y = 0, w = 99, h = 0 },
                    source = new { kind = "Sql", connectionId = "c1", sql = "  SELECT 1  " },
                },
            },
            refreshSeconds = 1,
        });

        var widget = Assert.Single(saved.GetProperty("widgets").EnumerateArray().ToList());
        var position = widget.GetProperty("position");

        Assert.Equal("untitled", widget.GetProperty("title").GetString());
        Assert.Equal(24, position.GetProperty("w").GetInt32());
        Assert.Equal(0, position.GetProperty("x").GetInt32());
        Assert.True(position.GetProperty("h").GetInt32() >= 2);
        Assert.Equal("SELECT 1", widget.GetProperty("source").GetProperty("sql").GetString());
        Assert.Equal(10, saved.GetProperty("refreshSeconds").GetInt32());
    }

    [Fact]
    public async Task A_dashboard_needs_a_name()
    {
        using var factory = Factory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/dashboards", new
        {
            name = "  ", widgets = Array.Empty<object>(),
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// A studio with no workspace file keeps nothing, and says so instead of losing the page.
    [Fact]
    public async Task Without_a_workspace_it_says_it_cannot_keep_one()
    {
        using var factory = Factory(workspace: false);
        var client = factory.CreateClient();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/dashboards", Ct);
        Assert.False(listed.GetProperty("available").GetBoolean());

        var response = await client.PostAsJsonAsync("/api/dashboards", new
        {
            name = "Morning", widgets = Array.Empty<object>(),
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- what a deployment ships ----------------------------------------------------------------

    /// A folder of Grafana JSON is a folder of dashboards, and nothing had to say so.
    [Fact]
    public async Task A_shipped_grafana_file_is_read_without_being_told_what_it_is()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "shipped")).FullName;

        await File.WriteAllTextAsync(Path.Combine(folder, "ops.json"), """
        {
          "title": "Ops", "schemaVersion": 39,
          "panels": [
            {
              "type": "stat", "title": "Rows", "gridPos": { "x": 0, "y": 0, "w": 6, "h": 4 },
              "datasource": { "uid": "SHOP" },
              "targets": [{ "rawSql": "SELECT count(*) FROM orders" }]
            }
          ]
        }
        """, Ct);

        await File.WriteAllTextAsync(Path.Combine(folder, "legacy.json"), """
        {
          "name": "Legacy",
          "tiles": [{ "title": "a", "connectionId": "c1", "sql": "SELECT 1", "view": "number", "width": 1 }]
        }
        """, Ct);

        using var factory = Factory(dashboardFile: folder);
        var client = factory.CreateClient();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/dashboards", Ct);
        var names = listed.GetProperty("dashboards").EnumerateArray()
            .Select(one => one.GetProperty("name").GetString()).ToList();

        Assert.Contains("Ops", names);
        Assert.Contains("Legacy", names);
        Assert.All(listed.GetProperty("dashboards").EnumerateArray(),
            one => Assert.True(one.GetProperty("fromFile").GetBoolean()));
    }

    [Fact]
    public async Task A_shipped_dashboard_cannot_be_saved_over_or_deleted()
    {
        var file = Path.Combine(_dir, "shipped.json");
        await File.WriteAllTextAsync(file, """
        { "name": "Ops", "widgets": [] }
        """, Ct);

        using var factory = Factory(dashboardFile: file);
        var client = factory.CreateClient();

        var save = await client.PutAsJsonAsync("/api/dashboards/shipped:Ops", new
        {
            name = "Mine", widgets = Array.Empty<object>(),
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.DeleteAsync("/api/dashboards/shipped:Ops", Ct)).StatusCode);
    }

    // --- in and out -----------------------------------------------------------------------------

    [Fact]
    public async Task A_pasted_grafana_dashboard_is_imported_with_its_notes()
    {
        using var factory = Factory();

        var response = await factory.CreateClient().PostAsync("/api/dashboards/import",
            new StringContent("""
            {
              "title": "Pasted", "schemaVersion": 39,
              "panels": [
                {
                  "type": "stat", "title": "Rows", "gridPos": { "x": 0, "y": 0, "w": 6, "h": 4 },
                  "datasource": { "uid": "nowhere" },
                  "targets": [{ "rawSql": "SELECT 1" }]
                },
                {
                  "type": "timeseries", "title": "CPU",
                  "targets": [{ "expr": "rate(cpu[5m])" }]
                }
              ]
            }
            """, Encoding.UTF8, "application/json"), Ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal("Pasted", body.GetProperty("dashboard").GetProperty("name").GetString());
        Assert.Single(body.GetProperty("dashboard").GetProperty("widgets").EnumerateArray());

        var notes = body.GetProperty("notes").EnumerateArray().Select(one => one.GetString()!).ToList();
        Assert.Contains(notes, note => note.Contains("nowhere"));
        Assert.Contains(notes, note => note.Contains("CPU"));

        // Nothing was kept: an import somebody has not looked at is not a dashboard they asked for.
        var listed = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/dashboards", Ct);
        Assert.Empty(listed.GetProperty("dashboards").EnumerateArray());
    }

    [Fact]
    public async Task Something_that_is_not_json_is_a_400_that_says_so()
    {
        using var factory = Factory();

        var response = await factory.CreateClient().PostAsync("/api/dashboards/import",
            new StringContent("not json at all", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_dashboard_here_can_be_taken_to_grafana()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var saved = await PostAsync(client, new
        {
            name = "Morning", widgets = new[] { Widget() }, refreshSeconds = 60,
        });

        var id = saved.GetProperty("id").GetString()!;
        var exported = await client.GetFromJsonAsync<JsonElement>($"/api/dashboards/{id}/grafana", Ct);

        Assert.Equal("Morning", exported.GetProperty("title").GetString());
        Assert.Equal("stat", exported.GetProperty("panels")[0].GetProperty("type").GetString());
        Assert.True(exported.GetProperty("schemaVersion").GetInt32() > 0);
    }
}
