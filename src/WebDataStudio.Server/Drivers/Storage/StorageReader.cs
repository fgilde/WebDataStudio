namespace WebDataStudio.Server.Drivers.Storage;

/// Which DuckDB function reads which file, decided by the name.
///
/// A `.zip` has no reader, and that is an answer rather than a failure: the menu then offers a
/// preview and a download instead of a query that would go wrong.
public static class StorageReader
{
    /// The reader call for a URI, or null when nothing here reads it. The URI may be a glob, in
    /// which case the pattern's own suffix decides — `exports/*.parquet` is one table.
    public static string? Call(string uri)
    {
        var reader = ReaderFor(Suffix(uri));
        if (reader is null) return null;

        // DuckDB handles the compression itself; the reader is chosen by what is under it.
        return $"{reader}('{uri.Replace("'", "''")}')";
    }

    /// Whether the studio can offer to query this at all.
    public static bool CanRead(string name) => ReaderFor(Suffix(name)) is not null;

    private static string? ReaderFor(string suffix) => suffix switch
    {
        ".parquet" => "read_parquet",
        // AUTO_DETECT is the default in current DuckDB; saying it keeps the call true to what it
        // means if that default ever changes.
        ".csv" or ".tsv" or ".txt" => "read_csv_auto",
        ".json" or ".ndjson" or ".jsonl" => "read_json_auto",
        _ => null,
    };

    /// The extension that decides, with a compression suffix looked through: `a.csv.gz` is a CSV.
    private static string Suffix(string uri)
    {
        var name = uri;
        foreach (var compressed in new[] { ".gz", ".zst", ".bz2" })
            if (name.EndsWith(compressed, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^compressed.Length];
                break;
            }

        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[dot..].ToLowerInvariant();
    }
}
