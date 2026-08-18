using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class SavedQueryEndpoints
{
    public record SavedQueryRequest(string Name, string? Folder, string Sql, string? ConnectionId);

    public static void MapSavedQueryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/saved-queries");

        api.MapGet("/", (WorkspaceStore store) => Results.Ok(store.ListSavedQueries()));

        api.MapPost("/", (SavedQueryRequest body, WorkspaceStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { message = "a saved query needs a name" });

            return Results.Ok(store.SaveQuery(new SavedQuery(
                "", body.Name.Trim(), body.Folder, body.Sql, body.ConnectionId, DateTimeOffset.UtcNow)));
        });

        api.MapPut("/{id}", (string id, SavedQueryRequest body, WorkspaceStore store) =>
        {
            if (store.ListSavedQueries().All(q => q.Id != id)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { message = "a saved query needs a name" });

            return Results.Ok(store.SaveQuery(new SavedQuery(
                id, body.Name.Trim(), body.Folder, body.Sql, body.ConnectionId, DateTimeOffset.UtcNow)));
        });

        api.MapDelete("/{id}", (string id, WorkspaceStore store) =>
            store.DeleteSavedQuery(id) ? Results.NoContent() : Results.NotFound());
    }
}
