using System.Security.Claims;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// A result kept as rows, and a link to it. The answer to "here is what I am seeing" that is not a
/// screenshot — and not a saved query either: a link shows what was there, and cannot run anything.
public static class ShareEndpoints
{
    public record ShareRequest(string ConnectionId, string Sql);

    public static void MapShareEndpoints(this WebApplication app)
    {
        app.MapPost("/api/share", async (ShareRequest body, ResultShares shares, HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!shares.Enabled)
                return Results.Json(
                    new { message = "sharing is off; set WDS_SHARE_ENABLED=true to allow it" },
                    statusCode: StatusCodes.Status501NotImplemented);

            try
            {
                var by = ctx.User.FindFirst(ClaimTypes.Name)?.Value;
                var shared = await shares.CreateAsync(body.ConnectionId, body.Sql, by, ct);

                return Results.Ok(new
                {
                    shared.Id,
                    url = $"/share/{shared.Id}",
                    shared.ExpiresAt,
                    rows = shared.Rows.Count,
                    shared.Truncated,
                    isPublic = shares.Public,
                });
            }
            catch (InvalidOperationException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // The snapshot itself. Open to anybody with the link when WDS_SHARE_PUBLIC=true — that is
        // what a link is for — and behind the login otherwise.
        app.MapGet("/api/share/{id}", (string id, ResultShares shares) =>
        {
            var shared = shares.Find(id);

            // Expired and never-existed are the same answer: a link that used to work says nothing
            // about what it used to show.
            return shared is null
                ? Results.NotFound(new { message = "that link is not valid any more" })
                : Results.Ok(new
                {
                    shared.Id,
                    shared.ConnectionName,
                    shared.Sql,
                    shared.By,
                    shared.At,
                    shared.ExpiresAt,
                    shared.Columns,
                    shared.Rows,
                    shared.Truncated,
                });
        }).AllowAnonymous();

        app.MapGet("/api/share", (ResultShares shares) => Results.Ok(new
        {
            enabled = shares.Enabled,
            isPublic = shares.Public,
        })).AllowAnonymous();
    }
}
