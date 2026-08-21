using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class AssistantEndpoints
{
    public record AssistDto(string ConnectionId, string? Sql, string? Question, bool? IncludeSchema);

    public static void MapAssistantEndpoints(this WebApplication app)
    {
        // 501 rather than 404: the route exists, the feature is simply not configured, and the
        // difference is what tells somebody they have the deployment they think they have.
        app.MapPost("/api/assist/explain", (AssistDto body, Assistant assistant, CancellationToken ct) =>
            RunAsync(assistant, body, ct, explain: true));

        app.MapPost("/api/assist/sql", (AssistDto body, Assistant assistant, CancellationToken ct) =>
            RunAsync(assistant, body, ct, explain: false));
    }

    private static async Task<IResult> RunAsync(
        Assistant assistant, AssistDto body, CancellationToken ct, bool explain)
    {
        if (!assistant.Configured)
            return Results.Json(
                new { message = "no assistance is configured; set WDS_ASSIST_ENDPOINT to enable it" },
                statusCode: StatusCodes.Status501NotImplemented);

        var request = new AssistRequest(body.ConnectionId, body.Sql, body.Question,
            body.IncludeSchema ?? false);

        try
        {
            var reply = explain
                ? await assistant.ExplainAsync(request, ct)
                : await assistant.DraftAsync(request, ct);

            // The statements travel as text. Nothing here runs them: a suggestion goes through the
            // same editor and the same preview as anything somebody typed themselves.
            return Results.Ok(new { text = reply.Text, statements = reply.Statements });
        }
        catch (AssistantException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
        catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
    }
}
