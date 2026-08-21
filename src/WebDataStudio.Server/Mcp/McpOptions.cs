namespace WebDataStudio.Server.Mcp;

/// Whether the studio also answers as an MCP server, and under what terms.
///
/// Off unless asked for: an endpoint that hands an agent every database in the stack is not
/// something to switch on by accident.
public sealed record McpOptions(
    bool Enabled, string Path, string? Key, bool AllowWrite, IReadOnlySet<string>? Only)
{
    public const string DefaultPath = "/mcp";

    public static McpOptions FromConfiguration(IConfiguration config)
    {
        var enabled = Truthy(config["WDS_MCP_ENABLED"]);
        var path = config["WDS_MCP_PATH"]?.Trim();
        var key = config["WDS_MCP_KEY"]?.Trim();

        // A path on its own means "yes, here" — nobody sets a path for a feature they want off.
        if (!enabled && string.IsNullOrEmpty(path))
            return new McpOptions(false, DefaultPath, null, false, null);

        return new McpOptions(true, Normalise(path), string.IsNullOrEmpty(key) ? null : key,
            Truthy(config["WDS_MCP_ALLOW_WRITE"]), Tools(config["WDS_MCP_TOOLS"]));
    }

    /// The tools this endpoint offers, when the deployment named them. Null means all of them.
    ///
    /// A whitelist rather than a blacklist: a tool added in a later version must not appear on an
    /// endpoint somebody deliberately narrowed.
    private static IReadOnlySet<string>? Tools(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var names = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Select(name => name.ToLowerInvariant());

        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? null : set;
    }

    private static bool Truthy(string? value) =>
        value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                              || value.Equals("1", StringComparison.Ordinal)
                              || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DefaultPath;

        var trimmed = path.Trim().TrimEnd('/');
        if (trimmed.Length == 0) return DefaultPath;

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
