using System.Text.Json;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Grafana dashboards, in and out.
///
/// The fixture is the shape a real export has, including the parts that cannot come along: a
/// Prometheus panel, a transformation, a panel type nothing here draws, and a collapsed row with
/// its children hidden inside it.
public class GrafanaDashboardTests
{
    private const string Exported = """
    {
      "uid": "shop-overview",
      "title": "Shop overview",
      "tags": ["shop", "ops"],
      "schemaVersion": 39,
      "refresh": "30s",
      "time": { "from": "now-7d", "to": "now" },
      "templating": {
        "list": [
          {
            "name": "region", "label": "Region", "type": "query", "multi": true,
            "includeAll": true, "datasource": { "type": "grafana-postgresql-datasource", "uid": "SHOP" },
            "query": "SELECT DISTINCT region FROM customers",
            "current": { "text": "eu", "value": "eu" }
          },
          { "name": "plan", "type": "custom", "query": "free, pro, team" },
          { "name": "adhoc", "type": "adhoc" }
        ]
      },
      "panels": [
        {
          "id": 1, "type": "stat", "title": "Customers",
          "gridPos": { "h": 4, "w": 6, "x": 0, "y": 0 },
          "datasource": { "type": "grafana-postgresql-datasource", "uid": "SHOP" },
          "targets": [{ "refId": "A", "rawSql": "SELECT count(*) FROM customers", "rawQuery": true }],
          "fieldConfig": {
            "defaults": {
              "unit": "short", "decimals": 0,
              "thresholds": { "mode": "absolute", "steps": [
                { "color": "green", "value": null },
                { "color": "orange", "value": 100 },
                { "color": "red", "value": 500 }
              ]}
            }
          }
        },
        {
          "id": 2, "type": "gauge", "title": "Disk used",
          "gridPos": { "h": 4, "w": 6, "x": 6, "y": 0 },
          "datasource": { "uid": "SHOP" },
          "targets": [{ "rawSql": "SELECT 61", "rawQuery": true }],
          "fieldConfig": { "defaults": { "min": 0, "max": 100, "unit": "percent" } }
        },
        {
          "id": 3, "type": "timeseries", "title": "Orders over time",
          "description": "one point per hour",
          "gridPos": { "h": 8, "w": 12, "x": 12, "y": 0 },
          "datasource": { "uid": "SHOP" },
          "targets": [{ "rawSql": "SELECT $__timeGroup(placed_at, '1h'), count(*) FROM orders GROUP BY 1", "rawQuery": true }],
          "fieldConfig": { "defaults": { "custom": { "stacking": { "mode": "normal" } } } },
          "options": { "legend": { "showLegend": false } }
        },
        {
          "id": 4, "type": "barchart", "title": "By status",
          "gridPos": { "h": 6, "w": 8, "x": 0, "y": 4 },
          "datasource": { "uid": "SHOP" },
          "targets": [{ "rawSql": "SELECT status, count(*) FROM orders GROUP BY status", "rawQuery": true }],
          "options": { "orientation": "horizontal" },
          "transformations": [{ "id": "organize", "options": {} }]
        },
        {
          "id": 5, "type": "text", "title": "How to read this",
          "gridPos": { "h": 4, "w": 8, "x": 8, "y": 4 },
          "options": { "mode": "markdown", "content": "**Orders** are counted when they are paid." }
        },
        {
          "id": 6, "type": "table", "title": "Slowest queries",
          "gridPos": { "h": 8, "w": 24, "x": 0, "y": 10 },
          "datasource": { "uid": "SHOP" },
          "targets": [{ "rawSql": "SELECT query, mean_ms FROM pg_stat_statements", "rawQuery": true, "format": "table" }]
        },
        {
          "id": 7, "type": "row", "title": "Infrastructure", "collapsed": true,
          "gridPos": { "h": 1, "w": 24, "x": 0, "y": 18 },
          "panels": [
            {
              "id": 8, "type": "timeseries", "title": "CPU",
              "gridPos": { "h": 6, "w": 12, "x": 0, "y": 19 },
              "datasource": { "type": "prometheus", "uid": "prom" },
              "targets": [{ "refId": "A", "expr": "rate(cpu_seconds_total[5m])" }]
            }
          ]
        },
        {
          "id": 9, "type": "nodeGraph", "title": "Service map",
          "gridPos": { "h": 6, "w": 12, "x": 12, "y": 19 },
          "datasource": { "uid": "SHOP" },
          "targets": [{ "rawSql": "SELECT 1 AS id", "rawQuery": true }]
        },
        {
          "id": 10, "type": "piechart", "title": "Plans",
          "gridPos": { "h": 6, "w": 12, "x": 0, "y": 25 },
          "datasource": { "uid": "WAREHOUSE" },
          "targets": [{ "rawSql": "SELECT plan, count(*) FROM accounts GROUP BY plan", "rawQuery": true }]
        }
      ]
    }
    """;

