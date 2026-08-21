using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Mcp;

namespace WebDataStudio.Server.Services;

public sealed record AssistRequest(
    string ConnectionId, string? Sql, string? Question, bool IncludeSchema);

/// The reply as text, plus any SQL it contained. The statements are handed over as text and never
/// executed — a suggestion is a suggestion.
public sealed record AssistReply(
    string Text, IReadOnlyList<string> Statements, IReadOnlyList<string>? UsedTools = null);

/// Where the optional assistance goes, if anywhere. Without an endpoint and a key the feature does
/// not exist: no calls, no button, nothing in the UI.
public sealed record AssistantOptions(
    bool Configured, string Endpoint, string? Key, string Model, bool Tools)
{
    public static AssistantOptions FromConfiguration(IConfiguration config)
    {
        var endpoint = config["WDS_ASSIST_ENDPOINT"]?.Trim();
        var key = config["WDS_ASSIST_KEY"]?.Trim();
        var model = config["WDS_ASSIST_MODEL"]?.Trim();

        // Tools ride along with the MCP endpoint: the same registry, the same rules. Off if the
        // studio has no MCP endpoint, and off if somebody says so explicitly.
        var tools = !string.Equals(config["WDS_ASSIST_TOOLS"], "false", StringComparison.OrdinalIgnoreCase);

        return string.IsNullOrEmpty(endpoint)
            ? new AssistantOptions(false, "", null, "", false)
            : new AssistantOptions(true, endpoint, string.IsNullOrEmpty(key) ? null : key,
                string.IsNullOrEmpty(model) ? "gpt-4o-mini" : model, tools);
    }
}

public sealed class AssistantException(string message) : Exception(message);

