using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Mcp;

/// Whether the MCP endpoint is actually served, and why not when it is not.
///
/// One place decides this, because two places deciding it is how a studio ends up advertising an
/// endpoint it refuses to serve — which is exactly what the header dialog then tries to read.
public sealed class McpAvailability(McpOptions options, UserStore users)
{
    public bool Configured => options.Enabled;

    /// Whether an agent may change data — through a preview and its hash, never in one step.
    public bool AllowWrite => Enabled && options.AllowWrite;

    /// A studio with accounts needs a key of its own: the MCP endpoint sits outside the login
    /// screen — an agent has no cookie — so without one it would be a way past it.
    public bool Enabled => options.Enabled && (users.Anonymous || options.Key is not null);

    public string? Reason => options.Enabled && !Enabled
        ? "This studio has accounts, so its MCP endpoint needs a key of its own: set WDS_MCP_KEY. "
          + "Without one an agent would reach every database without signing in, so the endpoint "
          + "is not served."
        : null;

    /// What `/api/health` reports. Null when nobody asked for MCP at all.
    public object? Describe() => options.Enabled
        ? new
        {
            path = options.Path,
            writes = options.AllowWrite,
            needsKey = options.Key is not null,
            enabled = Enabled,
            reason = Reason,
        }
        : null;
}
