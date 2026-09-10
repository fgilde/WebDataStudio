using System.Text.Json;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// Dashboards: a canvas of widgets somebody wants on a screen rather than in a tab.
///
/// Nothing here executes anything. A widget's statement runs through the query endpoint a tab runs
/// through, or through the studio's own federation when it spans connections — so the row cap,
/// masking, read-only connections and the audit line are the same ones, not a second set.
public static class DashboardEndpoints
{
    /// What the browser sends. The old shape is still accepted: `tiles` instead of `widgets` is what
    /// every dashboard file and app host written before the canvas looks like, and one of those is
    /// not a reason to lose a page.
    public record DashboardRequest(
        string Name,
        List<Widget>? Widgets,
        int? RefreshSeconds,
        DashboardLayout? Layout,
        DashboardTimeRange? TimeRange,
        List<DashboardVariable>? Variables,
        List<string>? Tags,
        List<DashboardTile>? Tiles);

    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/dashboards");
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WebDataStudio");

        api.MapGet("/", (WorkspaceStore store, IConfiguration config, ConnectionRegistry connections) =>
            Results.Ok(new
            {
                available = store.Available,
                // What the deployment ships first: those are the ones somebody is meant to look at.
                dashboards = Shipped(config, connections, log).Concat(store.ListDashboards()),
            }));

        api.MapPost("/", (DashboardRequest body, WorkspaceStore store) => Save(store, "", body));

        api.MapPut("/{id}", (string id, DashboardRequest body, WorkspaceStore store,
            IConfiguration config, ConnectionRegistry connections) =>
        {
            if (Shipped(config, connections, log).Any(one => one.Id == id)) return Owned();

            return store.ListDashboards().All(one => one.Id != id)
                ? Results.NotFound()
                : Save(store, id, body);
        });

        api.MapDelete("/{id}", (string id, WorkspaceStore store, IConfiguration config,
            ConnectionRegistry connections) =>
        {
            if (Shipped(config, connections, log).Any(one => one.Id == id)) return Owned();

            store.DeleteDashboard(id);
            return Results.NoContent();
        });

        // A dashboard from somewhere else, in whatever shape it arrived in. Nothing asks which:
        // pasting a Grafana JSON and pasting one of ours are the same gesture, so they are the same
        // endpoint. What could not come along is answered as sentences rather than swallowed.
        api.MapPost("/import", async (HttpContext ctx, ConnectionRegistry connections) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var text = await reader.ReadToEndAsync(ctx.RequestAborted);

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            }
            catch (JsonException e)
            {
                return Results.BadRequest(new { message = $"this is not JSON: {e.Message}" });
            }

            using (document)
            {
                var read = DashboardFormats.Read(document.RootElement, Names(connections));

                return read.Dashboard is null
                    ? Results.BadRequest(new { message = read.Notes.FirstOrDefault() ?? "unreadable" })
                    // Not saved yet: an import somebody has not looked at is not a dashboard they
                    // asked to keep.
                    : Results.Ok(new { dashboard = read.Dashboard, notes = read.Notes });
            }
        });

        // The way out. A dashboard built here opens in Grafana, and one that came from there goes
        // back with the fields this studio never read still in it.
        api.MapGet("/{id}/grafana", (string id, WorkspaceStore store, IConfiguration config,
            ConnectionRegistry connections) =>
        {
            var dashboard = Shipped(config, connections, log).Concat(store.ListDashboards())
                .FirstOrDefault(one => one.Id == id);

            return dashboard is null
                ? Results.NotFound()
                : Results.Text(GrafanaDashboards.Write(dashboard, Names(connections)).ToJsonString(),
                    "application/json");
        });
    }

    private static IReadOnlyList<ConnectionSpecName> Names(ConnectionRegistry connections) =>
        connections.All().Select(one => new ConnectionSpecName(one.Id, one.Name)).ToList();

    /// The dashboards a deployment ships, from `WDS_DASHBOARD_FILE` — a file, several files or a
    /// folder, in any of the three shapes. Their ids are their names, so the same file read twice is
    /// the same dashboard rather than a second copy.
    private static IReadOnlyList<Dashboard> Shipped(IConfiguration config,
        ConnectionRegistry connections, ILogger log)
    {
        var shipped = new List<Dashboard>();
        var names = Names(connections);

        foreach (var file in ConfiguredPaths.Files(config["WDS_DASHBOARD_FILE"], "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                var text = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(text)) continue;

                using var document = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                var (dashboards, notes) = DashboardFormats.All(document.RootElement, names);

                foreach (var note in notes)
                    log.LogInformation("dashboard file {File}: {Note}", file, note);

                shipped.AddRange(dashboards
                    .Where(one => !string.IsNullOrWhiteSpace(one.Name))
                    .Select(one => one with
                    {
                        Id = $"shipped:{one.Name.Trim()}",
                        UpdatedAt = DateTimeOffset.MinValue,
                        FromFile = true,
                    }));
            }
            catch (Exception e)
            {
                // One bad file must not take the others with it, and must never stop the studio.
                log.LogWarning(e, "could not read the dashboard file {File}", file);
            }
        }

        return shipped;
    }

    private static IResult Owned() => Results.BadRequest(new
    {
        message = "this dashboard comes with the deployment; save a copy under another name to "
                  + "change it",
    });

    private static IResult Save(WorkspaceStore store, string id, DashboardRequest body)
    {
        if (!store.Available)
            return Results.BadRequest(new
            {
                message = "this studio has no workspace file, so it cannot keep a dashboard",
            });

        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { message = "a dashboard needs a name" });

        // A page written before there was a canvas arrives as tiles; it lays itself out and is a
        // document from then on.
        var widgets = body.Widgets is { Count: > 0 } given
            ? given
            : Dashboards.FromTiles(body.Tiles ?? []).ToList();

        try
        {
            return Results.Ok(store.SaveDashboard(new Dashboard(
                id, body.Name.Trim(), widgets, body.RefreshSeconds ?? 0, DateTimeOffset.UtcNow,
                body.Layout, body.TimeRange, body.Variables, body.Tags)));
        }
        catch (InvalidOperationException e)
        {
            return Results.BadRequest(new { message = e.Message });
        }
    }
}
