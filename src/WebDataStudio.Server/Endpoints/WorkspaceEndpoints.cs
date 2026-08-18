using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class WorkspaceEndpoints
{
    public record HistoryRequest(string ConnectionId, string Sql, long? ElapsedMs, long? RowCount, string? Error);

    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/history", (string? connectionId, string? search, int? limit, WorkspaceStore store) =>
            Results.Ok(store.ListHistory(connectionId, search, Math.Clamp(limit ?? 200, 1, 2000))));

        app.MapPost("/api/history", (HistoryRequest body, WorkspaceStore store) =>
        {
            store.AddHistory(body.ConnectionId, body.Sql, body.ElapsedMs, body.RowCount, body.Error);
            return Results.NoContent();
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
