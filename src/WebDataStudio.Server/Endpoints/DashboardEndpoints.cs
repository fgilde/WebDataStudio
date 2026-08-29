using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// Dashboards: a page of statements somebody wants on a screen rather than in a tab.
///
/// The studio already had every piece — saved queries, charts, a watch interval — and no place that
/// put them next to each other. A dashboard is that place, and nothing more: the tiles run through
/// the same query endpoint everything else runs through, with the same row cap, the same masking
/// and the same audit line.
public static class DashboardEndpoints
{
    public record TileRequest(string Title, string ConnectionId, string Sql, string? View, int? Width);
    public record DashboardRequest(string Name, List<TileRequest> Tiles, int? RefreshSeconds);

    /// What a tile may draw. Anything else is a typo, and a typo should not become a blank box.
    private static readonly string[] Views = ["number", "table", "chart"];

    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/dashboards");

        api.MapGet("/", (WorkspaceStore store) => Results.Ok(new
        {
            available = store.Available,
            dashboards = store.ListDashboards(),
        }));

        api.MapPost("/", (DashboardRequest body, WorkspaceStore store) => Save(store, "", body));

        api.MapPut("/{id}", (string id, DashboardRequest body, WorkspaceStore store) =>
            store.ListDashboards().All(one => one.Id != id)
                ? Results.NotFound()
                : Save(store, id, body));

        api.MapDelete("/{id}", (string id, WorkspaceStore store) =>
        {
            store.DeleteDashboard(id);
            return Results.NoContent();
        });
    }

    private static IResult Save(WorkspaceStore store, string id, DashboardRequest body)
    {
        if (!store.Available)
            return Results.BadRequest(new
            {
                message = "this studio has no workspace file, so it cannot keep a dashboard",
            });

        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { message = "a dashboard needs a name" });

        var tiles = (body.Tiles ?? [])
            .Where(tile => !string.IsNullOrWhiteSpace(tile.Sql))
            .Select(tile => new DashboardTile(
                string.IsNullOrWhiteSpace(tile.Title) ? "untitled" : tile.Title.Trim(),
                tile.ConnectionId ?? "",
                tile.Sql.Trim(),
                Views.Contains(tile.View) ? tile.View! : "table",
                Math.Clamp(tile.Width ?? 1, 1, 4)))
            .Take(24)
            .ToList();

        // A refresh under ten seconds is a load test, not a dashboard.
        var refresh = body.RefreshSeconds is { } seconds && seconds > 0
            ? Math.Clamp(seconds, 10, 3600)
            : 0;

        return Results.Ok(store.SaveDashboard(
            new Dashboard(id, body.Name.Trim(), tiles, refresh, DateTimeOffset.UtcNow)));
    }
}
