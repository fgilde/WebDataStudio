using DuckDB.NET.Data;
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

    /// Installs the storage extensions into `directory`, using this application's own DuckDB.
    ///
    /// The image build does this once, with a network, so that no session ever needs one. Doing it
    /// through the engine that will later load them is what keeps the versions in step: an
    /// extension built for another DuckDB refuses to load, and a version number written down in a
    /// Dockerfile drifts the moment the package is updated.
    public static async Task<string> StageAsync(string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);

        await using var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);

        foreach (var statement in new[]
                 {
                     $"SET extension_directory='{directory.Replace("'", "''")}'",
                     "INSTALL httpfs", "INSTALL azure",
                     // Loaded as well, so a build fails here rather than a bucket failing later.
                     "LOAD httpfs", "LOAD azure",
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var report = connection.CreateCommand();
        report.CommandText =
            "SELECT extension_name || ' ' || coalesce(extension_version, '?') FROM duckdb_extensions() "
            + "WHERE extension_name IN ('httpfs', 'azure') AND loaded ORDER BY extension_name";

        var loaded = new List<string>();
        await using var reader = await report.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) loaded.Add(reader.GetString(0));

        return string.Join(", ", loaded);
    }

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
