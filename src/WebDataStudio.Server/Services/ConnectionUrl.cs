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

        // An OData service is addressed by the plain URL of its service root.
        ["http"] = "odata",
        ["https"] = "odata",
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

    /// The engine and the connection string a value in URL form means, or null when it is not a URL
    /// this studio recognises — a provider-native connection string, say, which needs no translating.
    ///
    /// One place for both ways in, because both used to get it wrong in their own way: a
    /// `WDS_CONN_*` variable dropped a URL it could not parse without a word, and the form stored it
    /// as typed and let the driver answer "Format of the initialization string does not conform to
    /// specification".
    public static (string Engine, string ConnectionString)? Read(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0) return null;

        if (EngineFromScheme(text[..scheme]) is not { } engine) return null;

        // A password is whatever somebody chose, and `#`, `?`, `/` and a space all mean something
        // to a URL and nothing to a password. Encoded before parsing rather than demanded of the
        // person typing it: `postgresql://user:pw#@host/db` is what they have in their password
        // manager.
        if (!Uri.TryCreate(Escaped(text, scheme + 3), UriKind.Absolute, out var url)) return null;

        return (engine, ToAdoConnectionString(engine, url));
    }

    /// Percent-encodes what has to be encoded inside a URL's user information — everything between
    /// `://` and the last `@` — and leaves the rest of the URL, and anything already encoded, alone.
    ///
    /// The host is what follows the *last* `@`, so a password may hold one too.
    private static string Escaped(string url, int authority)
    {
        var at = url.LastIndexOf('@');
        if (at < authority) return url;

        var userInfo = url[authority..at];
        var encoded = new System.Text.StringBuilder(userInfo.Length);

        for (var i = 0; i < userInfo.Length; i++)
        {
            var c = userInfo[i];

            // `:` separates the user from the password and stays; an already-escaped `%XX` stays as
            // it is, so somebody who encoded their password by hand does not get it encoded twice.
            if (c == '%' && i + 2 < userInfo.Length
                && Uri.IsHexDigit(userInfo[i + 1]) && Uri.IsHexDigit(userInfo[i + 2]))
            {
                encoded.Append(userInfo, i, 3);
                i += 2;
                continue;
            }

            if (c == ':' || char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')
                encoded.Append(c);
            else
                encoded.Append(Uri.HexEscape(c));
        }

        return url[..authority] + encoded + url[at..];
    }

    public static int DefaultPort(string engine) =>
        DefaultPorts.TryGetValue(engine, out var port) ? port : 0;

    public static string ToAdoConnectionString(string engine, Uri url)
    {
        // Document databases keep their own URL format; their drivers parse it directly.
        if (engine is "mongodb" or "redis") return url.ToString().TrimEnd('/');
        if (engine is "odata") return url.ToString();

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
