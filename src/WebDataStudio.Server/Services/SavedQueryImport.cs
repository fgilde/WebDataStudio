using System.Text.RegularExpressions;

namespace WebDataStudio.Server.Services;

/// Where team queries come from, if anywhere. A folder of `.sql` files is a thing a repository can
/// hold and a review can catch; the studio's own saved queries are per workspace.
public sealed record SavedQueryImportOptions(bool Configured, string Directory)
{
    public static SavedQueryImportOptions FromConfiguration(IConfiguration config)
    {
        var directory = config["WDS_SAVED_QUERIES_DIR"]?.Trim();

        return string.IsNullOrEmpty(directory)
            ? new SavedQueryImportOptions(false, "")
            : new SavedQueryImportOptions(true, directory);
    }
}

/// Imports `.sql` files from a directory as saved queries, so a stack can ship the five queries
/// everybody on the team needs instead of pasting them into a chat.
///
/// Idempotent: a file already imported under the same name is replaced rather than duplicated, so a
/// restart does not grow the list.
public sealed partial class SavedQueryImport(
    SavedQueryImportOptions options, WorkspaceStore workspace, ConnectionRegistry registry,
    ILogger<SavedQueryImport> log)
{
    /// A file may name the connection it belongs to and the folder it should appear in. Both are
    /// optional, and both are comments, so the file is still a file the database accepts.
    [GeneratedRegex(@"^\s*--\s*wds:connection\s+(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ConnectionHeader();

    [GeneratedRegex(@"^\s*--\s*wds:folder\s+(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FolderHeader();

    public bool Configured => options.Configured;

    /// Imports everything in the directory. Returns how many queries were written.
    public int Import()
    {
        if (!options.Configured) return 0;

        if (!Directory.Exists(options.Directory))
        {
            log.LogWarning("the saved-query directory {Directory} does not exist", options.Directory);
            return 0;
        }

        var existing = SafeList();
        var written = 0;

        foreach (var file in Directory.EnumerateFiles(options.Directory, "*.sql", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(500))
        {
            try
            {
                var sql = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(sql)) continue;

                var name = Path.GetFileNameWithoutExtension(file);
                var folder = FolderOf(file, sql);
                var connection = ConnectionOf(sql);

                // Same name, same folder: the same query, so it is replaced rather than added again.
                var id = existing.FirstOrDefault(query =>
                    query.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(query.Folder, folder, StringComparison.OrdinalIgnoreCase))?.Id;

                workspace.SaveQuery(new SavedQuery(
                    id ?? "", name, folder, sql, connection, DateTimeOffset.UtcNow));

                written++;
            }
            catch (Exception e)
            {
                // One unreadable file must not stop the other four.
                log.LogWarning(e, "could not import {File}", file);
            }
        }

        if (written > 0)
            log.LogInformation("imported {Count} saved queries from {Directory}", written, options.Directory);

        return written;
    }

    /// The folder from the header if the file names one, else the subdirectory it sits in, else none.
    private string? FolderOf(string file, string sql)
    {
        var header = FolderHeader().Match(sql);
        if (header.Success) return header.Groups["value"].Value.Trim();

        var relative = Path.GetRelativePath(options.Directory, Path.GetDirectoryName(file) ?? "");
        return relative is "." or "" ? null : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// The connection id for the name in the header, when that name is a connection this studio has.
    /// A query that names one nobody knows is still worth having — it just opens unbound.
    private string? ConnectionOf(string sql)
    {
        var header = ConnectionHeader().Match(sql);
        if (!header.Success) return null;

        var name = header.Groups["value"].Value.Trim();

        return registry.All().FirstOrDefault(spec =>
            spec.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || spec.Id.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private IReadOnlyList<SavedQuery> SafeList()
    {
        try
        {
            return workspace.ListSavedQueries();
        }
        catch (Exception)
        {
            // No workspace, no idea what is already there: importing is still better than not.
            return [];
        }
    }
}

/// Imports at start, once, before anybody has had time to open the saved-queries panel.
public sealed class SavedQueryImportStartup(
    SavedQueryImport import, ILogger<SavedQueryImportStartup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!import.Configured) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            import.Import();
        }
        catch (OperationCanceledException)
        {
            // Shutting down first is not a failure.
        }
        catch (Exception e)
        {
            log.LogWarning(e, "the saved-query import failed");
        }
    }
}
