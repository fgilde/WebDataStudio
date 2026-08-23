using System.Globalization;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

public sealed record PlanSummary(double TotalCost, double MaxNodeCost, int NodeCount, PlanNode? Hottest);

public static class PlanRules
{
    private const double ManyRows = 10_000;
    private const double EstimateDriftFactor = 10;

    /// A nested loop below this is the right shape and saying otherwise is noise.
    private const double ManyLoopedRows = 100_000;

    /// Engine-independent: every rule reads only the normalised plan fields, so a new driver gets
    /// the whole rule set for free the moment it can produce a PlanNode tree.
    public static IReadOnlyList<AnalyzeFinding> Evaluate(PlanNode root)
    {
        var findings = new List<AnalyzeFinding>();
        Walk(root, null, findings);
        return findings;
    }

    private static void Walk(PlanNode node, PlanNode? parent, List<AnalyzeFinding> findings)
    {
        var relation = node.Detail is { Length: > 0 } ? $" on {node.Detail}" : "";

        if (IsScan(node) && (node.EstimatedRows ?? 0) > ManyRows)
            findings.Add(new AnalyzeFinding("missing-index", "warning",
                $"Sequential scan{relation}",
                $"{node.Operation}{relation} reads about {Rows(node.EstimatedRows)} rows. " +
                "An index on the filtered or joined columns would let the engine skip most of them.",
                null));

        if (IsNestedLoop(node) && node.Children.Count > 1 && IsScan(node.Children[1]))
            findings.Add(new AnalyzeFinding("nested-loop-scan", "warning",
                "Nested loop over a scan",
                "The inner side of a nested loop is scanned once per outer row. " +
                "An index on the join column turns this into an index lookup.",
                null));

        if (node.ActualRows is { } actual && node.EstimatedRows is { } estimated && estimated > 0
            && actual > estimated * EstimateDriftFactor)
            findings.Add(new AnalyzeFinding("stale-statistics", "warning",
                $"Row estimate is off{relation}",
                $"The planner expected {Rows(estimated)} rows but got {Rows(actual)}. " +
                "Stale statistics make the planner pick the wrong strategy.",
                node.Detail is { Length: > 0 } ? $"ANALYZE {node.Detail};" : null));

        // Spilling to disk is the engine saying it did not get the memory it wanted. It is in the
        // detail rather than in a warning, so nothing else here would notice it.
        if (SpilledToDisk(node))
            findings.Add(new AnalyzeFinding("spilled-to-disk", "warning",
                $"{node.Operation} spilled to disk",
                $"{node.Operation} did not get the memory it wanted and used disk instead " +
                "(work_mem on PostgreSQL, sort_buffer_size on MySQL). More memory, or fewer rows " +
                "reaching this node, turns it back into an in-memory operation.",
                null));

        // A nested loop can be the wrong shape without an inner scan: enough rows through it and a
        // hash or merge join is what the planner should have chosen.
        if (IsNestedLoop(node) && (node.ActualRows ?? node.EstimatedRows) is { } looped
            && looped >= ManyLoopedRows)
            findings.Add(new AnalyzeFinding("nested-loop-rows", "warning",
                "Nested loop over many rows",
                $"The loop carries about {Rows(looped)} rows. A hash or merge join is usually the " +
                "shape for that many; the planner picks a loop when it expects far fewer.",
                null));

        foreach (var warning in node.Warnings)
            findings.Add(new AnalyzeFinding("plan-warning", warning.Contains("spill", StringComparison.OrdinalIgnoreCase)
                ? "warning" : "info",
                $"{node.Operation}: {warning}",
                $"The engine reported this about {node.Operation}{relation}.",
                null));

        _ = parent;
        foreach (var child in node.Children) Walk(child, node, findings);
    }

    // Findings are read by people on every locale; the server must not stamp its own into them.
    private static string Rows(double? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";

    private static bool IsScan(PlanNode node) =>
        node.Operation.Contains("Seq Scan", StringComparison.OrdinalIgnoreCase)
        || node.Operation.Contains("Table Scan", StringComparison.OrdinalIgnoreCase)
        || node.Operation.Contains("Clustered Index Scan", StringComparison.OrdinalIgnoreCase)
        || node.Operation.Contains("full table scan", StringComparison.OrdinalIgnoreCase);

    /// Every engine says it differently in the detail line — "Disk:", "external merge", "spill" —
    /// and none of them says it in a field of its own.
    private static bool SpilledToDisk(PlanNode node)
    {
        if (node.Detail is not { Length: > 0 } detail) return false;

        var interesting = node.Operation.Contains("Sort", StringComparison.OrdinalIgnoreCase)
            || node.Operation.Contains("Hash", StringComparison.OrdinalIgnoreCase)
            || node.Operation.Contains("Aggregate", StringComparison.OrdinalIgnoreCase);

        return interesting
            && (detail.Contains("disk", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("external", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("spill", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNestedLoop(PlanNode node) =>
        node.Operation.Contains("Nested Loop", StringComparison.OrdinalIgnoreCase);
}

public static class PlanSummaryBuilder
{
    /// The heat map divides each node's cost by MaxNodeCost, so the summary carries what the UI
    /// needs without walking the tree twice.
    public static PlanSummary Summarize(PlanNode root)
    {
        var count = 0;
        var max = 0d;
        PlanNode? hottest = null;

        void Walk(PlanNode node)
        {
            count++;
            var cost = node.EstimatedCost ?? 0;
            if (cost > max) { max = cost; hottest = node; }
            foreach (var child in node.Children) Walk(child);
        }

        Walk(root);
        return new PlanSummary(root.EstimatedCost ?? max, max, count, hottest);
    }
}
