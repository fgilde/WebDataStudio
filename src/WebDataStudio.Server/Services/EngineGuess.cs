namespace WebDataStudio.Server.Services;

/// Works out which engine a provider-native connection string belongs to. Used only when the
/// value of a `WDS_CONN_<NAME>` variable is not a URL and no `_ENGINE` variable says so — an
/// orchestrator that knows the resource type should always set `_ENGINE` instead of relying on
/// this.
public static class EngineGuess
{
    public static string? FromConnectionString(string connectionString)
    {
        var value = connectionString.Trim();
        if (value.Length == 0) return null;

        // A few providers hand out their own URL form even inside a connection string.
        if (value.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("mongodb+srv://", StringComparison.OrdinalIgnoreCase)) return "mongodb";
        if (value.StartsWith("redis://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase)) return "redis";

        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.OrdinalIgnoreCase);

        bool Has(params string[] keys) => keys.Any(parts.ContainsKey);
        string? Value(string key) => parts.TryGetValue(key, out var found) ? found : null;

        // Only Npgsql spells the pair Host + Username; MySqlConnector uses User ID with Server.
        if (Has("Host") && Has("Username") && !Has("Protocol")) return "postgresql";
        if (Has("Server") && Has("Uid")) return "mysql";
        if (Has("Initial Catalog") || Has("TrustServerCertificate") || Has("Integrated Security"))
            return "sqlserver";
        if (Has("Server") && Has("User ID", "User Id")) return "mysql";

        if (Value("Data Source") is { } source)
        {
            if (source.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase)
                || source.Equals(":memory:", StringComparison.OrdinalIgnoreCase)) return "sqlite";

            if (source.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase)) return "duckdb";

            // Oracle's easy-connect form: host:port/service.
            if (source.Contains('/') && source.Contains(':') && Has("User Id", "User ID")) return "oracle";
        }

        // A bare host list could be Redis, an Oracle descriptor or a typo. Anything without a
        // recognisable key stays unknown; the caller has to declare the engine.
        return null;
    }
}
