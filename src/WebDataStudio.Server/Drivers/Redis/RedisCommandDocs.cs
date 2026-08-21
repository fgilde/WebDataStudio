using StackExchange.Redis;

namespace WebDataStudio.Server.Drivers.Redis;

public sealed record CommandDoc(string Name, int Arity, string Summary, string Group, string Since);

public sealed record ClusterNodeDto(
    string Id, string Endpoint, string Role, string Slots, bool Connected);

public sealed record ClusterDto(bool Enabled, string State, int KnownNodes, IReadOnlyList<ClusterNodeDto> Nodes);

/// What the server says it can do, and how it is put together. Both come straight from Redis rather
/// than from a list baked into the studio: a server with modules has commands no such list knows.
public static class RedisCommandDocs
{
    /// `COMMAND DOCS` for the prose and `COMMAND` for the arity — the two answers carry different
    /// fields, so both are read and merged. An older server has no DOCS, and then there is no
    /// summary, only the name and the arity, which is still what completion needs.
    public static async Task<IReadOnlyList<CommandDoc>> ListAsync(RedisSession session)
    {
        var arities = (await InfoAsync(session))
            .ToDictionary(c => c.Name, c => c.Arity, StringComparer.Ordinal);

        var docs = await TryDocsAsync(session);
        if (docs.Count == 0)
            return [.. arities.Select(entry => new CommandDoc(entry.Key, entry.Value, "", "", ""))
                .OrderBy(c => c.Name, StringComparer.Ordinal)];

        return [.. docs
            .Select(doc => doc.Arity != 0 || !arities.TryGetValue(doc.Name, out var arity)
                ? doc
                : doc with { Arity = arity })
            .OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    private static async Task<IReadOnlyList<CommandDoc>> TryDocsAsync(RedisSession session)
    {
        var commands = new List<CommandDoc>();

        try
        {
            var result = await session.Database.ExecuteAsync("COMMAND", "DOCS");
            if (result.IsNull) return commands;

            var flat = (RedisResult[])result!;

            // A flat name, map, name, map, … sequence.
            for (var i = 0; i + 1 < flat.Length; i += 2)
            {
                var name = flat[i].ToString();
                if (string.IsNullOrEmpty(name)) continue;

                var fields = Fields(flat[i + 1]);
                commands.Add(new CommandDoc(
                    name.ToUpperInvariant(),
                    fields.TryGetValue("arity", out var arity) && int.TryParse(arity, out var value) ? value : 0,
                    fields.GetValueOrDefault("summary", ""),
                    fields.GetValueOrDefault("group", ""),
                    fields.GetValueOrDefault("since", "")));
            }
        }
        catch (RedisServerException)
        {
            // Older server: the caller falls back to COMMAND INFO.
        }

        return commands;
    }

    private static async Task<IReadOnlyList<CommandDoc>> InfoAsync(RedisSession session)
    {
        var commands = new List<CommandDoc>();

        try
        {
            var result = await session.Database.ExecuteAsync("COMMAND");
            if (result.IsNull) return commands;

            foreach (var entry in (RedisResult[])result!)
            {
                var parts = (RedisResult[])entry!;
                if (parts.Length < 2) continue;

                var name = parts[0].ToString();
                if (string.IsNullOrEmpty(name)) continue;

                commands.Add(new CommandDoc(
                    name.ToUpperInvariant(),
                    int.TryParse(parts[1].ToString(), out var arity) ? arity : 0,
                    "", "", ""));
            }
        }
        catch (RedisServerException)
        {
            // A server that answers neither is a server with no help to give.
        }

        return [.. commands.OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// The string fields of a `COMMAND DOCS` map, ignoring the nested ones (arguments, replies):
    /// completion needs the summary and the group, not the whole grammar.
    private static Dictionary<string, string> Fields(RedisResult map)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var flat = (RedisResult[])map!;
            for (var i = 0; i + 1 < flat.Length; i += 2)
            {
                var key = flat[i].ToString();
                if (string.IsNullOrEmpty(key) || flat[i + 1].Resp2Type == ResultType.Array) continue;
                fields[key] = flat[i + 1].ToString() ?? "";
            }
        }
        catch (InvalidCastException)
        {
            // Not a map after all; no fields, no crash.
        }

        return fields;
    }

    /// The cluster, or the honest answer that there is none: a standalone server reports itself as
    /// one node rather than failing, so the view works everywhere.
    public static async Task<ClusterDto> DescribeAsync(RedisSession session)
    {
        var endpoint = session.Multiplexer.GetEndPoints()[0].ToString() ?? "unknown";

        string info;
        try
        {
            info = (await session.Database.ExecuteAsync("CLUSTER", "INFO")).ToString() ?? "";
        }
        catch (RedisServerException)
        {
            info = "";
        }

        var enabled = Line(info, "cluster_enabled") == "1";

        if (!enabled)
            return new ClusterDto(false, Line(info, "cluster_state") is { Length: > 0 } state ? state : "standalone",
                1, [new ClusterNodeDto("-", endpoint, "master", "all", true)]);

        var nodes = new List<ClusterNodeDto>();

        try
        {
            var text = (await session.Database.ExecuteAsync("CLUSTER", "NODES")).ToString() ?? "";

            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // <id> <ip:port@cport> <flags> <master> … <link-state> <slot> <slot> …
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 8) continue;

                var flags = parts[2];
                nodes.Add(new ClusterNodeDto(
                    parts[0],
                    parts[1].Split('@')[0],
                    flags.Contains("master") ? "master" : "replica",
                    parts.Length > 8 ? string.Join(" ", parts[8..]) : "",
                    parts[7] == "connected"));
            }
        }
        catch (RedisServerException)
        {
            // Enabled but unreadable: report what CLUSTER INFO said and no nodes.
        }

        return new ClusterDto(true, Line(info, "cluster_state"),
            int.TryParse(Line(info, "cluster_known_nodes"), out var known) ? known : nodes.Count,
            nodes);
    }

    private static string Line(string info, string key)
    {
        foreach (var line in info.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                return trimmed[(key.Length + 1)..].Trim();
        }

        return "";
    }
}
