using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// Whether the studio keeps a copy before a statement that takes everything, and how much of one.
public sealed record SafetyOptions(bool Enabled, int MaxRows)
{
    public static SafetyOptions FromConfiguration(IConfiguration config)
    {
        var enabled = !string.Equals(config["WDS_SAFETY_NET"], "false",
            StringComparison.OrdinalIgnoreCase);

        return new SafetyOptions(enabled,
            int.TryParse(config["WDS_SAFETY_MAX_ROWS"], out var rows) && rows > 0 ? rows : 20_000);
    }
}

/// What was kept, and from what.
public sealed record KeptRows(string Archive, string Table, long Rows, bool Truncated)
{
    public string Describe() => Truncated
        ? $"the first {Rows} rows of {Table} were kept as the archive '{Archive}' — there were more"
        : $"{Rows} row(s) of {Table} were kept as the archive '{Archive}'";
}

/// The rows a statement is about to take, kept first.
///
/// The studio already warns about `DELETE` with no `WHERE` before it runs, and already has one step
/// of undo for cell edits. Between those two is the case that actually ruins an afternoon: the
/// statement ran, it took every row, and the undo was never about statements.
///
/// So for exactly the statements that take *everything* — a `DELETE` or an `UPDATE` with no `WHERE`,
/// a `TRUNCATE` — the table is read into an archive first. The archive is a file the studio already
/// knows how to list, reopen as a grid and script back out as inserts, which is what makes this a way
/// back rather than a comfort.
///
/// Not for a statement with a `WHERE`: that one is somebody being specific, and reading a table
/// nobody asked to read is its own kind of surprise.
public static partial class SafetyNet
{
    [GeneratedRegex(@"^\s*UPDATE\s+(?<target>[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Update();

    [GeneratedRegex(@"^\s*DELETE\s+(?:FROM\s+)?(?<target>[^\s;]+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Delete();

    [GeneratedRegex(@"^\s*TRUNCATE\s+(?:TABLE\s+)?(?<target>[^\s;]+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Truncate();

    [GeneratedRegex(@"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Where();

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"'(?:[^']|'')*'", RegexOptions.Singleline)]
    private static partial Regex Strings();

    /// The tables a script is about to empty, in the order the statements appear. Empty for a script
    /// that is specific about what it changes.
    public static IReadOnlyList<string> Sweeping(string sql, SqlDialect dialect)
    {
        var tables = new List<string>();

        foreach (var statement in StatementSplitter.Split(sql, dialect))
        {
            // Comments and string literals first: "-- DELETE everything" is a comment, and a WHERE
            // inside a literal is not a clause.
            var text = Strings().Replace(Comments().Replace(statement.Text, " "), "''");

            if (Truncate().Match(text) is { Success: true } truncate)
            {
                Add(tables, truncate.Groups["target"].Value);
                continue;
            }

            if (Where().IsMatch(text)) continue;

            if (Update().Match(text) is { Success: true } update)
                Add(tables, update.Groups["target"].Value);
            else if (Delete().Match(text) is { Success: true } delete)
                Add(tables, delete.Groups["target"].Value);
        }

        return tables;
    }

    private static void Add(List<string> tables, string target)
    {
        var name = target.Trim().TrimEnd(';');

        if (name.Length > 0 && !tables.Contains(name, StringComparer.OrdinalIgnoreCase))
            tables.Add(name);
    }

    /// Reads each of those tables into an archive. One archive per table, named after it and the
    /// minute, so two runs of the same mistake do not overwrite each other.
    public static async Task<IReadOnlyList<KeptRows>> KeepAsync(
        IDbDriver driver, IDbSession session, Archives archives, MaskPolicy policy,
        IReadOnlyList<string> tables, SafetyOptions options, int timeoutSeconds,
        CancellationToken ct)
    {
        if (!archives.Available || tables.Count == 0) return [];

        var kept = new List<KeptRows>();

        foreach (var table in tables)
        {
            var name = $"{Slug(table)}-before-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
            var request = new ScriptRequest($"SELECT * FROM {table}", options.MaxRows, timeoutSeconds);

            // Masked columns stay masked here as well: an archive is a file that leaves the server's
            // memory, and the rule does not change because the reason is a good one.
            var info = await archives.SaveAsync(name, $"before a sweeping statement on {table}",
                Masking.Stream(driver.ExecuteAsync(session, request, ct), policy, ct),
                options.MaxRows, ct);

            kept.Add(new KeptRows(info.Name, table, info.Rows, info.Rows >= options.MaxRows));
        }

        return kept;
    }

    /// A file name and nothing else, out of a table name that may carry a schema and quotes.
    private static string Slug(string table)
    {
        var cleaned = new string(table
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray())
            .Trim('-');

        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        return cleaned.Length == 0 ? "table" : cleaned;
    }
}
