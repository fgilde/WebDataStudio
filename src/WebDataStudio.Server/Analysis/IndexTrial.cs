using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

/// What a suggested index would do to a plan: measured, not claimed.
public sealed record TrialResult(
    string Index,
    string Created,
    PlanNode Before,
    PlanNode After,
    double? CostBefore,
    double? CostAfter,
    /// What changed, in words: the sentence somebody reads instead of two cost numbers.
    string Verdict,
    /// The index this trial could not remove again, where that happened. Null is the normal case.
    string? LeftBehind);

/// The index advisor writes `CREATE INDEX`; this finds out whether it helps.
///
/// A cost estimate before and after, with the index actually there — because "the planner would
/// probably use it" is the part everybody gets wrong. The index is created under a name of the
/// studio's own making, the plan is asked for again, and then it is dropped: a trial that leaves
/// something behind is a trial nobody runs twice.
///
/// Not in a transaction on purpose. MySQL and Oracle commit DDL whatever a transaction says, so a
/// rollback would be a promise that holds on two engines out of six. Creating and dropping is the
/// same shape everywhere, and the `finally` is the part that matters.
///
/// Refused on a read-only connection and on one marked as production: building an index takes locks
/// and time on the table it is built over, and "it was only a trial" is no comfort at 3am.
public static partial class IndexTrial
{
    [GeneratedRegex(
        @"CREATE\s+(?<unique>UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>[^\s(]+)\s+ON\s+(?<table>[^\s(]+)\s*\((?<columns>[^)]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CreateIndex();

    /// The parts of a suggested index, or null when this is not one. Only `CREATE INDEX` is tried:
    /// the studio has to be able to write the `DROP` for whatever it created.
    public static (string Table, string Columns, bool Unique)? Parse(string ddl)
    {
        if (CreateIndex().Match(ddl) is not { Success: true } match) return null;

        var columns = string.Join(", ",
            match.Groups["columns"].Value.Split(',').Select(part => part.Trim()));

        return (match.Groups["table"].Value.Trim(), columns,
            match.Groups["unique"].Success);
    }

    public static async Task<TrialResult> RunAsync(IDbDriver driver, IDbSession session,
        string sql, string ddl, CancellationToken ct)
    {
        var parsed = Parse(ddl)
            ?? throw new FormatException(
                "this is not a CREATE INDEX, and the studio only tries what it can undo");

        // The studio's own name, so the drop is certain and nothing collides with an index somebody
        // else made.
        var name = $"wds_trial_{Guid.NewGuid():N}"[..24];
        var quoted = driver.Dialect.QuoteIdentifier(name);

        var create = $"CREATE {(parsed.Unique ? "UNIQUE " : "")}INDEX {quoted} "
                     + $"ON {parsed.Table} ({parsed.Columns})";

        var before = await driver.ExplainAsync(session, sql, PlanMode.Estimated, ct);
        string? leftBehind = null;
        PlanNode after;

        await Execute(session, create, ct);

        try
        {
            after = await driver.ExplainAsync(session, sql, PlanMode.Estimated, ct);
        }
        finally
        {
            try
            {
                await Execute(session, $"DROP INDEX {quoted}"
                                       + (driver.Info.Id == "mysql" ? $" ON {parsed.Table}" : ""), ct);
            }
            catch (Exception e)
            {
                // Said rather than swallowed: an index the studio made and could not remove is
                // something a person has to know about.
                leftBehind = $"{name} could not be dropped ({e.Message})";
            }
        }

        var costBefore = Cost(before);
        var costAfter = Cost(after);

        return new TrialResult(name, create, before, after, costBefore, costAfter,
            Describe(before, after, costBefore, costAfter), leftBehind);
    }

    private static async Task Execute(IDbSession session, string sql, CancellationToken ct)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// The plan's own cost, where the engine reports one.
    private static double? Cost(PlanNode plan) => plan.EstimatedCost;

    /// What changed. The operations matter as much as the number: a plan that stopped scanning the
    /// table is the answer even where the engine reports no cost at all.
    public static string Describe(PlanNode before, PlanNode after, double? costBefore,
        double? costAfter)
    {
        var scanned = Scans(before);
        var scansNow = Scans(after);

        if (costBefore is > 0 && costAfter is > 0)
        {
            var change = (costBefore.Value - costAfter.Value) / costBefore.Value * 100;

            if (change >= 5)
                return $"cheaper by {Math.Round(change)}%"
                       + (scanned && !scansNow ? ", and it stopped scanning the table" : "");

            if (change <= -5) return $"more expensive by {Math.Round(-change)}%";

            return scanned && !scansNow
                ? "about the same cost, but it stopped scanning the table"
                : "no real difference — this index is not the answer";
        }

        return scanned && !scansNow
            ? "it stopped scanning the table"
            : "no visible difference in the plan";
    }

    /// Whether anything in the plan reads a whole table.
    private static bool Scans(PlanNode plan) =>
        plan.Operation.Contains("Seq Scan", StringComparison.OrdinalIgnoreCase)
        || plan.Operation.Contains("Table Scan", StringComparison.OrdinalIgnoreCase)
        || plan.Operation.Contains("SCAN TABLE", StringComparison.OrdinalIgnoreCase)
        || plan.Operation.Contains("ALL", StringComparison.Ordinal)
        || plan.Children.Any(Scans);
}
