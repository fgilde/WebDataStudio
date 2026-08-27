using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Drivers.Storage;

/// The statements that put DuckDB in a position to read a bucket.
///
/// `INSTALL` wants the internet, and the container this runs in usually has none — so the image
/// carries the two extensions and a session is told to take them from there and from nowhere else.
/// Where no bundle is staged (a developer's machine), the session installs them itself.
public static class DuckDbExtensions
{
    public const string DirectoryVariable = "WDS_DUCKDB_EXTENSION_DIR";

    private const string ImageDirectory = "/opt/duckdb/extensions";

    /// Where the bundled extensions are, or null if nothing is staged.
    public static string? BundledDirectory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } given
            ? given
            : Directory.Exists(ImageDirectory) ? ImageDirectory : null;

    /// What a session runs before its first query. A folder needs nothing: the engine reads a path
    /// on its own.
    public static IReadOnlyList<string> Preamble(StorageProvider provider, string? directory)
    {
        if (provider == StorageProvider.Local) return [];

        var needed = provider == StorageProvider.AzureBlob
            // The azure extension speaks `az://`; httpfs is still what serves an HTTP URL.
            ? new[] { "httpfs", "azure" }
            : ["httpfs"];

        var statements = new List<string>();

        if (directory is { Length: > 0 })
        {
            statements.Add($"SET extension_directory='{directory.Replace("'", "''")}'");
            // The bundle is the only source. A missing file then says so, instead of the session
            // quietly reaching for a network that is not there and timing out.
            statements.Add("SET autoinstall_known_extensions=false");
            statements.Add("SET autoload_known_extensions=false");
        }
        else
        {
            statements.AddRange(needed.Select(name => $"INSTALL {name}"));
        }

        statements.AddRange(needed.Select(name => $"LOAD {name}"));
        return statements;
    }
}
