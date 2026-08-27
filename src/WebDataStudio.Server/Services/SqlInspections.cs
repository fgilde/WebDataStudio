using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// Something worth reading before a statement runs. `Severity` is `warning` for what is probably a
/// mistake and `note` for what is merely worth knowing; neither of them refuses anything.
public sealed record SqlFinding(
    string Id,
    string Severity,
    string Message,
    /// Which statement in the script, counted from one, and the line it starts on.
    int Statement,
    int Line,
    string Excerpt);

/// A read of the SQL before it runs.
///
/// This warns and never refuses. Every finding here is something a person can legitimately mean —
/// an `UPDATE` over a whole table is a real thing to want — so the studio says what it noticed and
/// gets out of the way. Refusing would train people to bypass it.
///
/// It is lexical, not a parser: the goal is the handful of mistakes that cost an afternoon, not a
/// second opinion on SQL semantics.
public static class SqlInspections
{
    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;

    private static readonly Regex Comments = new(@"--[^\n]*|/\*.*?\*/", Options);
    private static readonly Regex Strings = new(@"'(?:[^']|'')*'", Options);
    private static readonly Regex Update = new(@"^\s*UPDATE\s+(?<target>[^\s]+)", Options);
    private static readonly Regex Delete = new(@"^\s*DELETE\s+(?:FROM\s+)?(?<target>[^\s;]+)", Options);
    private static readonly Regex Where = new(@"\bWHERE\b", Options);
    private static readonly Regex AlwaysTrue =
        new(@"\bWHERE\s+(?:1\s*=\s*1|true)\s*(?:;|$|\bAND\b|\bOR\b)", Options);
    private static readonly Regex EqualsNull = new(@"(?:=|<>|!=)\s*NULL\b", Options);
    private static readonly Regex Truncate = new(@"^\s*TRUNCATE\b", Options);
    private static readonly Regex Drop = new(@"^\s*DROP\s+(?<what>TABLE|SCHEMA|DATABASE|VIEW)\b", Options);
    private static readonly Regex FromClause =
        new(@"\bFROM\s+(?<sources>.*?)(?:\bWHERE\b|\bGROUP\b|\bORDER\b|\bHAVING\b|\bLIMIT\b|\bWINDOW\b|;|$)",
            Options);
    private static readonly Regex JoinWithoutOn =
        new(@"\b(?:INNER\s+|LEFT\s+|RIGHT\s+|FULL\s+|OUTER\s+|CROSS\s+|NATURAL\s+)*JOIN\b(?<rest>(?:(?!\bJOIN\b).)*?)(?:\bWHERE\b|\bGROUP\b|\bORDER\b|\bJOIN\b|;|$)",
            Options);

    public static IReadOnlyList<SqlFinding> Inspect(string sql, SqlDialect dialect)
    {
        var findings = new List<SqlFinding>();
        var statements = StatementSplitter.Split(sql, dialect);

        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];
            // Comments and string literals are blanked first: "-- DELETE everything" is a comment,
            // and 'WHERE' inside a literal is not a clause.
            var text = Blank(statement.Text);
            var line = LineOf(sql, statement.Text);
            var number = index + 1;

            void Add(string id, string severity, string message) =>
                findings.Add(new SqlFinding(id, severity, message, number, line, Excerpt(statement.Text)));

            var hasWhere = Where.IsMatch(text);

            if (Update.Match(text) is { Success: true } update && !hasWhere)
                Add("update-without-where", "warning",
                    $"This UPDATE has no WHERE: every row in {update.Groups["target"].Value} is changed.");

            if (Delete.Match(text) is { Success: true } delete && !hasWhere)
                Add("delete-without-where", "warning",
                    $"This DELETE has no WHERE: every row in {delete.Groups["target"].Value} goes.");

            if (AlwaysTrue.IsMatch(text) && (Update.IsMatch(text) || Delete.IsMatch(text)))
                Add("where-always-true", "warning",
                    "The WHERE is always true, so it filters nothing.");

            if (EqualsNull.IsMatch(text))
                Add("equals-null", "warning",
                    "= NULL is never true. IS NULL and IS NOT NULL are the comparisons that work.");

            if (Truncate.IsMatch(text))
                Add("truncate", "warning",
                    "TRUNCATE empties the table and, on most engines, cannot be rolled back.");

            if (Drop.Match(text) is { Success: true } drop)
                Add("drop", "warning",
                    $"DROP {drop.Groups["what"].Value.ToUpperInvariant()} removes it and everything in it.");

            foreach (var finding in CrossProducts(text)) Add(finding.Id, finding.Severity, finding.Message);
        }

        return findings;
    }

    /// Two shapes of accidental cross product: a comma-separated FROM with nothing joining the
    /// sources, and a JOIN with no ON.
    private static IEnumerable<(string Id, string Severity, string Message)> CrossProducts(string text)
    {
        if (FromClause.Match(text) is { Success: true } from)
        {
            var sources = SplitSources(from.Groups["sources"].Value);

            if (sources.Count > 1 && !Where.IsMatch(text) && !text.Contains("JOIN", StringComparison.OrdinalIgnoreCase))
                yield return ("cross-product", "warning",
                    $"{sources.Count} tables in FROM with nothing joining them: every row is paired "
                    + "with every other. Add a join condition, or say CROSS JOIN if that is the intent.");
        }

        foreach (Match join in JoinWithoutOn.Matches(text))
        {
            var rest = join.Groups["rest"].Value;

            if (rest.Contains(" ON ", StringComparison.OrdinalIgnoreCase)
                || rest.Contains("USING", StringComparison.OrdinalIgnoreCase)
                || join.Value.Contains("CROSS", StringComparison.OrdinalIgnoreCase)
                || join.Value.Contains("NATURAL", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return ("join-without-on", "warning",
                "A JOIN with no ON or USING is a cross product. CROSS JOIN says that on purpose.");
        }
    }

    /// The comma-separated sources of a FROM, ignoring commas inside brackets — a function call in a
    /// FROM is one source, not three.
    private static List<string> SplitSources(string clause)
    {
        var sources = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();

        foreach (var character in clause)
        {
            switch (character)
            {
                case '(': depth++; break;
                case ')': depth--; break;
                case ',' when depth == 0:
                    sources.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
            }

            current.Append(character);
        }

        if (current.ToString().Trim() is { Length: > 0 } last) sources.Add(last);

        return sources.Where(source => source.Length > 0).ToList();
    }

    /// Comments and string literals replaced by spaces of the same length, so every offset still
    /// lines up with the original text.
    private static string Blank(string sql)
    {
        var blanked = Comments.Replace(sql, match => new string(' ', match.Length));
        return Strings.Replace(blanked, match => new string(' ', match.Length));
    }

    private static int LineOf(string script, string statement)
    {
        var at = script.IndexOf(statement.Trim(), StringComparison.Ordinal);
        return at < 0 ? 1 : script[..at].Count(character => character == '\n') + 1;
    }

    private static string Excerpt(string statement)
    {
        var flat = Regex.Replace(statement.Trim(), @"\s+", " ");
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }
}
