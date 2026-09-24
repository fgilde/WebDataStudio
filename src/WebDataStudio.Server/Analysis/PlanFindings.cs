using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

/// What a whole plan document says is wrong: the rules over each statement's tree, plus what the
/// engine itself reported — its missing indexes and its warnings — which no rule has to guess.
public static class PlanFindings
{
    public static IEnumerable<AnalyzeFinding> From(PlanDocument document) =>
        document.Statements.SelectMany(statement =>
            (statement.Root is { } root ? PlanRules.Evaluate(root) : [])
            .Concat(statement.MissingIndexes.Select(ddl => new AnalyzeFinding(
                "missing-index", "warning", "The server asks for an index",
                "SQL Server noted this index while it compiled the plan. It weighs one statement, not the workload: try it before you keep it.",
                ddl)))
            .Concat(statement.Warnings.Select(warning => new AnalyzeFinding(
                "plan", "warning", "Plan warning", warning, null))));
}
