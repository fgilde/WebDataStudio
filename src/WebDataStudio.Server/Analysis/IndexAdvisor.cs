using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

public enum PredicateKind { Equality, Range, Join, OrderBy, GroupBy }

public sealed record PredicateRef(string Table, string Column, PredicateKind Kind);

/// A tokenising scanner, not a parser. It strips strings and comments and then reads the clauses it
/// recognises. A missed predicate costs nothing — a suggestion is a hint — while a wrong parse
/// would produce a confidently wrong index, so anything ambiguous is skipped.
public static partial class PredicateExtractor
{
    public static IReadOnlyList<PredicateRef> Extract(string sql)
    {
        var clean = Strip(sql);
        var aliases = Aliases(clean);
        var found = new List<PredicateRef>();

        string Resolve(string qualifier) =>
            aliases.TryGetValue(qualifier.ToLowerInvariant(), out var table) ? table : qualifier;

        // WHERE / AND / ON comparisons
        foreach (Match match in ComparisonPattern().Matches(clean))
        {
            var leftQualifier = match.Groups["lq"].Value;
            var leftColumn = match.Groups["lc"].Value;
            var op = match.Groups["op"].Value;
            var rightQualifier = match.Groups["rq"].Value;
            var rightColumn = match.Groups["rc"].Value;

            var isJoin = rightColumn.Length > 0;
            var kind = isJoin ? PredicateKind.Join
                : op is "=" ? PredicateKind.Equality
                : PredicateKind.Range;

            var leftTable = leftQualifier.Length > 0 ? Resolve(leftQualifier) : SoleTable(aliases);
            if (leftTable is not null) found.Add(new PredicateRef(leftTable, leftColumn, kind));

            if (isJoin)
            {
                var rightTable = rightQualifier.Length > 0 ? Resolve(rightQualifier) : SoleTable(aliases);
                if (rightTable is not null) found.Add(new PredicateRef(rightTable, rightColumn, PredicateKind.Join));
            }
        }

        foreach (var (pattern, kind) in new[]
                 {
                     (OrderByPattern(), PredicateKind.OrderBy),
                     (GroupByPattern(), PredicateKind.GroupBy),
                 })
        {
            var match = pattern.Match(clean);
            if (!match.Success) continue;

            foreach (var part in match.Groups["cols"].Value.Split(','))
            {
                var token = part.Trim().Split(' ')[0];
                if (token.Length == 0) continue;

                var pieces = token.Split('.');
                var column = pieces[^1];
                var table = pieces.Length > 1 ? Resolve(pieces[0]) : SoleTable(aliases);
                if (table is not null) found.Add(new PredicateRef(table, column, kind));
            }
        }

        return found;
    }

