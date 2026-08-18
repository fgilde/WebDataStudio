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
    ];

    private readonly IReadOnlyList<ConnectionSpec> _environment;
    private readonly ConnectionStore _store;
    private readonly bool _forceReadOnly;

    public ConnectionRegistry(IConfiguration config, ConnectionStore store)
    {
        _store = store;
        _environment = EnvironmentConnections.Parse(
            config.AsEnumerable().ToDictionary(kv => kv.Key, kv => kv.Value));
        _forceReadOnly = string.Equals(config["WDS_READONLY"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ConnectionSpec> All() =>
        _environment.Concat(_store.List())
            .Select(c => _forceReadOnly ? c with { ReadOnly = true } : c)
            .ToList();

    public ConnectionSpec? Find(string id) => All().FirstOrDefault(c => c.Id == id);

    public static ConnectionDto ToDto(ConnectionSpec spec) => new(
        spec.Id, spec.Name, spec.Engine, spec.ReadOnly, spec.Color, spec.Group,
        spec.Source.ToString(), Summarize(spec));

    /// A human-readable target for the connection list — host and database only, never a secret.
    private static string Summarize(ConnectionSpec spec)
    {
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
