using Microsoft.AspNetCore.Mvc;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class WorkspaceEndpoints
{
    public record NoteRequest([property: System.Text.Json.Serialization.JsonPropertyName("ref")] string ObjectRef, string Body);

    public record HistoryRequest(string ConnectionId, string Sql, long? ElapsedMs, long? RowCount,
        string? Error, string? Snapshot);

    /// A snapshot is a convenience, not an archive. Past this it is refused rather than quietly
    /// truncated, because a truncated result that looks whole is worse than none.
    private const int MaxSnapshotChars = 1_000_000;

    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/history", (string? connectionId, string? search, int? limit, WorkspaceStore store) =>
            Results.Ok(store.ListHistory(connectionId, search, Math.Clamp(limit ?? 200, 1, 2000))));

        // What this connection actually spends its time on, and what changed. The history holds
        // every run with its elapsed time and nobody reads it as a whole, because two thousand
        // statements answer no question; grouped by fingerprint they answer two.
        app.MapGet("/api/history/stats", (string? connectionId, int? days, int? top,
            WorkspaceStore store) =>
        {
            var window = Math.Clamp(days ?? 30, 1, 365);
            var since = DateTimeOffset.UtcNow.AddDays(-window);

            // The window is applied here rather than in SQL: the history is bounded and the store's
            // list is the one place that reads it.
            var entries = store.ListHistory(connectionId, null, 5000)
                .Where(entry => entry.ExecutedAt >= since)
                .ToList();

            return Results.Ok(new
            {
                days = window,
                runs = entries.Count,
                statements = QueryStats.Report(entries, top ?? 50),
            });
        });

        // What people know about an object and nowhere to put it. The database has COMMENT ON, which
        // needs a DDL right and a migration; this is the studio's own note, with a name and a date.
        app.MapGet("/api/notes/{conn}", (string conn, [FromQuery(Name = "ref")] string? objectRef,
            int? limit, WorkspaceStore store) =>
            Results.Ok(store.ListNotes(conn, objectRef, limit ?? 100)));

        app.MapGet("/api/notes", (string? search, int? limit, WorkspaceStore store) =>
            Results.Ok(string.IsNullOrWhiteSpace(search)
                ? Array.Empty<ObjectNote>()
                : store.SearchNotes(search, limit ?? 100)));

        app.MapPost("/api/notes/{conn}", (string conn, NoteRequest body, HttpContext ctx,
            CurrentUser current, WorkspaceStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.ObjectRef))
                return Results.BadRequest(new { message = "a note belongs to an object" });

            if (string.IsNullOrWhiteSpace(body.Body))
                return Results.BadRequest(new { message = "an empty note is not a note" });

            // Whoever is signed in, or the machine somebody is sitting at. A note with no name on it
            // is worth less than half of one.
            var author = current.User?.Name ?? "anonymous";

            Audit.Detail(ctx, $"note on {body.ObjectRef}", conn);

            return Results.Ok(store.AddNote(conn, body.ObjectRef.Trim(), author, body.Body.Trim()));
        });

        app.MapDelete("/api/notes/{conn}/{id:long}", (string conn, long id, WorkspaceStore store) =>
            store.DeleteNote(id) ? Results.NoContent() : Results.NotFound());

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
