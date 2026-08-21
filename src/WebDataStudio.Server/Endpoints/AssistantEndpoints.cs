using WebDataStudio.Server.Mcp;
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
            RunAsync(assistant, body, ct, Kind.Explain));

        app.MapPost("/api/assist/sql", (AssistDto body, Assistant assistant, CancellationToken ct) =>
            RunAsync(assistant, body, ct, Kind.Draft));

        // The one that may act: the model gets the studio's own tools — the same registry the MCP
        // endpoint exposes, with the same rules — and looks things up instead of guessing.
        app.MapPost("/api/assist/ask", (AssistDto body, Assistant assistant, CancellationToken ct) =>
            RunAsync(assistant, body, ct, Kind.Ask));

        // What the UI needs to decide which buttons exist.
        app.MapGet("/api/assist/capabilities", (Assistant assistant, McpToolbox toolbox) =>
            Results.Ok(new
            {
                configured = assistant.Configured,
                tools = assistant.HasTools,
                toolNames = assistant.HasTools
                    ? toolbox.Tools.Select(tool => tool.Name)
                    : [],
            }));
    }

    private enum Kind { Explain, Draft, Ask }

    private static async Task<IResult> RunAsync(
        Assistant assistant, AssistDto body, CancellationToken ct, Kind kind)
    {
        if (!assistant.Configured)
            return Results.Json(
                new { message = "no assistance is configured; set WDS_ASSIST_ENDPOINT to enable it" },
                statusCode: StatusCodes.Status501NotImplemented);

        var request = new AssistRequest(body.ConnectionId, body.Sql, body.Question,
            body.IncludeSchema ?? false);

        try
        {
            var reply = kind switch
            {
                Kind.Explain => await assistant.ExplainAsync(request, ct),
                Kind.Ask => await assistant.AskAsync(request, ct),
                _ => await assistant.DraftAsync(request, ct),
            };

            // The statements travel as text. Nothing here runs them: a suggestion goes through the
            // same editor and the same preview as anything somebody typed themselves. A tool the
            // model did use is named, so the answer can be checked rather than believed.
            return Results.Ok(new
            {
                text = reply.Text, statements = reply.Statements, usedTools = reply.UsedTools,
            });
        }
        catch (AssistantException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
        catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
    }
}