    private static readonly IReadOnlyList<ConnectionSpecName> Connections =
        [new ConnectionSpecName("c1", "SHOP")];

    private static DashboardImport Read(string json = Exported)
    {
        using var document = JsonDocument.Parse(json);
        return GrafanaDashboards.Read(document.RootElement, Connections);
    }

    [Fact]
    public void The_dashboard_itself_comes_across()
    {
        var dashboard = Read().Dashboard!;

        Assert.Equal("Shop overview", dashboard.Name);
        Assert.Equal(30, dashboard.RefreshSeconds);
        Assert.Equal("now-7d", dashboard.TimeRange!.From);
        Assert.Equal(["shop", "ops"], dashboard.Tags);
    }

    [Fact]
    public void Each_panel_type_becomes_the_widget_that_draws_it()
    {
        var widgets = Read().Dashboard!.Widgets.ToDictionary(one => one.Title, one => one.Type);

        Assert.Equal(WidgetType.Stat, widgets["Customers"]);
        Assert.Equal(WidgetType.Gauge, widgets["Disk used"]);
        // Stacked in its fieldConfig, so it is the stacked form rather than a flag nobody reads.
        Assert.Equal(WidgetType.StackedArea, widgets["Orders over time"]);
        Assert.Equal(WidgetType.Bar, widgets["By status"]);
        Assert.Equal(WidgetType.Text, widgets["How to read this"]);
        Assert.Equal(WidgetType.Table, widgets["Slowest queries"]);
        Assert.Equal(WidgetType.Row, widgets["Infrastructure"]);
        Assert.Equal(WidgetType.Pie, widgets["Plans"]);
    }

    /// Twenty-four columns on both sides, so this is a copy.
    [Fact]
    public void The_grid_is_the_same_grid()
    {
        var widget = Read().Dashboard!.Widgets.First(one => one.Title == "Orders over time");

        Assert.Equal(new WidgetPosition(12, 0, 12, 8), widget.Position);
    }

    [Fact]
    public void The_statement_and_its_connection_come_across()
    {
        var widget = Read().Dashboard!.Widgets.First(one => one.Title == "Customers");

        Assert.Equal("SELECT count(*) FROM customers", widget.Source.Sql);
        // Matched by name: the datasource uid is SHOP and so is a connection here.
        Assert.Equal("c1", widget.Source.ConnectionId);
    }

    [Fact]
    public void A_datasource_that_is_not_a_connection_here_is_said_out_loud()
    {
        var read = Read();
        var widget = read.Dashboard!.Widgets.First(one => one.Title == "Plans");

        Assert.Null(widget.Source.ConnectionId);
        Assert.Contains(read.Notes, note => note.Contains("WAREHOUSE") && note.Contains("Plans"));
    }

    [Fact]
    public void The_field_config_becomes_the_options()
    {
        var widgets = Read().Dashboard!.Widgets.ToDictionary(one => one.Title);

        var stat = widgets["Customers"].Options;
        Assert.Equal("short", stat.Unit);
        Assert.Equal(0, stat.Decimals);
        // The step with no value is Grafana's base colour, not a threshold.
        Assert.Equal([new WidgetThreshold(100, "serious"), new WidgetThreshold(500, "critical")],
            stat.Thresholds);

        var gauge = widgets["Disk used"].Options;
        Assert.Equal(0, gauge.Min);
        Assert.Equal(100, gauge.Max);

        Assert.False(widgets["Orders over time"].Options.Legend);
        Assert.Equal("horizontal", widgets["By status"].Options.Orientation);
        Assert.Contains("**Orders**", widgets["How to read this"].Options.Markdown!);
        Assert.Equal("one point per hour", widgets["Orders over time"].Description);
    }

    [Fact]
    public void A_collapsed_rows_children_come_along_too()
    {
        var read = Read();

        // The CPU panel is a Prometheus panel, so it is a note rather than an empty widget — but the
        // row it lived in is still there.
        Assert.Contains(read.Dashboard!.Widgets, one => one.Title == "Infrastructure");
        Assert.Contains(read.Notes, note => note.Contains("CPU") && note.Contains("metrics"));
        Assert.DoesNotContain(read.Dashboard.Widgets, one => one.Title == "CPU");
    }

    [Fact]
    public void What_cannot_come_along_is_a_sentence_rather_than_a_surprise()
    {
        var notes = Read().Notes;

        Assert.Contains(notes, note => note.Contains("transformation") && note.Contains("By status"));
        Assert.Contains(notes, note => note.Contains("nodeGraph"));
        Assert.Contains(notes, note => note.Contains("adhoc"));
    }

    /// A panel type nothing here draws still has a statement, and rows are better than nothing.
    [Fact]
    public void An_unknown_panel_type_comes_in_as_its_rows()
    {
        var widget = Read().Dashboard!.Widgets.First(one => one.Title == "Service map");

        Assert.Equal(WidgetType.Table, widget.Type);
        Assert.Equal("SELECT 1 AS id", widget.Source.Sql);
    }

