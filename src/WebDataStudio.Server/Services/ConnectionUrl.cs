namespace WebDataStudio.Server.Services;

/// Translates the URL form accepted in WDS_CONN_&lt;NAME&gt; into the provider-native
/// connection string each ADO.NET driver expects.
public static class ConnectionUrl
{
    private static readonly Dictionary<string, string> Engines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = "postgresql",
        ["postgresql"] = "postgresql",
        ["mysql"] = "mysql",
        ["mariadb"] = "mysql",
        ["sqlserver"] = "sqlserver",
        ["mssql"] = "sqlserver",
        ["sqlite"] = "sqlite",
        ["oracle"] = "oracle",
        ["duckdb"] = "duckdb",
        ["clickhouse"] = "clickhouse",
        ["mongodb"] = "mongodb",
        ["redis"] = "redis",

        // Object storage: one engine, four schemes. The scheme picks the provider inside the driver,
        // which is why they all answer with the same engine id here.
        ["s3"] = "storage",
        ["azblob"] = "storage",
        ["azure"] = "storage",
        ["gs"] = "storage",
        ["gcs"] = "storage",
        ["file"] = "storage",
    };

    private static readonly Dictionary<string, int> DefaultPorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgresql"] = 5432,
        ["mysql"] = 3306,
        ["sqlserver"] = 1433,
        ["oracle"] = 1521,
        ["clickhouse"] = 8123,
        ["mongodb"] = 27017,
        ["redis"] = 6379,
    };

    public static string? EngineFromScheme(string scheme) =>
        Engines.TryGetValue(scheme, out var engine) ? engine : null;

    public static int DefaultPort(string engine) =>
        DefaultPorts.TryGetValue(engine, out var port) ? port : 0;

    public static string ToAdoConnectionString(string engine, Uri url)
    {
        // Document databases keep their own URL format; their drivers parse it directly.
        if (engine is "mongodb" or "redis") return url.ToString().TrimEnd('/');

        // File-backed engines carry a path, not a host.
        if (engine is "sqlite" or "duckdb") return $"Data Source={url.LocalPath}";

        // A storage connection is its URL: the driver parses it into a bucket, a prefix and
        // credentials, and there is no ADO connection string to translate it into.
        if (engine is "storage") return url.ToString();

        var userInfo = url.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var database = url.AbsolutePath.Trim('/');
        var port = url.IsDefaultPort ? DefaultPort(engine) : url.Port;

        return engine switch
        {
            "postgresql" => $"Host={url.Host};Port={port};Database={database};Username={user};Password={password}",
            "mysql" => $"Server={url.Host};Port={port};Database={database};User ID={user};Password={password}",
            "sqlserver" => $"Server={url.Host},{port};Database={database};User Id={user};Password={password};TrustServerCertificate=True",
            "oracle" => $"Data Source={url.Host}:{port}/{database};User Id={user};Password={password}",
            "clickhouse" => $"Host={url.Host};Port={port};Database={database};Username={user};Password={password}",
            _ => throw new NotSupportedException($"no URL mapping for engine '{engine}'"),
        };
    }
}
