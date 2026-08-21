using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

public sealed record AssistRequest(
    string ConnectionId, string? Sql, string? Question, bool IncludeSchema);

/// The reply as text, plus any SQL it contained. The statements are handed over as text and never
/// executed — a suggestion is a suggestion.
public sealed record AssistReply(string Text, IReadOnlyList<string> Statements);

/// Where the optional assistance goes, if anywhere. Without an endpoint and a key the feature does
/// not exist: no calls, no button, nothing in the UI.
public sealed record AssistantOptions(bool Configured, string Endpoint, string? Key, string Model)
{
    public static AssistantOptions FromConfiguration(IConfiguration config)
    {
        var endpoint = config["WDS_ASSIST_ENDPOINT"]?.Trim();
        var key = config["WDS_ASSIST_KEY"]?.Trim();
        var model = config["WDS_ASSIST_MODEL"]?.Trim();

        return string.IsNullOrEmpty(endpoint)
            ? new AssistantOptions(false, "", null, "")
            : new AssistantOptions(true, endpoint, string.IsNullOrEmpty(key) ? null : key,
                string.IsNullOrEmpty(model) ? "gpt-4o-mini" : model);
    }
}

public sealed class AssistantException(string message) : Exception(message);

/// Explains a statement, or drafts one from a question. Deliberately thin: one HTTP call to an
/// OpenAI-compatible endpoint, no SDK, and a hard rule that nothing it returns runs on its own.
///
/// What leaves the machine is the statement or the question, and — only when the caller asks for it
/// — a summary of table and column names. Never a row of data.
public sealed partial class Assistant(
    IHttpClientFactory clients, AssistantOptions options, SessionFactory factory)
{
    /// Enough of a schema to name things; more than this would be a data transfer.
    private const int MaxTables = 60;
    private const int MaxColumnsPerTable = 40;

    [GeneratedRegex(@"```(?:sql)?\s*(.+?)```", RegexOptions.Singleline)]
    private static partial Regex FencedSql();

    public bool Configured => options.Configured;

    public Task<AssistReply> ExplainAsync(AssistRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
            throw new AssistantException("there is no statement to explain");

        return AskAsync(request,
            "You explain SQL to an engineer who knows SQL. Say what the statement does, what it " +
            "reads, and what would make it slow or wrong. Be brief and concrete.",
            $"Explain this statement:\n\n{request.Sql}", ct);
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

        if (request.IncludeSchema)
        {
            var schema = await SchemaSummaryAsync(request.ConnectionId, ct);
            if (schema.Length > 0)
                prompt.Append("\n\nThe schema, names only:\n").Append(schema);
        }

        var body = new
        {
            model = options.Model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = prompt.ToString() },
            },
            temperature = 0.2,
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

        var answer = Answer(text);
        return new AssistReply(answer, Statements(answer));
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