    /// Alias to table name, plus every table mapped to itself.
    public static Dictionary<string, string> Aliases(string sql)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in FromJoinPattern().Matches(sql))
        {
            var table = match.Groups["table"].Value;
            var alias = match.Groups["alias"].Value;
            if (table.Length == 0) continue;

            aliases[table] = table;
            if (alias.Length > 0 && !Keywords.Contains(alias)) aliases[alias] = table;
        }

        return aliases;
    }

    private static string? SoleTable(Dictionary<string, string> aliases)
    {
        var tables = aliases.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Without a qualifier the column is only unambiguous when exactly one table is in play.
        return tables.Count == 1 ? tables[0] : null;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "where", "group", "order", "having", "limit", "offset", "set", "values", "inner",
        "left", "right", "full", "outer", "cross", "join", "as", "using", "select", "and", "or",
    };

    /// Removes string literals and comments so their contents cannot look like predicates.
    private static string Strip(string sql)
    {
        var result = StringLiteralPattern().Replace(sql, "''");
        result = LineCommentPattern().Replace(result, " ");
        return BlockCommentPattern().Replace(result, " ");
    }

    [GeneratedRegex(@"'(?:[^']|'')*'")] private static partial Regex StringLiteralPattern();
    [GeneratedRegex(@"--[^\n]*")] private static partial Regex LineCommentPattern();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"\b(?:from|join|update|into)\s+(?:[A-Za-z_][\w$]*\.)?(?<table>[A-Za-z_][\w$]*)(?:\s+(?:as\s+)?(?<alias>[A-Za-z_][\w$]*))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex FromJoinPattern();

    [GeneratedRegex(@"(?:(?<lq>[A-Za-z_][\w$]*)\.)?(?<lc>[A-Za-z_][\w$]*)\s*(?<op>=|<>|!=|<=|>=|<|>|\blike\b|\bbetween\b)\s*(?:(?<rq>[A-Za-z_][\w$]*)\.(?<rc>[A-Za-z_][\w$]*)\b(?!\s*\())?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonPattern();

    [GeneratedRegex(@"\border\s+by\s+(?<cols>[^)]+?)(?:\blimit\b|\boffset\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex OrderByPattern();

    [GeneratedRegex(@"\bgroup\s+by\s+(?<cols>[^)]+?)(?:\bhaving\b|\border\b|\blimit\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex GroupByPattern();
}

public static class IndexAdvisor
{
    public static IReadOnlyList<AnalyzeFinding> Suggest(string sql, PlanNode? plan,
        IReadOnlyDictionary<string, ObjectDetail> tables, SqlDialect dialect)
    {
        var predicates = PredicateExtractor.Extract(sql);
        var findings = new List<AnalyzeFinding>();
        // null means "no plan was supplied, judge from the SQL alone"; an empty set means the plan
        // scans nothing, which is a different statement and must not be treated as "anything goes".
        var scanned = plan is null ? null : ScannedRelations(plan);

        foreach (var group in predicates.GroupBy(p => p.Table, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryGetTable(tables, group.Key, out var detail)) continue;

            // Equality first, then range: that is the order an index can actually use.
            var equality = Distinct(group.Where(p => p.Kind is PredicateKind.Equality or PredicateKind.Join));
            var range = Distinct(group.Where(p => p.Kind == PredicateKind.Range));
            var ordering = Distinct(group.Where(p => p.Kind is PredicateKind.OrderBy or PredicateKind.GroupBy));

            var columns = equality.Concat(range).Concat(ordering).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (columns.Count == 0) continue;

            if (AlreadyIndexed(detail, columns[0])) continue;

            if (scanned is not null && !scanned.Contains(group.Key)) continue;

            var name = $"ix_{group.Key}_{string.Join("_", columns).ToLowerInvariant()}";
            var statement =
                $"CREATE INDEX {dialect.QuoteIdentifier(name)} ON {dialect.QuoteIdentifier(group.Key)} " +
                $"({string.Join(", ", columns.Select(dialect.QuoteIdentifier))});";

            findings.Add(new AnalyzeFinding("missing-index", "warning",
                $"Index suggestion for {group.Key}",
                $"The query filters or joins {group.Key} on {string.Join(", ", columns)} and no index " +
                "leads with that column, so the engine has to read the whole table.",
                statement));
        }

        return findings;
    }

    private static IEnumerable<string> Distinct(IEnumerable<PredicateRef> predicates) =>
        predicates.Select(p => p.Column).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool TryGetTable(IReadOnlyDictionary<string, ObjectDetail> tables, string name,
        out ObjectDetail detail)
    {
        foreach (var (key, value) in tables)
        {
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            detail = value;
            return true;
        }

        detail = null!;
        return false;
    }

    private static bool AlreadyIndexed(ObjectDetail detail, string column) =>
        detail.Indexes.Any(i => i.Columns.Count > 0
                                && i.Columns[0].Equals(column, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> ScannedRelations(PlanNode root)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(PlanNode node)
        {
            // Only scans that read the whole relation count. An index scan is already the fix.
            if (node.Detail is { Length: > 0 } relation
                && node.Operation.Contains("scan", StringComparison.OrdinalIgnoreCase)
                && !node.Operation.Contains("Index Scan", StringComparison.OrdinalIgnoreCase)
                && !node.Operation.Contains("Index Seek", StringComparison.OrdinalIgnoreCase))
                found.Add(relation);
            foreach (var child in node.Children) Walk(child);
        }

        Walk(root);
        return found;
    }
}