    [Fact]
    public void Variables_come_across_with_what_they_are()
    {
        var variables = Read().Dashboard!.Variables!.ToDictionary(one => one.Name);

        Assert.Equal(VariableKind.Query, variables["region"].Kind);
        Assert.Equal("SELECT DISTINCT region FROM customers", variables["region"].Sql);
        Assert.True(variables["region"].Multi);
        Assert.True(variables["region"].IncludeAll);
        Assert.Equal("eu", variables["region"].Default);

        Assert.Equal(VariableKind.Custom, variables["plan"].Kind);
        Assert.Equal(["free", "pro", "team"], variables["plan"].Values);
    }

    [Fact]
    public void An_export_from_the_api_is_unwrapped()
    {
        var read = Read($$"""{ "meta": { "type": "db" }, "dashboard": {{Exported}} }""");

        Assert.Equal("Shop overview", read.Dashboard!.Name);
    }

    // --- out ------------------------------------------------------------------------------------

    [Fact]
    public void What_came_in_goes_back_out()
    {
        var dashboard = Read().Dashboard! with { Id = "shop-overview" };
        var written = GrafanaDashboards.Write(dashboard, Connections);

        Assert.Equal("Shop overview", written["title"]!.GetValue<string>());
        Assert.Equal(GrafanaDashboards.SchemaVersion, written["schemaVersion"]!.GetValue<int>());
        Assert.Equal("30s", written["refresh"]!.GetValue<string>());
        Assert.Equal("now-7d", written["time"]!["from"]!.GetValue<string>());
        Assert.Equal(dashboard.Widgets.Count, written["panels"]!.AsArray().Count);

        var stat = written["panels"]!.AsArray()
            .First(one => one!["title"]!.GetValue<string>() == "Customers")!;

        Assert.Equal("stat", stat["type"]!.GetValue<string>());
        Assert.Equal("SELECT count(*) FROM customers",
            stat["targets"]![0]!["rawSql"]!.GetValue<string>());
        Assert.Equal(6, stat["gridPos"]!["w"]!.GetValue<int>());
        // The connection's name is what a Grafana datasource is called, not our id.
        Assert.Equal("SHOP", stat["datasource"]!["uid"]!.GetValue<string>());

        // Their thresholds start with a base step that has no value; ours resume after it.
        var steps = stat["fieldConfig"]!["defaults"]!["thresholds"]!["steps"]!.AsArray();
        Assert.Equal(3, steps.Count);
        Assert.Equal("orange", steps[1]!["color"]!.GetValue<string>());

        var variables = written["templating"]!["list"]!.AsArray();
        Assert.Contains(variables, one => one!["name"]!.GetValue<string>() == "region"
                                          && one["type"]!.GetValue<string>() == "query");
    }

    /// A dashboard built here has no original panel to start from, and still has to open there.
    [Fact]
    public void A_dashboard_built_here_exports_too()
    {
        var dashboard = Dashboards.Normalise(new Dashboard("d1", "Mine",
        [
            new Widget("w1", WidgetType.StackedBar, "Sales", new WidgetPosition(0, 0, 12, 6),
                new WidgetSource(SourceKind.Sql, "c1", "SELECT region, sum(total) FROM sales GROUP BY region"),
                new WidgetMapping(Category: "region", Values: ["sum"]),
                new WidgetOptions(Unit: "currencyEUR", Stacked: true,
                    Thresholds: [new WidgetThreshold(1000, "good")])),
        ], 60, DateTimeOffset.UtcNow));

        var panel = GrafanaDashboards.Write(dashboard, Connections)["panels"]!.AsArray()[0]!;

        Assert.Equal("barchart", panel["type"]!.GetValue<string>());
        Assert.Equal("currencyEUR", panel["fieldConfig"]!["defaults"]!["unit"]!.GetValue<string>());
        Assert.Equal("normal",
            panel["fieldConfig"]!["defaults"]!["custom"]!["stacking"]!["mode"]!.GetValue<string>());
        Assert.Equal("SELECT region, sum(total) FROM sales GROUP BY region",
            panel["targets"]![0]!["rawSql"]!.GetValue<string>());
    }

    /// The fields this studio never read are the reason the original panel is kept.
    [Fact]
    public void An_export_puts_back_what_this_studio_never_read()
    {
        var dashboard = Read().Dashboard!;
        var written = GrafanaDashboards.Write(dashboard, Connections);

        var bar = written["panels"]!.AsArray()
            .First(one => one!["title"]!.GetValue<string>() == "By status")!;

        // Nothing here reads transformations, and they still go back.
        Assert.Equal("organize", bar["transformations"]![0]!["id"]!.GetValue<string>());
    }
}
