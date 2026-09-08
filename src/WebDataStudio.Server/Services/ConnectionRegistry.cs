using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// The single place that answers "what connections exist": environment definitions first
/// (read-only, re-read on every start), then everything the user stored in the UI.
public sealed class ConnectionRegistry
{
    // Engines the UI may offer. Drivers arrive in P1/P7; the list gates typos, not features.
    public static readonly string[] KnownEngines =
    [
        "postgresql", "mysql", "sqlserver", "sqlite",
        "oracle", "duckdb", "clickhouse", "mongodb", "redis",
        // Object storage — S3, Azure Blob, Google Cloud Storage or a folder, told apart by the
        // connection's own URL rather than by a separate engine each.
        "storage",
        "odata",
    ];

    private readonly IReadOnlyList<ConnectionSpec> _environment;
    private readonly ConnectionStore _store;
    private readonly bool _forceReadOnly;
    // Absent in the few places that construct a registry outside the container.
    private readonly CurrentUser? _current;
    private readonly SessionConnections? _sessions;
    private readonly ConnectHosts? _hosts;
    private readonly ILogger<ConnectionRegistry>? _log;
    // Said once per connection rather than on every list: a studio asks for its connections often.
    private readonly HashSet<string> _reported = [];

    public ConnectionRegistry(IConfiguration config, ConnectionStore store,
        CurrentUser? current = null, SessionConnections? sessions = null,
        ConnectHosts? hosts = null, ILogger<ConnectionRegistry>? log = null)
    {
        _store = store;
        _current = current;
        _sessions = sessions;
        _hosts = hosts;
        _log = log;
        _environment = EnvironmentConnections.Parse(
            config.AsEnumerable().ToDictionary(kv => kv.Key, kv => kv.Value),
            // A connection somebody wrote down and did not get is worth a line on the way up.
            why => log?.LogWarning("connection from the environment not offered: {Why}", why));
        _forceReadOnly = string.Equals(config["WDS_READONLY"], "true", StringComparison.OrdinalIgnoreCase);
    }

    /// Everything this request may see. Filtering here rather than in each endpoint is the point:
    /// every path that opens a session goes through `Find`, so a connection somebody may not see
    /// does not exist for them — not in the list, not by guessing its id.
    public IReadOnlyList<ConnectionSpec> All()
    {
        var user = _current?.User;

        // Three sources: what the deployment wrote down, what somebody stored, and what this
        // browser opened from a link. The last one is nobody else's.
        return _environment.Concat(_store.List()).Concat(_sessions?.Current ?? [])
            .Where(c => user is null || user.MaySee(c.Id, c.Name))
            // A host WDS_CONNECT_HOSTS does not name is not a connection this studio has: a
            // connection written down before the list was set must not become the way around it.
            .Where(Reachable)
            .Select(c => _forceReadOnly || user?.ReadOnly == true ? c with { ReadOnly = true } : c)
            .ToList();
    }

    /// Whether this studio may connect to the target at all. Refusals are logged once each, so a
    /// deployment that narrows the list can see what it dropped instead of wondering.
    private bool Reachable(ConnectionSpec spec)
    {
        if (_hosts?.Refuse(spec.Engine, spec.ConnectionString) is not { } refusal) return true;

        lock (_reported)
            if (_reported.Add(spec.Id))
                _log?.LogWarning("connection {Name} is not offered: {Why}", spec.Name, refusal);

        return false;
    }

    /// A connection by its id, or — when nothing has that id — by its name.
    ///
    /// Everything a person or a deployment writes down says the name: a dashboard tile, a schedule
    /// file, a seed. Ids are the studio's own and are checked first, so a name can never shadow
    /// one. Three callers used to do this by hand, and the fourth forgot.
    public ConnectionSpec? Find(string idOrName) =>
        All().FirstOrDefault(c => c.Id == idOrName)
        ?? All().FirstOrDefault(c => c.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public static ConnectionDto ToDto(ConnectionSpec spec) => new(
        spec.Id, spec.Name, spec.Engine, spec.ReadOnly, spec.Color, spec.Group,
        spec.Source.ToString(), Summarize(spec), spec.Tunnel is not null,
        EntraConnectionString.WantsAPerson(spec.ConnectionString));

    /// A human-readable target for the connection list — host and database only, never a secret.
    private static string Summarize(ConnectionSpec spec)
    {
        // A bucket's connection string is a URL, and the useful half of it is the container and the
        // prefix. Everything after the '?' is credentials and never shown.
        if (spec.Engine.Equals("storage", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var target = Storage.StorageUrl.Parse(spec.ConnectionString);
                var where = target.Prefix.Length == 0
                    ? target.Container
                    : $"{target.Container}/{target.Prefix}";

                return target.Account is { Length: > 0 } account ? $"{account}/{where}" : where;
            }
            catch (FormatException)
            {
                // A URL the parser refuses is still a connection somebody has to see in the list.
                return spec.Engine;
            }
        }

        var parts = spec.ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string? Value(params string[] keys) => parts
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2 && keys.Contains(kv[0].Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(kv => kv[1].Trim())
            .FirstOrDefault();

        var host = Value("Host", "Server", "Data Source");
        var database = Value("Database", "Initial Catalog");
        return (host, database) switch
        {
            (null, null) => spec.Engine,
            (not null, null) => host!,
            (null, not null) => database!,
            _ => $"{host}/{database}",
        };
    }
}
