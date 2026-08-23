using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class WorkspaceEndpoints
{
    public record HistoryRequest(string ConnectionId, string Sql, long? ElapsedMs, long? RowCount,
        string? Error, string? Snapshot);

    /// A snapshot is a convenience, not an archive. Past this it is refused rather than quietly
    /// truncated, because a truncated result that looks whole is worse than none.
    private const int MaxSnapshotChars = 1_000_000;

    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/history", (string? connectionId, string? search, int? limit, WorkspaceStore store) =>
            Results.Ok(store.ListHistory(connectionId, search, Math.Clamp(limit ?? 200, 1, 2000))));

        app.MapPost("/api/history", (HistoryRequest body, WorkspaceStore store) =>
        {
            if (body.Snapshot is { Length: > MaxSnapshotChars })
                return Results.BadRequest(new
                {
                    message = $"the snapshot is larger than {MaxSnapshotChars / 1000} kB",
                });

            store.AddHistory(body.ConnectionId, body.Sql, body.ElapsedMs, body.RowCount, body.Error,
                body.Snapshot);
            return Results.NoContent();
        });

        app.MapGet("/api/history/{id:long}/snapshot", (long id, WorkspaceStore store) =>
        {
            var snapshot = store.LoadSnapshot(id);
            return snapshot is null
                ? Results.NotFound(new { message = "that entry has no kept result" })
                : Results.Content(snapshot, "application/json");
        });

        app.MapGet("/api/workspace/tabs", (WorkspaceStore store) =>
            Results.Content(store.LoadTabs(), "application/json"));

        app.MapPut("/api/workspace/tabs", async (HttpContext ctx, WorkspaceStore store) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            store.SaveTabs(await reader.ReadToEndAsync());
            return Results.NoContent();
        });

        app.MapGet("/api/workspace/item/{key}", (string key, WorkspaceStore store) =>
            Results.Content(store.LoadItem(key) ?? "null", "application/json"));

        app.MapPut("/api/workspace/item/{key}", async (string key, HttpContext ctx, WorkspaceStore store) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            store.SaveItem(key, await reader.ReadToEndAsync());
            return Results.NoContent();
        });
    }
}
