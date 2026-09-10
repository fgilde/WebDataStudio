using System.Text.Json;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Nothing asks what shape a dashboard file is in. Three are in the world, and this is the one
/// place that tells them apart.
public class DashboardFormatTests
{
    private static DashboardImport Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return DashboardFormats.Read(document.RootElement);
    }

    [Fact]
    public void Our_own_document_is_read_as_it_is()
    {
        var read = Read("""
        {
          "name": "Morning",
          "refreshSeconds": 60,
          "timeRange": { "from": "now-6h", "to": "now" },
          "widgets": [
            {
              "id": "w1", "type": "Stat", "title": "Customers",
              "position": { "x": 0, "y": 0, "w": 6, "h": 4 },
              "source": { "kind": "Sql", "connectionId": "c1", "sql": "SELECT count(*) FROM customers" }
            }
          ]
        }
        """);

        var dashboard = read.Dashboard!;

        Assert.Empty(read.Notes);
        Assert.Equal("Morning", dashboard.Name);
        Assert.Equal("now-6h", dashboard.TimeRange!.From);
        Assert.Equal(WidgetType.Stat, Assert.Single(dashboard.Widgets).Type);
    }

    /// What every dashboard file in a repository and every app host written so far looks like.
    [Fact]
    public void The_old_shape_lays_itself_out()
    {
        var read = Read("""
        {
          "name": "Morning",
          "refreshSeconds": 30,
          "tiles": [
            { "title": "Customers", "connectionId": "c1", "sql": "SELECT count(*) FROM customers", "view": "number", "width": 1 },
            { "title": "By status", "connectionId": "c1", "sql": "SELECT status, count(*) FROM orders GROUP BY status", "view": "chart", "width": 2 }
          ]
        }
        """);

        var dashboard = read.Dashboard!;

        Assert.Equal(2, dashboard.Widgets.Count);
        Assert.Equal(WidgetType.Stat, dashboard.Widgets[0].Type);
        Assert.Equal(6, dashboard.Widgets[0].Position.W);
        Assert.Equal(30, dashboard.RefreshSeconds);
    }

    [Fact]
    public void A_grafana_dashboard_is_recognised_without_being_told()
    {
        var read = Read("""
        {
          "title": "From Grafana", "schemaVersion": 39,
          "panels": [
            {
              "type": "stat", "title": "Rows", "gridPos": { "x": 0, "y": 0, "w": 6, "h": 4 },
              "targets": [{ "rawSql": "SELECT count(*) FROM orders" }]
            }
          ]
        }
        """);

        Assert.Equal("From Grafana", read.Dashboard!.Name);
        Assert.Equal(WidgetType.Stat, Assert.Single(read.Dashboard.Widgets).Type);
    }

    [Fact]
    public void A_file_holding_several_gives_several()
    {
        using var document = JsonDocument.Parse("""
        [
          { "name": "One", "tiles": [{ "title": "a", "connectionId": "c1", "sql": "SELECT 1", "view": "number", "width": 1 }] },
          { "title": "Two", "panels": [{ "type": "table", "title": "b", "targets": [{ "rawSql": "SELECT 2" }] }] }
        ]
        """);

        var (dashboards, _) = DashboardFormats.All(document.RootElement);

        Assert.Equal(2, dashboards.Count);
        Assert.Equal("One", dashboards[0].Name);
        Assert.Equal("Two", dashboards[1].Name);
    }

    [Fact]
    public void A_json_that_is_no_dashboard_at_all_says_so()
    {
        var read = Read("""{ "hello": "world" }""");

        Assert.Null(read.Dashboard);
        Assert.Contains("widgets", Assert.Single(read.Notes));
    }

    [Fact]
    public void A_json_that_is_not_even_an_object_says_so()
    {
        var read = Read("\"just a string\"");

        Assert.Null(read.Dashboard);
        Assert.NotEmpty(read.Notes);
    }
}
