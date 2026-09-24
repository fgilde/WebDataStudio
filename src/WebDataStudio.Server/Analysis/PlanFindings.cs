using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

/// What a whole plan document says is wrong: the rules over each statement's tree, plus what the
/// engine itself reported — its missing indexes and its warnings — which no rule has to guess.
public static class PlanFindings
{
    public static IEnumerable<AnalyzeFinding> From(PlanDocument document) =>
        document.Statements.SelectMany(statement =>
            (statement.Root is { } root ? PlanRules.Evaluate(root) : [])
            // Titles tell them apart: findings are de-duplicated by category and title.
            .Concat(statement.MissingIndexes.Select(ddl => new AnalyzeFinding(
                "missing-index", "warning", $"The server asks for {IndexName(ddl)}",
                "SQL Server noted this index while it compiled the plan. It weighs one statement, not the workload: try it before you keep it.",
                ddl)))
            .Concat(statement.Warnings.Select(warning => new AnalyzeFinding(
                "plan", "warning", warning.Length > 120 ? warning[..119] + "…" : warning, warning, null))));

    private static string IndexName(string ddl) =>
        Regex.Match(ddl, @"CREATE INDEX (\S+) ON (\S+)") is { Success: true } m
            ? $"{m.Groups[1].Value} on {m.Groups[2].Value}"
            : "an index";
}
