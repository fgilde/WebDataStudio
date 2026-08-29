namespace WebDataStudio.Server.Services;

/// The paths one setting names.
///
/// A deployment used to get one folder per setting: the export templates its repository holds, *or*
/// the ones an app host wrote — never both. Every one of these settings takes a list now, separated
/// the way a PATH is: `WDS_SAVED_QUERIES_DIR=/data/queries;/data/queries-inline`. One path is still
/// one path, so nothing that worked before reads differently.
public static class ConfiguredPaths
{
    /// Both separators are accepted: `;` is what Windows and these settings use, `:` is what a Unix
    /// PATH uses — and a Windows drive letter is why `:` alone would be wrong.
    public static IReadOnlyList<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        return value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(SplitUnix)
            .Where(part => part.Length > 0)
            .ToList();
    }

    /// `/data/a:/data/b` is two paths; `C:\data` is one.
    private static IEnumerable<string> SplitUnix(string part)
    {
        if (!part.Contains(':')) return [part];

        // A drive letter is a single character before the colon, and nothing else is.
        if (part.Length > 1 && char.IsLetter(part[0]) && part[1] == ':') return [part];

        return part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// The first path that exists as a file or a folder, for a setting that can only use one.
    public static string? First(string? value) =>
        Split(value).FirstOrDefault(path => File.Exists(path) || Directory.Exists(path))
        ?? Split(value).FirstOrDefault();

    /// Every file the paths hold, in the order the paths were given: a file counts as itself, a
    /// folder as everything in it that matches.
    public static IEnumerable<string> Files(string? value, string pattern,
        SearchOption depth = SearchOption.AllDirectories)
    {
        foreach (var path in Split(value))
        {
            if (File.Exists(path))
            {
                yield return path;
                continue;
            }

            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, pattern, depth)
                .OrderBy(file => file, StringComparer.Ordinal))
                yield return file;
        }
    }
}
