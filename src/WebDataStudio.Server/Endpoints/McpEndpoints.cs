using System.Text.Json;
using WebDataStudio.Server.Mcp;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// The studio as an MCP server: the same databases, the same rules, offered to an agent instead of
/// to a person. JSON-RPC 2.0 over one HTTP endpoint — the transport every MCP client speaks.
///
/// Off unless configured. When it is off the route does not exist at all, so a scan finds nothing
/// to talk to.
public static class McpEndpoints
{
    private const string ProtocolVersion = "2025-06-18";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapMcpEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<McpOptions>();
        if (!options.Enabled) return;

        // A studio with a login must not have an unguarded back door: the MCP endpoint sits outside
        // the login (an agent has no cookie), so it needs a key of its own or it does not open.
        var users = app.Services.GetRequiredService<UserStore>();
        if (!users.Anonymous && options.Key is null)
        {
            app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("WebDataStudio.Mcp")
                .LogError("This studio has accounts, so the MCP endpoint needs WDS_MCP_KEY. " +
                          "Without it an agent would reach every database without signing in, so " +
                          "the endpoint stays off.");
            return;
        }

        // A GET says what this is, for a human who pasted the URL into a browser.
        app.MapGet(options.Path, (McpOptions current, McpToolbox toolbox) => Results.Ok(new
        {
            name = "webdatastudio",
            protocolVersion = ProtocolVersion,
            transport = "http",
            writes = current.AllowWrite,
            authentication = current.Key is null ? "none" : "bearer",
            tools = toolbox.Tools.Select(tool => new { tool.Name, tool.Description, tool.Writes }),
        })).AllowAnonymous();

        app.MapPost(options.Path, async (HttpContext ctx, McpOptions current, McpToolbox toolbox) =>
        {
            if (!Authorised(ctx, current))
                return Results.Json(new { message = "the MCP key is missing or wrong" },
                    statusCode: StatusCodes.Status401Unauthorized);

            JsonElement request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<JsonElement>(
                    ctx.Request.Body, Json, ctx.RequestAborted);
            }
            catch (JsonException e)
            {
                return Rpc(null, error: (-32700, $"the body is not JSON: {e.Message}"));
            }

            // A batch is a JSON array of calls; the spec allows it and some clients use it.
            if (request.ValueKind == JsonValueKind.Array)
            {
                var replies = new List<object>();
                foreach (var single in request.EnumerateArray())
                {
                    var reply = await HandleAsync(single, toolbox, current, ctx.RequestAborted);
                    if (reply is not null) replies.Add(reply);
                }

                return replies.Count == 0 ? Results.NoContent() : Results.Ok(replies);
            }

            var response = await HandleAsync(request, toolbox, current, ctx.RequestAborted);

            // A notification gets no answer, which is what 202 says without inventing a body.
            return response is null ? Results.Accepted() : Results.Ok(response);
        }).AllowAnonymous();
    }

    /// One JSON-RPC call. Returns null for a notification, which by definition has no reply.
    private static async Task<object?> HandleAsync(
        JsonElement request, McpToolbox toolbox, McpOptions options, CancellationToken ct)
    {
        var method = request.TryGetProperty("method", out var m) ? m.GetString() : null;
        var id = request.TryGetProperty("id", out var found) && found.ValueKind != JsonValueKind.Null
            ? found
            : (JsonElement?)null;

        if (method is null) return Rpc(id, error: (-32600, "no method"));

        switch (method)
        {
            case "initialize":
                return Rpc(id, result: new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new
                    {
                        name = "webdatastudio",
                        version = typeof(McpEndpoints).Assembly.GetName().Version?.ToString() ?? "1.0",
                    },
                    instructions =
                        "The databases of a WebDataStudio instance. Start with list_connections, "
                        + "then list_objects and describe_object to find your way around, then "
                        + "run_query to read. Masked columns come back as dots on purpose. "
                        + (options.AllowWrite
                            ? "Writing goes through preview_script and apply_script, in that order."
                            : "This endpoint is read-only."),
                });

            // Notifications: acknowledged by answering nothing at all.
            case "notifications/initialized":
            case "notifications/cancelled":
                return null;

            case "ping":
                return Rpc(id, result: new { });

            case "tools/list":
                return Rpc(id, result: new
                {
                    tools = toolbox.Tools.Select(tool => new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        inputSchema = tool.InputSchema,
                    }),
                });

            case "tools/call":
            {
                if (!request.TryGetProperty("params", out var parameters)
                    || !parameters.TryGetProperty("name", out var name)
                    || name.GetString() is not { Length: > 0 } toolName)
                    return Rpc(id, error: (-32602, "params.name is required"));

                var arguments = parameters.TryGetProperty("arguments", out var given)
                    ? given
                    : default;

                var result = await toolbox.CallAsync(toolName, arguments, ct);

                // A tool that failed is a result with isError, not a protocol error: the agent is
                // supposed to read it and try something else.
                return Rpc(id, result: new
                {
                    content = new[] { new { type = "text", text = result.Text } },
                    isError = result.IsError,
                });
            }

            // Nothing here has resources or prompts, and saying so beats a protocol error.
            case "resources/list":
                return Rpc(id, result: new { resources = Array.Empty<object>() });
            case "prompts/list":
                return Rpc(id, result: new { prompts = Array.Empty<object>() });

            default:
                return Rpc(id, error: (-32601, $"'{method}' is not a method this server has"));
        }
    }

    private static bool Authorised(HttpContext ctx, McpOptions options)
    {
        if (options.Key is null) return true;

        var header = ctx.Request.Headers.Authorization.ToString();
        var bearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

        // Some clients only do a plain header, so both are accepted.
        var plain = ctx.Request.Headers["X-API-Key"].ToString();

        return Fixed(bearer, options.Key) || Fixed(plain, options.Key);
    }

    private static bool Fixed(string? given, string expected) =>
        given is { Length: > 0 } && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(expected));

    private static object Rpc(JsonElement? id, object? result = null, (int Code, string Message)? error = null)
    {
        if (error is { } failure)
            return new
            {
                jsonrpc = "2.0",
                id = id as object,
                error = new { code = failure.Code, message = failure.Message },
            };

        return new { jsonrpc = "2.0", id = id as object, result };
    }
}
