namespace WebDataStudio.Server.Mcp;

/// Whether the studio also answers as an MCP server, and under what terms.
///
/// Off unless asked for: an endpoint that hands an agent every database in the stack is not
/// something to switch on by accident.
public sealed record McpOptions(bool Enabled, string Path, string? Key, bool AllowWrite)
{
    public const string DefaultPath = "/mcp";

    public static McpOptions FromConfiguration(IConfiguration config)
    {
        var enabled = Truthy(config["WDS_MCP_ENABLED"]);
        var path = config["WDS_MCP_PATH"]?.Trim();
        var key = config["WDS_MCP_KEY"]?.Trim();

        // A path on its own means "yes, here" — nobody sets a path for a feature they want off.
        if (!enabled && string.IsNullOrEmpty(path)) return new McpOptions(false, DefaultPath, null, false);

        return new McpOptions(true, Normalise(path), string.IsNullOrEmpty(key) ? null : key,
            Truthy(config["WDS_MCP_ALLOW_WRITE"]));
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
