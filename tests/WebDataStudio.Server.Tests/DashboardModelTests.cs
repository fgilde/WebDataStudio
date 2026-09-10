using System.Text.Json;
using System.Text.Json.Nodes;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The model, what it does with what a browser sends, and the way back to the shape dashboards had
/// before there was a canvas.
public class DashboardModelTests
{
    private static Widget Widget(WidgetType type = WidgetType.Bar, int x = 0, int y = 0,
        int w = 8, int h = 6) =>
        new("", type, "Orders", new WidgetPosition(x, y, w, h),
            new WidgetSource(SourceKind.Sql, "c1", " SELECT 1 "),
            new WidgetMapping(Category: "status", Values: ["count"]),
            new WidgetOptions());

    private static Dashboard Dashboard(params Widget[] widgets) =>
        new("", "Morning", widgets, 60, DateTimeOffset.UtcNow);

    [Fact]
    public void A_widget_outside_the_grid_comes_back_inside_it()
    {
        var normalised = Dashboards.Normalise(Dashboard(Widget(x: 40, w: 40, h: 0)));

        var widget = Assert.Single(normalised.Widgets);

        Assert.Equal(24, widget.Position.W);
        Assert.Equal(0, widget.Position.X);
        Assert.True(widget.Position.H >= 2);
    }

    [Fact]
    public void Every_widget_gets_an_id_and_a_title()
    {
        var normalised = Dashboards.Normalise(Dashboard(Widget() with { Title = "  " }));
        var widget = Assert.Single(normalised.Widgets);

        Assert.NotEmpty(widget.Id);
        Assert.Equal("untitled", widget.Title);
        Assert.Equal("SELECT 1", widget.Source.Sql);
    }

    /// A refresh of one second is a load test, and sixty widgets is already a wall.
    [Fact]
    public void What_a_browser_sends_is_clamped_rather_than_trusted()
    {
        var many = Enumerable.Range(0, 200).Select(_ => Widget()).ToArray();

        var normalised = Dashboards.Normalise(Dashboard(many) with { RefreshSeconds = 1 });

        Assert.Equal(Dashboards.MaxWidgets, normalised.Widgets.Count);
        Assert.Equal(10, normalised.RefreshSeconds);
        Assert.Equal(24, normalised.Layout!.Columns);
    }

    /// A threshold's level is one of four reserved states; anything else is a colour a dashboard
    /// tried to pick, and those do not exist here.
    [Fact]
    public void A_threshold_keeps_only_the_states_that_mean_something()
    {
        var widget = Widget(WidgetType.Stat) with
        {
            Options = new WidgetOptions(Thresholds:
            [
                new WidgetThreshold(90, "critical"),
                new WidgetThreshold(10, "good"),
                new WidgetThreshold(50, "chartreuse"),
            ]),
        };

        var thresholds = Assert.Single(Dashboards.Normalise(Dashboard(widget)).Widgets).Options.Thresholds!;

        Assert.Equal(2, thresholds.Count);
        Assert.Equal(10, thresholds[0].Value);   // sorted, so a renderer reads them in order
        Assert.Equal(90, thresholds[1].Value);
    }

    [Fact]
    public void The_old_tiles_become_widgets_in_the_order_they_were_written()
    {
        var widgets = Dashboards.FromTiles(
        [
            new DashboardTile("Customers", "c1", "SELECT count(*) FROM customers", "number", 1),
            new DashboardTile("By status", "c1", "SELECT status, count(*) FROM orders", "chart", 2),
            new DashboardTile("Nothing", "c1", "  ", "table", 1),
            new DashboardTile("Rows", "c1", "SELECT * FROM orders", "table", 4),
        ]);

        Assert.Equal(3, widgets.Count);
        Assert.Equal(WidgetType.Stat, widgets[0].Type);
        Assert.Equal(WidgetType.Bar, widgets[1].Type);
        Assert.Equal(WidgetType.Table, widgets[2].Type);

        // Six columns per quarter of the grid, and the full-width one wraps to its own row.
        Assert.Equal(new WidgetPosition(0, 0, 6, 6), widgets[0].Position);
        Assert.Equal(new WidgetPosition(6, 0, 12, 6), widgets[1].Position);
        Assert.Equal(new WidgetPosition(0, 6, 24, 6), widgets[2].Position);
    }

    /// The way back, for an image rolled back onto the same workspace file.
    [Fact]
    public void A_dashboard_can_still_be_read_as_tiles()
    {
        var dashboard = Dashboards.Normalise(Dashboard(
            Widget(WidgetType.Stat, w: 6), Widget(WidgetType.Line, x: 6, w: 12),
            Widget(WidgetType.Row, x: 0, y: 6, w: 24, h: 1)));

        var tiles = Dashboards.ToTiles(dashboard);

        // A row is structure, not a statement, so it is not a tile at all.
        Assert.Equal(2, tiles.Count);
        Assert.Equal("number", tiles[0].View);
        Assert.Equal(1, tiles[0].Width);
        Assert.Equal("chart", tiles[1].View);
        Assert.Equal(2, tiles[1].Width);
    }

    /// The panel Grafana wrote is kept so an export can put back what came in, and nothing that
    /// draws ever reads it.
    [Fact]
    public void The_original_grafana_panel_survives_a_round_trip()
    {
        var widget = Widget() with
        {
            Options = new WidgetOptions(Grafana: JsonNode.Parse("""{"type":"timeseries","id":7}""")),
        };

        var json = JsonSerializer.Serialize(Dashboards.Normalise(Dashboard(widget)), Dashboards.Json);
        var back = JsonSerializer.Deserialize<Dashboard>(json, Dashboards.Json)!;

        Assert.Equal(7, back.Widgets[0].Options.Grafana!["id"]!.GetValue<int>());
    }

    [Fact]
    public void A_variable_name_is_an_identifier_or_it_is_made_into_one()
    {
        var normalised = Dashboards.Normalise(Dashboard() with
        {
            Variables =
            [
                new DashboardVariable("region name", VariableKind.Custom, Values: ["eu", "us"]),
                new DashboardVariable("  ", VariableKind.Custom),
            ],
        });

        var variable = Assert.Single(normalised.Variables!);
        Assert.Equal("region_name", variable.Name);
    }
}