/// Explains a statement, or drafts one from a question. Deliberately thin: one HTTP call to an
/// OpenAI-compatible endpoint, no SDK, and a hard rule that nothing it returns runs on its own.
///
/// What leaves the machine is the statement or the question, and — only when the caller asks for it
/// — a summary of table and column names. Never a row of data.
public sealed partial class Assistant(
    IHttpClientFactory clients, AssistantOptions options, SessionFactory factory,
    McpToolbox toolbox, McpOptions mcp, ConnectionRegistry registry)
{
    /// How many times the model may come back asking for another tool call. Enough to look
    /// something up, read it and answer; not enough to sit in a loop on somebody's bill.
    private const int MaxToolRounds = 6;

    /// Enough of a schema to name things; more than this would be a data transfer.
    private const int MaxTables = 60;
    private const int MaxColumnsPerTable = 40;

    [GeneratedRegex(@"```(?:sql)?\s*(.+?)```", RegexOptions.Singleline)]
    private static partial Regex FencedSql();

    public bool Configured => options.Configured;

    /// True when the assistant may look things up itself, rather than only reading what it was
    /// handed. Tied to the MCP endpoint on purpose: one registry, one set of rules, one thing to
    /// turn off.
    public bool HasTools => options.Configured && options.Tools && mcp.Enabled;

    public Task<AssistReply> ExplainAsync(AssistRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
            throw new AssistantException("there is no statement to explain");

        return AskAsync(request,
            "You explain SQL to an engineer who knows SQL. Say what the statement does, what it " +
            "reads, and what would make it slow or wrong. Be brief and concrete.",
            $"Explain this statement:\n\n{request.Sql}", ct);
    }

    /// A question that may need the database to answer it: the model gets the studio's own tools
    /// and uses them, rather than guessing from a schema summary.
    public Task<AssistReply> AskAsync(AssistRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            throw new AssistantException("there is no question to answer");

        if (!HasTools)
            throw new AssistantException(
                "this studio's assistant has no tools; it can explain and draft SQL, but not look " +
                "anything up. Enable the MCP endpoint to change that.");

        return AskAsync(request,
            "You answer questions about a database you can inspect with tools. Use them: look the " +
            "schema up rather than guessing at names, and read the rows you need. Say what you " +
            "did, then answer. Keep it short. " +
            (mcp.AllowWrite
                ? "You may change data, but only through preview_script and apply_script, in that " +
                  "order, and you say what the script does before you apply it."
                : "You cannot change anything: every tool you have only reads."),
            request.Question!, ct);
    }

    public Task<AssistReply> DraftAsync(AssistRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            throw new AssistantException("there is no question to answer");

        return AskAsync(request,
            "You draft SQL. Answer with one statement in a ```sql block, then one or two sentences " +
            "about what it assumes. Never invent table or column names that were not given to you.",
            request.Question!, ct);
    }

    private async Task<AssistReply> AskAsync(
        AssistRequest request, string system, string user, CancellationToken ct)
    {
        if (!options.Configured)
            throw new AssistantException("no assistance endpoint is configured");

        var prompt = new StringBuilder(user);

        // With tools, the model has to be told which connection it is looking at — otherwise its
        // first tool call guesses an id, fails, and burns a round finding out.
        if (HasTools && registry.Find(request.ConnectionId) is { } spec)
            prompt.Append("\n\nUse connectionId ")
                .Append('"').Append(spec.Id).Append('"')
                .Append($" — that is the {spec.Engine} connection called {spec.Name}")
                .Append(spec.ReadOnly ? ", and it is read-only." : ".");

        if (request.IncludeSchema)
        {
            var schema = await SchemaSummaryAsync(request.ConnectionId, ct);
            if (schema.Length > 0)
                prompt.Append("\n\nThe schema, names only:\n").Append(schema);
        }

        var messages = new List<object>
        {
            new { role = "system", content = system },
            new { role = "user", content = prompt.ToString() },
        };

        var tools = HasTools
            ? toolbox.Tools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.InputSchema,
                },
            }).ToArray()
            : null;

        var used = new List<string>();

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var payload = await SendAsync(messages, tools, ct);

            // A model that wants a tool answers with tool_calls instead of content; every call is
            // run here, against the same registry the MCP endpoint exposes, and fed back.
            var calls = ToolCalls(payload);
            if (calls.Count == 0) return Reply(Answer(payload), used);

            messages.Add(AssistantTurn(payload));

            foreach (var (id, name, arguments) in calls)
            {
                used.Add(name);
                var result = await toolbox.CallAsync(name, Arguments(arguments), ct);

                messages.Add(new
                {
                    role = "tool",
                    tool_call_id = id,
                    content = result.IsError ? $"error: {result.Text}" : result.Text,
                });
            }
        }

        // Out of rounds: say so rather than pretending the last half-answer is the answer.
        throw new AssistantException(
            $"the model asked for tools {MaxToolRounds} times without answering; ask something " +
            "narrower, or turn tools off with WDS_ASSIST_TOOLS=false");
    }

    private AssistReply Reply(string answer, IReadOnlyList<string> used) =>
        new(answer, Statements(answer), used.Count == 0 ? null : used);

    /// One request to the endpoint, returning its raw body.
    private async Task<string> SendAsync(IReadOnlyList<object> messages, object? tools, CancellationToken ct)
    {
        var body = tools is null
            ? new Dictionary<string, object>
            {
                ["model"] = options.Model, ["messages"] = messages, ["temperature"] = 0.2,
            }
            : new Dictionary<string, object>
            {
                ["model"] = options.Model, ["messages"] = messages, ["temperature"] = 0.2,
                ["tools"] = tools, ["tool_choice"] = "auto",
            };

        var client = clients.CreateClient("assist");
        using var message = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = JsonContent.Create(body),
        };

        if (options.Key is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Key);

        using var response = await client.SendAsync(message, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new AssistantException(
                $"the assistance endpoint answered {(int)response.StatusCode}: {Trim(text)}");

        return text;
    }

    /// The tool calls in a reply: (id, name, raw arguments). Empty when the model answered instead.
    private static List<(string Id, string Name, string Arguments)> ToolCalls(string json)
    {
        var calls = new List<(string, string, string)>();

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("tool_calls", out var requested)
                || requested.ValueKind != JsonValueKind.Array)
                return calls;

            foreach (var call in requested.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var function)
                    || function.GetProperty("name").GetString() is not { Length: > 0 } name)
                    continue;

                var id = call.TryGetProperty("id", out var found) ? found.GetString() ?? name : name;
                var arguments = function.TryGetProperty("arguments", out var given)
                    ? given.ValueKind == JsonValueKind.String ? given.GetString() ?? "{}" : given.ToString()
                    : "{}";

                calls.Add((id, name, arguments));
            }
        }
        catch (JsonException)
        {
            // Not the shape we know: treated as an answer, which the caller then reads.
        }

        return calls;
    }

    /// The assistant turn to append verbatim, so the tool results line up with their calls.
    private static object AssistantTurn(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");

            return JsonSerializer.Deserialize<JsonElement>(message.GetRawText());
        }
        catch (Exception)
        {
            return new { role = "assistant", content = "" };
        }
    }

    /// Tool arguments arrive as a JSON string; anything unparseable becomes an empty object, and
    /// the tool then says which field it needed.
    private static JsonElement Arguments(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        }
        catch (JsonException)
        {
            return JsonSerializer.Deserialize<JsonElement>("{}");
        }
    }

    /// The OpenAI-compatible shape, defensively: an endpoint that answers something else should
    /// produce a message, not an exception nobody can act on.
    private static string Answer(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var first)
                && first.TryGetProperty("content", out var content))
                return content.GetString() ?? "";

            // Some endpoints answer {"content": …} or {"text": …}; take either rather than fail.
            foreach (var name in new[] { "content", "text", "message" })
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
        }
        catch (JsonException)
        {
            // Not JSON at all: the raw body is more useful than a parse error.
        }

        return Trim(json);
    }

    private static IReadOnlyList<string> Statements(string answer) =>
        [.. FencedSql().Matches(answer)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(sql => sql.Length > 0)];

    /// Table and column names, nothing else. No rows, no comments, no sizes.
    private async Task<string> SchemaSummaryAsync(string connectionId, CancellationToken ct)
    {
        var summary = new StringBuilder();

        try
        {
            var (driver, session) = await factory.OpenAsync(connectionId, ct);
            await using (session)
            {
                var queue = new Queue<SchemaNodeRef?>();
                queue.Enqueue(null);
                var tables = 0;
                var visited = 0;

                while (queue.Count > 0 && tables < MaxTables && visited++ < 100)
                {
                    var parent = queue.Dequeue();

                    foreach (var node in await driver.IntrospectAsync(session, parent, ct))
                    {
                        if (node.Ref.Kind is SchemaNodeKind.Table or SchemaNodeKind.View)
                        {
                            if (tables++ >= MaxTables) break;

                            var detail = await driver.DescribeAsync(session, node.Ref, ct);
                            var columns = detail.Columns
                                .Take(MaxColumnsPerTable)
                                .Select(c => $"{c.Name} {c.DataType}");

                            summary.Append(node.Ref.Name).Append('(')
                                .Append(string.Join(", ", columns)).Append(")\n");
                            continue;
                        }

                        if (node.HasChildren) queue.Enqueue(node.Ref);
                    }
                }
            }
        }
        catch (Exception)
        {
            // A schema that cannot be read is a prompt without a schema, not a failed request.
        }

        return summary.ToString();
    }

    private static string Trim(string text) =>
        text.Length <= 400 ? text : text[..400] + "…";
}
