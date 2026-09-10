using System.Text.Json;

namespace WebDataStudio.Server.Services;

/// The one place that decides what a dashboard file is.
///
/// Three shapes are in the world: this studio's own document, the tile list it wrote before there
/// was a canvas, and a Grafana dashboard. An app host that mounts thirteen Grafana files should not
/// have to say so, and neither should somebody pasting one into the JSON view — so nothing asks.
public static class DashboardFormats
{
    /// All of them, for the seeding path where a file may hold a list.
    public static (IReadOnlyList<Dashboard> Dashboards, IReadOnlyList<string> Notes) All(
        JsonElement root, IReadOnlyList<ConnectionSpecName>? connections = null)
    {
        var notes = new List<string>();
        var dashboards = new List<Dashboard>();

        foreach (var one in root.ValueKind == JsonValueKind.Array
                     ? root.EnumerateArray().ToList()
                     : [root])
        {
            var read = Read(one, connections);
            if (read.Dashboard is { } dashboard) dashboards.Add(dashboard);
            notes.AddRange(read.Notes);
        }

        return (dashboards, notes);
    }

    public static DashboardImport Read(JsonElement root,
        IReadOnlyList<ConnectionSpecName>? connections = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new DashboardImport(null,
                ["this is not a dashboard: a dashboard is a JSON object, or an array of them"]);

        // Ours first: a document that says `widgets` is one, whatever else it carries.
        if (root.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
        {
            try
            {
                var dashboard = root.Deserialize<Dashboard>(Dashboards.Json);
                return dashboard is null
                    ? new DashboardImport(null, ["this dashboard could not be read"])
                    : new DashboardImport(Dashboards.Normalise(dashboard), []);
            }
            catch (JsonException e)
            {
                return new DashboardImport(null, [$"this dashboard could not be read: {e.Message}"]);
            }
        }

        if (GrafanaDashboards.LooksLikeOne(root)) return GrafanaDashboards.Read(root, connections);

        // The old shape: a name and a list of tiles, which is what a repository's dashboard file and
        // every app host written so far look like.
        if (root.TryGetProperty("tiles", out var tiles) && tiles.ValueKind == JsonValueKind.Array)
        {
            try
            {
                var legacy = root.Deserialize<LegacyDashboard>(Dashboards.Json);

                return legacy is null
                    ? new DashboardImport(null, ["this dashboard could not be read"])
                    : new DashboardImport(Dashboards.Normalise(new Dashboard(
                        Id: "", Name: legacy.Name ?? "untitled",
                        Widgets: Dashboards.FromTiles(legacy.Tiles ?? []),
                        RefreshSeconds: legacy.RefreshSeconds ?? 0,
                        UpdatedAt: DateTimeOffset.UtcNow)), []);
            }
            catch (JsonException e)
            {
                return new DashboardImport(null, [$"this dashboard could not be read: {e.Message}"]);
            }
        }

        return new DashboardImport(null,
        [
            "this file is not a dashboard this studio recognises: it holds neither `widgets` nor "
            + "`tiles` nor Grafana's `panels`",
        ]);
    }

    /// The shape a `WDS_DASHBOARD_FILE` had before there was a canvas.
    private sealed record LegacyDashboard(string? Name, List<DashboardTile>? Tiles, int? RefreshSeconds);
}
